using System.Text.Json;
using PushKit.Models;

namespace PushKit.Builders;

/// <summary>
/// Fluent, type-safe builder for <see cref="PushMessage"/>.
/// Eliminates the raw anonymous-object pattern used in CorePush.
/// </summary>
/// <example>
/// <code>
/// var msg = PushMessageBuilder.Create()
///     .WithData("event", "ORDER_SHIPPED")
///     .WithData("orderId", "ORD-999")
///     .WithNotification("Your order shipped!", "It's on its way.")
///     .WithAndroid(priority: AndroidPriority.High, ttlSeconds: 86400)
///     .Build();
/// </code>
/// </example>
public sealed class PushMessageBuilder
{
    private readonly PushMessage _msg = new();

    private PushMessageBuilder() { }

    /// <summary>Start building a new <see cref="PushMessage"/>.</summary>
    public static PushMessageBuilder Create() => new();

    // ─── Data ─────────────────────────────────────────────────────────────────

    /// <summary>Add a string key-value pair to the data payload.</summary>
    public PushMessageBuilder WithData(string key, string value)
    {
        _msg.Data[key] = value;
        return this;
    }

    /// <summary>
    /// JSON-serialize <paramref name="value"/> and store it under <paramref name="key"/>.
    /// Perfect for embedding complex DTOs in the data payload.
    /// </summary>
    public PushMessageBuilder WithData<T>(string key, T value, JsonSerializerOptions? opts = null)
    {
        _msg.Data[key] = JsonSerializer.Serialize(value, opts);
        return this;
    }

    /// <summary>Merge an existing dictionary into the data payload.</summary>
    public PushMessageBuilder WithData(IDictionary<string, string> data)
    {
        foreach (var (k, v) in data) _msg.Data[k] = v;
        return this;
    }

    // ─── Notification ─────────────────────────────────────────────────────────

    /// <summary>
    /// Add a visible notification (title + body).
    /// If not called, the message is data-only (silent).
    /// </summary>
    public PushMessageBuilder WithNotification(string? title = null, string? body = null, string? imageUrl = null)
    {
        _msg.Notification = new NotificationPayload { Title = title, Body = body, ImageUrl = imageUrl };
        return this;
    }

    // ─── Android ─────────────────────────────────────────────────────────────

    /// <summary>Configure Android-specific delivery options.</summary>
    public PushMessageBuilder WithAndroid(
        AndroidPriority priority = AndroidPriority.Normal,
        int? ttlSeconds = null,
        string? collapseKey = null,
        string? channelId = null)
    {
        _msg.Android = new AndroidOptions
        {
            Priority = priority,
            TtlSeconds = ttlSeconds,
            CollapseKey = collapseKey,
            ChannelId = channelId
        };
        return this;
    }

    /// <summary>Configure Android delivery via a delegate for full control.</summary>
    public PushMessageBuilder WithAndroid(Action<AndroidOptions> configure)
    {
        _msg.Android = new AndroidOptions();
        configure(_msg.Android);
        return this;
    }

    // ─── APNs (via FCM) ───────────────────────────────────────────────────────

    /// <summary>Configure APNs headers and payload for FCM-to-APNs delivery.</summary>
    public PushMessageBuilder WithApns(Action<ApnsOptions>? configure = null)
    {
        _msg.Apns = new ApnsOptions();
        configure?.Invoke(_msg.Apns);
        return this;
    }

    // ─── Web Push ─────────────────────────────────────────────────────────────

    /// <summary>Configure Web Push delivery options.</summary>
    public PushMessageBuilder WithWebPush(Action<WebPushOptions>? configure = null)
    {
        _msg.WebPush = new WebPushOptions();
        configure?.Invoke(_msg.WebPush);
        return this;
    }

    // ─── Meta ─────────────────────────────────────────────────────────────────

    /// <summary>Set a custom message ID for tracing and deduplication.</summary>
    public PushMessageBuilder WithMessageId(string id)
    {
        _msg.MessageId = id;
        return this;
    }

    /// <summary>
    /// Enable dry-run mode: the provider validates the payload but does NOT deliver.
    /// Useful for testing without hitting real devices.
    /// </summary>
    public PushMessageBuilder DryRun(bool enabled = true)
    {
        _msg.ValidateOnly = enabled;
        return this;
    }

    // ─── Build ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Builds and validates the <see cref="PushMessage"/>.
    /// Throws <see cref="InvalidOperationException"/> if neither data nor notification is set.
    /// </summary>
    public PushMessage Build()
    {
        if (_msg.Data.Count == 0 && _msg.Notification is null)
            throw new InvalidOperationException(
                "A PushMessage must have at least one data entry or a notification. " +
                "Call WithData(...) or WithNotification(...).");
        return _msg;
    }
}

// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Fluent builder for native <see cref="ApnMessage"/> (direct APNs path).
/// </summary>
public sealed class ApnMessageBuilder
{
    private readonly ApnMessage _msg = new();

    private ApnMessageBuilder() { }

    public static ApnMessageBuilder Create() => new();

    public ApnMessageBuilder WithAlert(string? title = null, string? body = null, string? subtitle = null)
    {
        _msg.Aps.Alert = new ApnAlert { Title = title, Body = body, Subtitle = subtitle };
        _msg.PushType = ApnPushType.Alert;
        return this;
    }

    public ApnMessageBuilder WithBadge(int count) { _msg.Aps.Badge = count; return this; }

    public ApnMessageBuilder WithSound(string sound = "default") { _msg.Aps.Sound = sound; return this; }

    /// <summary>Silent background push — wakes the app to fetch content.</summary>
    public ApnMessageBuilder AsBackground()
    {
        _msg.Aps.ContentAvailable = 1;
        _msg.PushType = ApnPushType.Background;
        _msg.Priority = 5; // Apple requires priority 5 for background pushes
        return this;
    }

    public ApnMessageBuilder WithMutableContent() { _msg.Aps.MutableContent = 1; return this; }

    public ApnMessageBuilder WithCategory(string category) { _msg.Aps.Category = category; return this; }

    public ApnMessageBuilder WithThreadId(string threadId) { _msg.Aps.ThreadId = threadId; return this; }

    /// <summary>Add a custom root-level field to the APNs payload.</summary>
    public ApnMessageBuilder WithCustomData(string key, object value) { _msg.CustomData[key] = value; return this; }

    /// <summary>JSON-serialize a complex object and store it under <paramref name="key"/>.</summary>
    public ApnMessageBuilder WithCustomData<T>(string key, T value, JsonSerializerOptions? opts = null)
    {
        _msg.CustomData[key] = JsonSerializer.Serialize(value, opts);
        return this;
    }

    public ApnMessageBuilder WithCollapseId(string collapseId) { _msg.CollapseId = collapseId; return this; }

    public ApnMessageBuilder WithPriority(int priority) { _msg.Priority = priority; return this; }

    public ApnMessageBuilder ExpiresIn(int seconds) { _msg.ExpirationSeconds = seconds; return this; }

    public ApnMessageBuilder AsPushType(ApnPushType type) { _msg.PushType = type; return this; }

    public ApnMessageBuilder DryRun(bool enabled = true) { _msg.ValidateOnly = enabled; return this; }

    public ApnMessage Build()
    {
        if (_msg.Aps.Alert is null &&
            _msg.Aps.ContentAvailable is null &&
            _msg.CustomData.Count == 0)
            throw new InvalidOperationException(
                "An ApnMessage must have at least an alert, content-available flag, or custom data.");
        return _msg;
    }
}
