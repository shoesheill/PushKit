namespace PushKit.Models;

/// <summary>
/// Result of a single push send operation.
/// Errors are returned as values — never thrown as exceptions for FCM/APNs protocol errors.
/// </summary>
public sealed class PushResult
{
    /// <summary>True if the provider accepted the message for delivery.</summary>
    public bool IsSuccess { get; init; }

    /// <summary>The provider message ID / name on success.</summary>
    public string? MessageId { get; init; }

    /// <summary>The target this result is for.</summary>
    public PushTarget? Target { get; init; }

    /// <summary>Provider error code on failure (e.g. "UNREGISTERED", "BadDeviceToken").</summary>
    public string? ErrorCode { get; init; }

    /// <summary>Human-readable error description.</summary>
    public string? ErrorMessage { get; init; }

    /// <summary>HTTP status code returned by the provider.</summary>
    public int? HttpStatus { get; init; }

    /// <summary>
    /// True when the token/device is permanently invalid and should be removed from your database.
    /// Covers FCM: UNREGISTERED, INVALID_ARGUMENT | APNs: BadDeviceToken, Unregistered.
    /// </summary>
    public bool IsTokenInvalid =>
        ErrorCode is
            "UNREGISTERED" or "INVALID_ARGUMENT" or   // FCM
            "BadDeviceToken" or "Unregistered" or      // APNs
            "DeviceTokenNotForTopic";                  // APNs wrong bundle

    /// <summary>
    /// True when the error is transient and a retry may succeed.
    /// Polly handles these automatically; exposed here for manual retry strategies.
    /// </summary>
    public bool IsRetryable =>
        ErrorCode is
            "UNAVAILABLE" or "INTERNAL" or "QUOTA_EXCEEDED" or  // FCM
            "TooManyRequests" or "InternalServerError" or        // APNs
            "ServiceUnavailable" or "Shutdown";                  // APNs

    public static PushResult Success(PushTarget target, string messageId, int httpStatus = 200) =>
        new() { IsSuccess = true, Target = target, MessageId = messageId, HttpStatus = httpStatus };

    public static PushResult Failure(PushTarget target, string errorCode, string errorMessage, int? httpStatus = null) =>
        new() { IsSuccess = false, Target = target, ErrorCode = errorCode, ErrorMessage = errorMessage, HttpStatus = httpStatus };

    public override string ToString() =>
        IsSuccess
            ? $"[OK] {Target?.Type}:{Target?.Masked()} → {MessageId}"
            : $"[FAIL] {Target?.Type}:{Target?.Masked()} → HTTP {HttpStatus} | {ErrorCode}: {ErrorMessage}";
}

/// <summary>Aggregated result of a batch send to many tokens.</summary>
public sealed class BatchPushResult
{
    public IReadOnlyList<PushResult> Results { get; init; } = [];
    public int TotalCount => Results.Count;
    public int SuccessCount => Results.Count(r => r.IsSuccess);
    public int FailureCount => Results.Count(r => !r.IsSuccess);
    public bool IsFullSuccess => FailureCount == 0;
    public bool IsPartialSuccess => SuccessCount > 0 && FailureCount > 0;
    public bool IsFullFailure => SuccessCount == 0;

    /// <summary>
    /// Tokens permanently rejected by the provider.
    /// Remove these from your database immediately.
    /// </summary>
    public IEnumerable<string> InvalidTokens =>
        Results
            .Where(r => r.IsTokenInvalid && r.Target?.Type == TargetType.Token)
            .Select(r => r.Target!.Value);

    /// <summary>Tokens that failed with retryable errors. You may retry these.</summary>
    public IEnumerable<string> RetryableTokens =>
        Results
            .Where(r => !r.IsSuccess && r.IsRetryable && r.Target?.Type == TargetType.Token)
            .Select(r => r.Target!.Value);

    public override string ToString() =>
        $"Batch: {SuccessCount}/{TotalCount} succeeded, {FailureCount} failed";
}
