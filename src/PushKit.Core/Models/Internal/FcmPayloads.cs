using System.Text.Json.Serialization;

namespace PushKit.Models.Internal;

// ─── Request ─────────────────────────────────────────────────────────────────

internal sealed class FcmSendRequest
{
    [JsonPropertyName("validate_only")]
    public bool ValidateOnly { get; set; }

    [JsonPropertyName("message")]
    public FcmMessage Message { get; set; } = default!;
}

internal sealed class FcmMessage
{
    [JsonPropertyName("token")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Token { get; set; }

    [JsonPropertyName("topic")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Topic { get; set; }

    [JsonPropertyName("condition")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Condition { get; set; }

    [JsonPropertyName("data")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, string>? Data { get; set; }

    [JsonPropertyName("notification")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public FcmNotification? Notification { get; set; }

    [JsonPropertyName("android")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public FcmAndroid? Android { get; set; }

    [JsonPropertyName("apns")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public FcmApns? Apns { get; set; }

    [JsonPropertyName("webpush")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public FcmWebPush? WebPush { get; set; }
}

internal sealed class FcmNotification
{
    [JsonPropertyName("title")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Title { get; set; }

    [JsonPropertyName("body")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Body { get; set; }

    [JsonPropertyName("image")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Image { get; set; }
}

internal sealed class FcmAndroid
{
    [JsonPropertyName("priority")]
    public string Priority { get; set; } = "normal";

    [JsonPropertyName("ttl")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Ttl { get; set; }

    [JsonPropertyName("collapse_key")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CollapseKey { get; set; }

    [JsonPropertyName("notification")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public FcmAndroidNotification? Notification { get; set; }
}

internal sealed class FcmAndroidNotification
{
    [JsonPropertyName("channel_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ChannelId { get; set; }
}

internal sealed class FcmApns
{
    [JsonPropertyName("headers")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, string>? Headers { get; set; }

    [JsonPropertyName("payload")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, object>? Payload { get; set; }
}

internal sealed class FcmWebPush
{
    [JsonPropertyName("headers")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, string>? Headers { get; set; }

    [JsonPropertyName("data")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, string>? Data { get; set; }
}

// ─── Response ─────────────────────────────────────────────────────────────────

internal sealed class FcmSendResponse
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;
}

internal sealed class FcmErrorEnvelope
{
    [JsonPropertyName("error")]
    public FcmError? Error { get; set; }
}

internal sealed class FcmError
{
    [JsonPropertyName("code")]
    public int Code { get; set; }

    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;

    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;

    [JsonPropertyName("details")]
    public List<FcmErrorDetail>? Details { get; set; }
}

internal sealed class FcmErrorDetail
{
    [JsonPropertyName("@type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("errorCode")]
    public string? ErrorCode { get; set; }
}
