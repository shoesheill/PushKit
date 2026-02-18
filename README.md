# PushKit.NET — Complete Implementation Guide

> **Platforms covered:** Android · iOS (FCM + Native APNs) · Web  
> **Stack:** .NET Core 10 · FCM HTTP v1 · APNs HTTP/2 JWT

---

## Table of Contents

1. [Architecture Overview](#1-architecture-overview)
2. [Backend Setup (.NET Core 10)](#2-backend-setup-net-core-10)
3. [Android Implementation](#3-android-implementation)
4. [iOS Implementation](#4-ios-implementation)
5. [Web Implementation](#5-web-implementation)
6. [Complete Registration Flow](#6-complete-registration-flow)
7. [Error Handling & Token Cleanup](#7-error-handling--token-cleanup)
8. [Quick Reference](#8-quick-reference)

---

## 1. Architecture Overview

### The Core Problem — Tokens Look the Same

You **cannot** tell the platform by reading a token. They all look like random strings:

```
FCM Android token:  dK3x9F2mQ8:APA91bHPR...
FCM iOS token:      dK3x9F2mQ8:APA91bHPR...   ← identical format!
FCM Web token:      dK3x9F2mQ8:APA91bHPR...   ← identical format!
APNs iOS token:     a1b2c3d4e5f6789abc...      ← different (hex, 64 chars)
```

**Solution:** The client tells you its platform when registering. You store it in the database alongside the token.

### Platform Routing Table

| Platform | Token Type | Token Format | SDK | Backend Sender |
|---|---|---|---|---|
| Android | FCM token | `dK3x9F2m:APA91b...` | Firebase Android SDK | `IFcmSender` |
| iOS (Firebase) | FCM token | `dK3x9F2m:APA91b...` | Firebase iOS SDK | `IFcmSender` |
| iOS (Native APNs) | APNs token | `a1b2c3d4e5f6...` (hex) | UIKit / UNUserNotificationCenter | `IApnSender` |
| Web | FCM token | `dK3x9F2m:APA91b...` | Firebase JS SDK | `IFcmSender` |

> ⚠️ **Important:** FCM tokens for Android, iOS (Firebase), and Web are indistinguishable by format. Platform **must** be stored in your database.

---

## 2. Backend Setup (.NET Core 10)

### 2.1 Database Model

Add a `DeviceTokens` table. The `Platform` column is what makes routing possible.

```csharp
public sealed class DeviceToken
{
    public string Id             { get; set; } = Guid.NewGuid().ToString();
    public string UserId         { get; set; } = string.Empty;
    public string Token          { get; set; } = string.Empty;
    public DevicePlatform Platform { get; set; }
    public DateTime RegisteredAt { get; set; } = DateTime.UtcNow;
    public DateTime? LastUsedAt  { get; set; }
    public bool IsActive         { get; set; } = true;
}

public enum DevicePlatform
{
    Android,    // FCM token — Firebase SDK on Android
    IosFcm,     // FCM token — Firebase SDK on iOS
    IosApn,     // Native APNs hex token — direct Apple HTTP/2
    Web         // FCM token — Firebase JS SDK in browser
}
```

### 2.2 appsettings.json

```json
{
  "PushKit": {
    "Fcm": {
      "ProjectId": "your-firebase-project-id",
      "ServiceAccountKeyFilePath": "/secrets/firebase.json",
      "MaxRetryAttempts": 3,
      "RetryBaseDelayMs": 500,
      "RequestTimeoutSeconds": 30,
      "BatchParallelism": 100
    },
    "Apn": {
      "P8PrivateKey": "MIGHAgEAMBMGByq...base64keynoheadersnewlines...",
      "P8PrivateKeyId": "ABCDE12345",
      "TeamId":         "FGHIJ67890",
      "BundleId":       "com.yourcompany.yourapp",
      "Environment":    "Production",
      "BatchParallelism": 50
    }
  }
}
```

**For Docker / Kubernetes** — inject credentials via environment variables instead of file:

```bash
export PushKit__Fcm__ServiceAccountJson="$(cat firebase.json)"
export PushKit__Apn__P8PrivateKey="MIGHAgEAMBMGByq..."
```

### 2.3 Program.cs Registration

```csharp
using PushKit.Extensions;

var builder = WebApplication.CreateBuilder(args);

// Option A — from appsettings.json (recommended)
builder.Services.AddPushKit(builder.Configuration);

// Option B — FCM only, inline config
builder.Services.AddFcmSender(opts => {
    opts.ProjectId          = Environment.GetEnvironmentVariable("FIREBASE_PROJECT_ID")!;
    opts.ServiceAccountJson = Environment.GetEnvironmentVariable("FIREBASE_SA_JSON");
});

// Option C — APNs only, inline config
builder.Services.AddApnSender(opts => {
    opts.P8PrivateKey   = Environment.GetEnvironmentVariable("APN_P8_KEY")!;
    opts.P8PrivateKeyId = Environment.GetEnvironmentVariable("APN_KEY_ID")!;
    opts.TeamId         = Environment.GetEnvironmentVariable("APN_TEAM_ID")!;
    opts.BundleId       = "com.yourcompany.app";
    opts.Environment    = ApnEnvironment.Production;
});

builder.Services.AddScoped<SmartPushService>();
```

### 2.4 Token Registration Endpoint

Every client calls this after getting their token. The `platform` field is what makes everything work.

```csharp
app.MapPost("/device/register", async (
    RegisterDeviceRequest req,
    IDeviceTokenRepository repo) =>
{
    await repo.UpsertAsync(new DeviceToken
    {
        UserId   = req.UserId,
        Token    = req.Token,
        Platform = Enum.Parse<DevicePlatform>(req.Platform, ignoreCase: true)
    });
    return Results.Ok(new { registered = true });
});

public record RegisterDeviceRequest(
    string UserId,
    string Token,
    string Platform  // "android" | "ios_fcm" | "ios_apn" | "web"
);
```

### 2.5 Smart Push Service (Platform Router)

This service loads all tokens for a user and automatically routes each one to the correct sender.

```csharp
public sealed class SmartPushService
{
    private readonly IFcmSender _fcm;
    private readonly IApnSender _apn;
    private readonly IDeviceTokenRepository _repo;
    private readonly ILogger<SmartPushService> _logger;

    public SmartPushService(
        IFcmSender fcm, IApnSender apn,
        IDeviceTokenRepository repo,
        ILogger<SmartPushService> logger)
    {
        _fcm    = fcm;
        _apn    = apn;
        _repo   = repo;
        _logger = logger;
    }

    /// <summary>
    /// Sends to ALL devices of a user — routes by Platform automatically.
    /// </summary>
    public async Task SendToUserAsync(
        string userId, string eventType, object payload,
        CancellationToken ct = default)
    {
        var tokens = await _repo.GetActiveByUserAsync(userId);
        var tasks  = tokens.Select(d => SendToDeviceAsync(d, eventType, payload, ct));
        await Task.WhenAll(tasks);
    }

    private async Task SendToDeviceAsync(
        DeviceToken device, string eventType, object payload,
        CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(payload);

        var result = device.Platform switch
        {
            DevicePlatform.Android => await SendFcmAsync(device.Token, eventType, json, ct),
            DevicePlatform.IosFcm  => await SendFcmAsync(device.Token, eventType, json, ct),
            DevicePlatform.Web     => await SendFcmAsync(device.Token, eventType, json, ct),
            DevicePlatform.IosApn  => await SendApnAsync(device.Token, eventType, json, ct),
            _ => throw new ArgumentOutOfRangeException()
        };

        if (result.IsTokenInvalid)
        {
            _logger.LogWarning("Stale token removed — {Platform}, User: {UserId}",
                device.Platform, device.UserId);
            await _repo.DeactivateAsync(device.Id);
        }
    }

    private Task<PushResult> SendFcmAsync(
        string token, string eventType, string payload, CancellationToken ct)
    {
        var msg = PushMessageBuilder.Create()
            .WithData("event",   eventType)
            .WithData("payload", payload)
            .WithAndroid(priority: AndroidPriority.High, ttlSeconds: 86400)
            .Build();

        return _fcm.SendToTokenAsync(token, msg, ct);
    }

    private Task<PushResult> SendApnAsync(
        string token, string eventType, string payload, CancellationToken ct)
    {
        var msg = ApnMessageBuilder.Create()
            .WithAlert("New Update", eventType)
            .WithCustomData("event",   eventType)
            .WithCustomData("payload", payload)
            .WithSound("default")
            .Build();

        return _apn.SendAsync(token, msg, ct);
    }
}
```

---

## 3. Android Implementation

### 3.1 Firebase Project Setup

1. Go to [console.firebase.google.com](https://console.firebase.google.com) → **Add project**
2. Register Android app with your package name (e.g. `com.yourcompany.app`)
3. Download `google-services.json` → place it in `app/` directory
4. Add plugin to **project-level** `build.gradle`:

```groovy
// project/build.gradle
plugins {
    id 'com.google.gms.google-services' version '4.4.0' apply false
}
```

5. Add to **app-level** `build.gradle`:

```groovy
// app/build.gradle
plugins {
    id 'com.google.gms.google-services'
}

dependencies {
    implementation platform('com.google.firebase:firebase-bom:32.7.0')
    implementation 'com.google.firebase:firebase-messaging-ktx'
}
```

### 3.2 Firebase Messaging Service

```kotlin
// PushKitMessagingService.kt
import com.google.firebase.messaging.FirebaseMessagingService
import com.google.firebase.messaging.RemoteMessage

class PushKitMessagingService : FirebaseMessagingService() {

    // Called when a new FCM token is generated (app install or token refresh)
    override fun onNewToken(token: String) {
        super.onNewToken(token)
        sendTokenToBackend(token)
    }

    // Called when app is FOREGROUND and a message arrives
    override fun onMessageReceived(message: RemoteMessage) {
        super.onMessageReceived(message)

        val event   = message.data["event"]
        val payload = message.data["payload"]

        when (event) {
            "ORDER_SHIPPED" -> handleOrderShipped(payload)
            "FLASH_SALE"    -> showFlashSaleDialog(payload)
            "REFRESH_FEED"  -> refreshFeedInBackground()
            else            -> handleGenericEvent(event, payload)
        }
    }

    private fun sendTokenToBackend(token: String) {
        val userId = AuthManager.currentUserId ?: return
        ApiClient.registerDevice(
            token    = token,
            platform = "android",   // ← always hardcode this for Android
            userId   = userId
        )
    }
}
```

### 3.3 Register in AndroidManifest.xml

```xml
<service
    android:name=".PushKitMessagingService"
    android:exported="false">
    <intent-filter>
        <action android:name="com.google.firebase.MESSAGING_EVENT" />
    </intent-filter>
</service>
```

### 3.4 Get Token on App Launch

```kotlin
// MainActivity.kt
import com.google.firebase.messaging.FirebaseMessaging

class MainActivity : AppCompatActivity() {
    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)

        // Refresh token on every launch (handles token rotation)
        FirebaseMessaging.getInstance().token
            .addOnSuccessListener { token ->
                sendTokenToBackend(token, platform = "android")
            }
            .addOnFailureListener { e ->
                Log.e("PushKit", "FCM token fetch failed", e)
            }
    }
}
```

> ℹ️ **Background messages:** When the app is in background and message has **only** a `data` payload (no `notification` block), `onMessageReceived()` is still called. If you add a `notification` block, Android shows it automatically and `onMessageReceived()` is **not** called.

---

## 4. iOS Implementation

You have two options depending on your setup:

| | Option A — Firebase iOS SDK | Option B — Native APNs |
|---|---|---|
| **Token type** | FCM (same as Android) | APNs hex string |
| **Firebase dependency** | Required | None |
| **VoIP / CallKit** | ❌ Not supported | ✅ Supported |
| **Backend sender** | `IFcmSender` | `IApnSender` |
| **Platform value** | `"ios_fcm"` | `"ios_apn"` |
| **Best for** | Already using Firebase | Pure Apple, VoIP, no Firebase |

---

### 4.1 Option A — Firebase iOS SDK

#### Step 1 — Xcode Setup

1. **Swift Package Manager:** File → Add Packages → `https://github.com/firebase/firebase-ios-sdk`
2. Add `FirebaseMessaging` to your target
3. Download `GoogleService-Info.plist` from Firebase Console → drag into Xcode project root
4. In **Signing & Capabilities:** add **Push Notifications**
5. In **Signing & Capabilities:** add **Background Modes** → tick **Remote notifications**

#### Step 2 — AppDelegate.swift

```swift
import UIKit
import Firebase
import FirebaseMessaging
import UserNotifications

@main
class AppDelegate: UIResponder, UIApplicationDelegate, MessagingDelegate,
                   UNUserNotificationCenterDelegate {

    func application(_ application: UIApplication,
        didFinishLaunchingWithOptions launchOptions: [UIApplication.LaunchOptionsKey: Any]?) -> Bool {

        FirebaseApp.configure()
        Messaging.messaging().delegate = self
        UNUserNotificationCenter.current().delegate = self

        // Request permission
        UNUserNotificationCenter.current().requestAuthorization(
            options: [.alert, .badge, .sound]) { granted, _ in
            guard granted else { return }
            DispatchQueue.main.async {
                application.registerForRemoteNotifications()
            }
        }
        return true
    }

    // Firebase gives you a fresh FCM token
    func messaging(_ messaging: Messaging,
        didReceiveRegistrationToken fcmToken: String?) {
        guard let token = fcmToken else { return }

        // Send to YOUR backend with platform = "ios_fcm"
        let userId = AuthManager.shared.currentUserId ?? return
        APIClient.registerDevice(
            token:    token,
            platform: "ios_fcm",   // ← always "ios_fcm" for Firebase iOS path
            userId:   userId
        )
    }

    // Foreground data message handler
    func application(_ application: UIApplication,
        didReceiveRemoteNotification userInfo: [AnyHashable: Any],
        fetchCompletionHandler completionHandler: @escaping (UIBackgroundFetchResult) -> Void) {

        let event   = userInfo["event"] as? String ?? ""
        let payload = userInfo["payload"] as? String ?? "{}"
        handlePushEvent(event: event, payload: payload)
        completionHandler(.newData)
    }
}
```

---

### 4.2 Option B — Native APNs

#### Step 1 — Apple Developer Portal

1. Go to [developer.apple.com](https://developer.apple.com) → **Certificates, Identifiers & Profiles**
2. **Keys** → **+** → enable **Apple Push Notifications service (APNs)** → Continue
3. Download the `.p8` file (**you can only download once — keep it safe!**)
4. Note your **Key ID** (10 chars, shown on key details page)
5. Note your **Team ID** (top-right of developer.apple.com)

#### Step 2 — Extract the P8 Key

Open the `.p8` file in a text editor. It looks like this:

```
-----BEGIN PRIVATE KEY-----
MIGHAgEAMBMGByqGSM49AgEGCCqGSM49AwEHBG0wawIBAQQgXXXXXXXXXXXXXXXX
XXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXoAoGCCqGSM49AwEHoWQDYgAEXXXX
-----END PRIVATE KEY-----
```

Remove the header line, footer line, and **all newlines**. The result — a single base64 string — goes into `appsettings.json` as `P8PrivateKey`.

#### Step 3 — Xcode Setup

1. **Signing & Capabilities** → add **Push Notifications**
2. **Signing & Capabilities** → add **Background Modes** → tick **Remote notifications**
3. No Firebase SDK needed

#### Step 4 — AppDelegate.swift

```swift
import UIKit
import UserNotifications

@main
class AppDelegate: UIResponder, UIApplicationDelegate,
                   UNUserNotificationCenterDelegate {

    func application(_ application: UIApplication,
        didFinishLaunchingWithOptions launchOptions: [UIApplication.LaunchOptionsKey: Any]?) -> Bool {

        UNUserNotificationCenter.current().delegate = self

        // Request permission
        UNUserNotificationCenter.current().requestAuthorization(
            options: [.alert, .badge, .sound]) { granted, _ in
            guard granted else { return }
            DispatchQueue.main.async {
                application.registerForRemoteNotifications()
            }
        }
        return true
    }

    // Apple gives you the raw APNs device token (binary Data)
    func application(_ application: UIApplication,
        didRegisterForRemoteNotificationsWithDeviceToken deviceToken: Data) {

        // Convert binary Data → hex string
        let tokenString = deviceToken
            .map { String(format: "%02x", $0) }
            .joined()

        // Send to YOUR backend with platform = "ios_apn"
        let userId = AuthManager.shared.currentUserId ?? return
        APIClient.registerDevice(
            token:    tokenString,
            platform: "ios_apn",   // ← native APNs token
            userId:   userId
        )
    }

    func application(_ application: UIApplication,
        didFailToRegisterForRemoteNotificationsWithError error: Error) {
        print("APNs registration failed: \(error)")
    }

    // Receive data push (foreground or background)
    func application(_ application: UIApplication,
        didReceiveRemoteNotification userInfo: [AnyHashable: Any],
        fetchCompletionHandler completionHandler: @escaping (UIBackgroundFetchResult) -> Void) {

        let event   = userInfo["event"] as? String ?? ""
        let payload = userInfo["payload"] as? String ?? "{}"
        handlePushEvent(event: event, payload: payload)
        completionHandler(.newData)
    }
}
```

---

## 5. Web Implementation

### 5.1 Firebase Project Setup

1. Firebase Console → **Project Settings** → **General** → **Your apps** → **Add app** → Web (`</>`)
2. Register with your app nickname → copy the `firebaseConfig` object
3. **Project Settings** → **Cloud Messaging** → **Web Push certificates** → **Generate key pair** → copy the **VAPID key**
4. Install Firebase SDK:

```bash
npm install firebase
```

### 5.2 Firebase Initialisation

```javascript
// src/firebase.js
import { initializeApp } from 'firebase/app';
import { getMessaging } from 'firebase/messaging';

const firebaseConfig = {
    apiKey:            'AIzaSyXXXXXXXXXXXXXXXX',
    authDomain:        'your-app.firebaseapp.com',
    projectId:         'your-app-12345',
    storageBucket:     'your-app.appspot.com',
    messagingSenderId: '123456789012',
    appId:             '1:123456789012:web:abcdef123456'
};

const app       = initializeApp(firebaseConfig);
const messaging = getMessaging(app);

export { messaging };
```

### 5.3 Request Permission and Register Token

```javascript
// src/push.js
import { messaging } from './firebase.js';
import { getToken, onMessage } from 'firebase/messaging';

const VAPID_KEY = 'YOUR_WEB_PUSH_VAPID_KEY_FROM_FIREBASE_CONSOLE';

export async function registerPushToken(userId) {
    // 1. Ask for permission
    const permission = await Notification.requestPermission();
    if (permission !== 'granted') {
        console.warn('Push notification permission denied');
        return;
    }

    // 2. Register service worker and get FCM token
    const registration = await navigator.serviceWorker
        .register('/firebase-messaging-sw.js');

    const token = await getToken(messaging, {
        vapidKey: VAPID_KEY,
        serviceWorkerRegistration: registration
    });

    // 3. Send to YOUR backend with platform = "web"
    await fetch('/device/register', {
        method:  'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({
            userId:   userId,
            token:    token,
            platform: 'web'   // ← always "web"
        })
    });

    console.log('Push token registered:', token.substring(0, 20) + '...');
}

// Handle messages when browser tab is OPEN (foreground)
onMessage(messaging, (payload) => {
    const event = payload.data?.event;
    const data  = JSON.parse(payload.data?.payload ?? '{}');

    switch (event) {
        case 'ORDER_SHIPPED': handleOrderShipped(data); break;
        case 'FLASH_SALE':    showFlashSale(data);      break;
        default: console.log('Push received:', event, data);
    }
});
```

### 5.4 Service Worker

> ⚠️ This file **must** be served from the root of your domain: `https://yoursite.com/firebase-messaging-sw.js`

```javascript
// /public/firebase-messaging-sw.js
importScripts('https://www.gstatic.com/firebasejs/10.7.0/firebase-app-compat.js');
importScripts('https://www.gstatic.com/firebasejs/10.7.0/firebase-messaging-compat.js');

firebase.initializeApp({
    apiKey:            'AIzaSyXXXXXXXXXXXXXXXX',
    authDomain:        'your-app.firebaseapp.com',
    projectId:         'your-app-12345',
    storageBucket:     'your-app.appspot.com',
    messagingSenderId: '123456789012',
    appId:             '1:123456789012:web:abcdef123456'
});

const messaging = firebase.messaging();

// Handle messages when tab is CLOSED or in background
messaging.onBackgroundMessage((payload) => {
    const event = payload.data?.event;
    const data  = JSON.parse(payload.data?.payload ?? '{}');

    // Show a native browser notification
    self.registration.showNotification('New Update', {
        body:  event,
        icon:  '/icon-192x192.png',
        badge: '/badge-72x72.png',
        data:  data
    });
});
```

---

## 6. Complete Registration Flow

```
App Install / Login
       │
       ▼
Client SDK generates token
       │
       ├── Android    → onNewToken()                  → platform = "android"
       ├── iOS (FCM)  → didReceiveRegistrationToken() → platform = "ios_fcm"
       ├── iOS (APNs) → didRegisterForRemoteNotifs()  → Data→hex → platform = "ios_apn"
       └── Web        → getToken()                    → platform = "web"
                                   │
                                   ▼
                    POST /device/register
                    { userId, token, platform }
                                   │
                                   ▼
                    DB: DeviceTokens table
                    { UserId, Token, Platform, IsActive }
                                   │
                                   ▼
                    SmartPushService.SendToUserAsync()
                           │
                           ├── Platform = Android/IosFcm/Web  → IFcmSender
                           └── Platform = IosApn              → IApnSender
```

---

## 7. Error Handling & Token Cleanup

After every send operation, handle the result — never ignore it.

```csharp
var result = await _fcm.SendToTokenAsync(token, message);

if (result.IsSuccess)
{
    await _repo.UpdateLastUsedAsync(device.Id);
    _logger.LogInformation("Delivered to {Platform}", device.Platform);
}
else if (result.IsTokenInvalid)
{
    // App uninstalled or token rotated — remove permanently
    await _repo.DeactivateAsync(device.Id);
    _logger.LogWarning("Stale token removed for user {UserId}", device.UserId);
}
else if (result.IsRetryable)
{
    // Polly already retried 3 times. Queue for later via Hangfire / a message queue.
    _logger.LogWarning("Transient failure [{Code}] — may retry later", result.ErrorCode);
}
else
{
    _logger.LogError("Push failed [{Code}]: {Message}", result.ErrorCode, result.ErrorMessage);
}
```

### Batch Result Cleanup

```csharp
var batch = await _fcm.SendBatchAsync(tokens, message);

_logger.LogInformation("Batch: {Ok}/{Total} delivered", batch.SuccessCount, batch.TotalCount);

// Remove all permanently invalid tokens in one DB call
var invalidIds = GetDeviceIdsByTokens(batch.InvalidTokens);
await _repo.DeactivateManyAsync(invalidIds);
```

---

## 8. Quick Reference

### FCM Error Codes

| Error Code | HTTP | Meaning | Action |
|---|---|---|---|
| `UNREGISTERED` | 404 | App uninstalled / token expired | ❌ Remove from DB |
| `INVALID_ARGUMENT` | 400 | Malformed token | ❌ Remove from DB |
| `QUOTA_EXCEEDED` | 429 | Rate limit hit | ♻️ Polly retries automatically |
| `UNAVAILABLE` | 503 | FCM temporarily down | ♻️ Polly retries automatically |
| `INTERNAL` | 500 | FCM internal error | ♻️ Polly retries automatically |
| `SENDER_ID_MISMATCH` | 403 | Wrong Firebase project | 🔧 Fix `ProjectId` in config |

### APNs Error Codes

| Reason | HTTP | Meaning | Action |
|---|---|---|---|
| `BadDeviceToken` | 400 | Token is malformed | ❌ Remove from DB |
| `Unregistered` | 410 | App was uninstalled | ❌ Remove from DB |
| `DeviceTokenNotForTopic` | 400 | Wrong bundle ID | 🔧 Fix `BundleId` in config |
| `TooManyRequests` | 429 | Rate limited by Apple | ♻️ Polly retries automatically |
| `InternalServerError` | 500 | Apple server error | ♻️ Polly retries automatically |
| `BadTopic` | 400 | Invalid apns-topic header | 🔧 Check `BundleId` matches app |

### PushResult Cheatsheet

```csharp
result.IsSuccess        // true = provider accepted the message
result.MessageId        // FCM: "projects/.../messages/123" | APNs: apns-id header value
result.HttpStatus       // 200, 400, 404, 429, 500 ...
result.ErrorCode        // "UNREGISTERED", "BadDeviceToken", "QUOTA_EXCEEDED" etc.
result.ErrorMessage     // Human-readable description
result.IsTokenInvalid   // true → remove from database NOW
result.IsRetryable      // true → Polly already tried; consider queueing

// Batch
batch.TotalCount        // total tokens attempted
batch.SuccessCount      // successfully delivered
batch.FailureCount      // failed
batch.InvalidTokens     // IEnumerable<string> — remove these from DB
batch.RetryableTokens   // IEnumerable<string> — may retry later
batch.Results           // IReadOnlyList<PushResult> — full per-token detail
```

### Final Checklist

**Firebase Setup**
- [ ] Firebase project created
- [ ] `google-services.json` added to Android `app/` directory
- [ ] `GoogleService-Info.plist` added to iOS Xcode project
- [ ] Firebase Web config object copied to `firebase.js`
- [ ] VAPID key generated and copied for Web

**Apple Setup (APNs native path only)**
- [ ] APNs key created in Apple Developer Portal
- [ ] Key ID (10 chars) noted
- [ ] Team ID (10 chars) noted
- [ ] `.p8` file downloaded and stored safely
- [ ] Base64 key content extracted (no header/footer/newlines)

**Backend**
- [ ] `appsettings.json` has `ProjectId`, service account path/JSON, and APNs credentials
- [ ] `AddPushKit(configuration)` called in `Program.cs`
- [ ] `/device/register` endpoint accepts `token`, `platform`, `userId`
- [ ] `DeviceToken` table has `Platform` column
- [ ] `SmartPushService` routes by `Platform` field
- [ ] Invalid token cleanup implemented after every send

**Android**
- [ ] `FirebaseMessagingService` implemented and registered in `AndroidManifest.xml`
- [ ] `onNewToken()` calls `/device/register` with `platform = "android"`
- [ ] `onMessageReceived()` handles data payload

**iOS**
- [ ] Push Notifications capability enabled in Xcode
- [ ] Background Modes → Remote Notifications enabled
- [ ] FCM path: `MessagingDelegate.didReceiveRegistrationToken` posts with `platform = "ios_fcm"`
- [ ] APNs path: `didRegisterForRemoteNotificationsWithDeviceToken` converts `Data → hex`, posts with `platform = "ios_apn"`

**Web**
- [ ] `firebase-messaging-sw.js` is served from root path `/`
- [ ] `getToken()` called after permission granted
- [ ] Token posted with `platform = "web"`
- [ ] `onMessage()` handles foreground messages
- [ ] `onBackgroundMessage()` in service worker handles background messages

---

> ⚠️ **Always test on real devices.** FCM and APNs push delivery to simulators/emulators is unreliable or unsupported.
