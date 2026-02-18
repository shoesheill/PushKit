namespace PushKit.Models;

/// <summary>
/// A complete push message. Supports data-only messages, visible notifications, or both.
/// Build instances via <see cref="PushKit.Builders.PushMessageBuilder"/>.
/// </summary>
public sealed class PushMessage
{
    /// <summary>
    /// Key-value data payload (string → string).
    /// Delivered silently to the app; the app decides how to display it.
    /// All FCM values must be strings — complex objects should be JSON-serialized first.
    /// Max 4 KB total.
    /// </summary>
    public Dictionary<string, string> Data { get; internal set; } = [];

    /// <summary>
    /// Optional visible notification shown by the OS.
    /// If null, this is a silent data-only message.
    /// </summary>
    public NotificationPayload? Notification { get; internal set; }

    /// <summary>Android-specific delivery tuning.</summary>
    public AndroidOptions? Android { get; internal set; }

    /// <summary>APNs (iOS/macOS) delivery tuning for FCM-to-APNs messages.</summary>
    public ApnsOptions? Apns { get; internal set; }

    /// <summary>Web Push delivery tuning.</summary>
    public WebPushOptions? WebPush { get; internal set; }

    /// <summary>
    /// When true, validates the payload with FCM/APNs but does NOT deliver.
    /// Useful during development and CI.
    /// </summary>
    public bool ValidateOnly { get; internal set; }

    /// <summary>Optional caller-assigned ID for tracing / deduplication.</summary>
    public string? MessageId { get; internal set; }
}

/// <summary>Visible notification shown by the OS notification centre.</summary>
public sealed class NotificationPayload
{
    public string? Title { get; set; }
    public string? Body { get; set; }
    /// <summary>URL of an image to show (FCM + some APNs configs).</summary>
    public string? ImageUrl { get; set; }
}

/// <summary>Android-specific delivery options.</summary>
public sealed class AndroidOptions
{
    /// <summary>"normal" (battery-friendly) or "high" (wakes the device immediately).</summary>
    public AndroidPriority Priority { get; set; } = AndroidPriority.Normal;

    /// <summary>
    /// How long FCM stores the message when the device is offline.
    /// Range: 0 – 2,419,200 seconds (28 days).
    /// </summary>
    public int? TtlSeconds { get; set; }

    /// <summary>
    /// Collapse key: only the latest message with this key is retained per device.
    /// </summary>
    public string? CollapseKey { get; set; }

    /// <summary>Notification channel ID for Android 8+.</summary>
    public string? ChannelId { get; set; }
}

public enum AndroidPriority { Normal, High }

/// <summary>APNs-specific options used when FCM delivers to Apple devices.</summary>
public sealed class ApnsOptions
{
    /// <summary>APNs headers (e.g. apns-priority: 10, apns-collapse-id).</summary>
    public Dictionary<string, string> Headers { get; set; } = [];

    /// <summary>Extra APS payload fields merged into the aps dict.</summary>
    public Dictionary<string, object> ApsPayload { get; set; } = [];
}

/// <summary>Web Push (VAPID) options.</summary>
public sealed class WebPushOptions
{
    public Dictionary<string, string> Headers { get; set; } = [];
    public Dictionary<string, string> Data { get; set; } = [];
}

// ─── APNs-native message (used by IApnSender directly) ───────────────────────

/// <summary>
/// A native APNs message, built separately from the FCM path.
/// Use this when sending directly via <see cref="PushKit.Interfaces.IApnSender"/>
/// without going through Firebase.
/// </summary>
public sealed class ApnMessage
{
    /// <summary>The APS dictionary — the core of every APNs message.</summary>
    public ApnAps Aps { get; set; } = new();

    /// <summary>Custom key-value pairs merged at the root of the APNs JSON payload.</summary>
    public Dictionary<string, object> CustomData { get; set; } = [];

    /// <summary>apns-push-type header value. Default is "alert" if notification set, else "background".</summary>
    public ApnPushType PushType { get; set; } = ApnPushType.Alert;

    /// <summary>apns-priority: 10 = immediate, 5 = conserve power. Default 10.</summary>
    public int Priority { get; set; } = 10;

    /// <summary>APNs expiry in seconds from now. 0 = deliver once and discard.</summary>
    public int ExpirationSeconds { get; set; } = 3600;

    /// <summary>apns-collapse-id: collapse multiple messages with the same key.</summary>
    public string? CollapseId { get; set; }

    /// <summary>When true, validates payload without actual delivery (sandbox only).</summary>
    public bool ValidateOnly { get; set; }
}

public sealed class ApnAps
{
    public ApnAlert? Alert { get; set; }
    public int? Badge { get; set; }
    public string? Sound { get; set; }
    /// <summary>Set 1 to trigger background fetch (silent push).</summary>
    public int? ContentAvailable { get; set; }
    /// <summary>Set 1 to trigger notification service extension.</summary>
    public int? MutableContent { get; set; }
    public string? Category { get; set; }
    public string? ThreadId { get; set; }
}

public sealed class ApnAlert
{
    public string? Title { get; set; }
    public string? Subtitle { get; set; }
    public string? Body { get; set; }
}

public enum ApnPushType { Alert, Background, Location, Voip, Complication, FileProvider, Mdm }
