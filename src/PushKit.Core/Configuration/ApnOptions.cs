namespace PushKit.Configuration;

/// <summary>
/// Apple Push Notification service (APNs) configuration.
/// Bind from appsettings.json under "PushKit:Apn" or configure inline.
/// </summary>
public sealed class ApnOptions
{
    public const string SectionName = "PushKit:Apn";

    /// <summary>
    /// P8 private key content — the single-line base64 string from Apple.
    /// Strip the "-----BEGIN PRIVATE KEY-----" header/footer and all whitespace.
    /// </summary>
    public string P8PrivateKey { get; set; } = string.Empty;

    /// <summary>
    /// 10-character key ID from Apple Developer Portal.
    /// Usually part of the downloaded filename: AuthKey_XXXXXXXXXX.p8
    /// </summary>
    public string P8PrivateKeyId { get; set; } = string.Empty;

    /// <summary>10-character Apple Team ID from Apple Developer Portal.</summary>
    public string TeamId { get; set; } = string.Empty;

    /// <summary>Your app bundle identifier, e.g. "com.mycompany.myapp".</summary>
    public string BundleId { get; set; } = string.Empty;

    /// <summary>Send to production or sandbox (development) APNs endpoint.</summary>
    public ApnEnvironment Environment { get; set; } = ApnEnvironment.Production;

    /// <summary>Max retry attempts on transient APNs errors. Default 3.</summary>
    public int MaxRetryAttempts { get; set; } = 3;

    /// <summary>Per-request HTTP timeout in seconds. Default 30.</summary>
    public int RequestTimeoutSeconds { get; set; } = 30;

    /// <summary>Max parallel HTTP/2 requests during batch send. Default 50.</summary>
    public int BatchParallelism { get; set; } = 50;

    /// <summary>Override APNs base URL for testing.</summary>
    public string? ApnBaseUrl { get; set; }

    internal string GetApnHost() =>
        ApnBaseUrl ?? (Environment == ApnEnvironment.Production
            ? "https://api.push.apple.com"
            : "https://api.sandbox.push.apple.com");
}
