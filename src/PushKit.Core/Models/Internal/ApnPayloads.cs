using System.Text.Json.Serialization;

namespace PushKit.Models.Internal;

/// <summary>Serializable APNs JSON payload envelope.</summary>
internal sealed class ApnPayload
{
    [JsonPropertyName("aps")]
    public ApnApsInternal Aps { get; set; } = new();

    // All additional root-level custom fields are added dynamically at serialisation time
}

internal sealed class ApnApsInternal
{
    [JsonPropertyName("alert")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ApnAlertInternal? Alert { get; set; }

    [JsonPropertyName("badge")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Badge { get; set; }

    [JsonPropertyName("sound")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Sound { get; set; }

    [JsonPropertyName("content-available")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? ContentAvailable { get; set; }

    [JsonPropertyName("mutable-content")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? MutableContent { get; set; }

    [JsonPropertyName("category")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Category { get; set; }

    [JsonPropertyName("thread-id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ThreadId { get; set; }
}

internal sealed class ApnAlertInternal
{
    [JsonPropertyName("title")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Title { get; set; }

    [JsonPropertyName("subtitle")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Subtitle { get; set; }

    [JsonPropertyName("body")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Body { get; set; }
}

/// <summary>APNs error response body.</summary>
internal sealed class ApnErrorResponse
{
    [JsonPropertyName("reason")]
    public string Reason { get; set; } = string.Empty;

    [JsonPropertyName("timestamp")]
    public long? Timestamp { get; set; }
}
