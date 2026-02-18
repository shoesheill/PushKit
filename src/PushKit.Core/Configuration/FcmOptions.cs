namespace PushKit.Configuration;

/// <summary>
/// Firebase Cloud Messaging configuration.
/// Bind from appsettings.json under "PushKit:Fcm" or configure inline.
/// </summary>
public sealed class FcmOptions
{
    public const string SectionName = "PushKit:Fcm";

    /// <summary>Firebase project ID (e.g. "my-app-12345").</summary>
    public string ProjectId { get; set; } = string.Empty;

    /// <summary>
    /// Path to the Firebase service account JSON key file.
    /// Takes lower priority than <see cref="ServiceAccountJson"/>.
    /// </summary>
    public string? ServiceAccountKeyFilePath { get; set; }

    /// <summary>
    /// Inline service account JSON content.
    /// Inject from env vars or secrets manager. Takes priority over the file path.
    /// </summary>
    public string? ServiceAccountJson { get; set; }

    /// <summary>Max retry attempts on transient failures. Default 3. Set 0 to disable.</summary>
    public int MaxRetryAttempts { get; set; } = 3;

    /// <summary>Base delay in ms for exponential backoff. Default 500ms.</summary>
    public int RetryBaseDelayMs { get; set; } = 500;

    /// <summary>Per-request HTTP timeout in seconds. Default 30.</summary>
    public int RequestTimeoutSeconds { get; set; } = 30;

    /// <summary>Max parallel HTTP requests during batch send. Default 100.</summary>
    public int BatchParallelism { get; set; } = 100;

    /// <summary>FCM HTTP v1 base URL. Override for proxies / integration tests.</summary>
    public string FcmBaseUrl { get; set; } = "https://fcm.googleapis.com";

    /// <summary>Google OAuth2 scope for Firebase Messaging.</summary>
    public string TokenScope { get; set; } = "https://www.googleapis.com/auth/firebase.messaging";
}
