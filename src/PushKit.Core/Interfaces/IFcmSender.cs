using PushKit.Models;

namespace PushKit.Interfaces;

/// <summary>
/// Sends FCM messages (data, notification, or both) to Android, iOS via FCM, and Web clients.
/// Register via <c>services.AddPushKit()</c> and inject <c>IFcmSender</c> wherever needed.
/// </summary>
public interface IFcmSender
{
    /// <summary>Send a message to a single target (token, topic, or condition).</summary>
    Task<PushResult> SendAsync(
        PushTarget target,
        PushMessage message,
        CancellationToken cancellationToken = default);

    /// <summary>Convenience: send to a single device token.</summary>
    Task<PushResult> SendToTokenAsync(
        string deviceToken,
        PushMessage message,
        CancellationToken cancellationToken = default);

    /// <summary>Convenience: send to an FCM topic.</summary>
    Task<PushResult> SendToTopicAsync(
        string topic,
        PushMessage message,
        CancellationToken cancellationToken = default);

    /// <summary>Convenience: send via boolean topic condition expression.</summary>
    Task<PushResult> SendToConditionAsync(
        string condition,
        PushMessage message,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Send the same message to many device tokens in parallel.
    /// Parallelism is capped by <c>FcmOptions.BatchParallelism</c>.
    /// Returns a <see cref="BatchPushResult"/> with per-token outcomes including
    /// <see cref="BatchPushResult.InvalidTokens"/> for database cleanup.
    /// </summary>
    Task<BatchPushResult> SendBatchAsync(
        IEnumerable<string> deviceTokens,
        PushMessage message,
        CancellationToken cancellationToken = default);
}
