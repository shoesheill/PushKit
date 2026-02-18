using PushKit.Models;

namespace PushKit.Interfaces;

/// <summary>
/// Sends native APNs messages directly to Apple's servers via HTTP/2 and p8 JWT authentication.
/// Use this when you want native iOS/macOS push without going through Firebase.
/// Register via <c>services.AddPushKit()</c> and inject <c>IApnSender</c>.
/// </summary>
public interface IApnSender
{
    /// <summary>
    /// Send a push message to a single Apple device token.
    /// </summary>
    /// <param name="deviceToken">The hex APNs device token string.</param>
    /// <param name="message">The APNs message to deliver.</param>
    /// <param name="cancellationToken">Cancellation support.</param>
    Task<PushResult> SendAsync(
        string deviceToken,
        ApnMessage message,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Send the same APNs message to many device tokens in parallel.
    /// Built-in rate-limiting prevents HTTP 429 from Apple.
    /// Returns a <see cref="BatchPushResult"/> with per-token outcomes and
    /// <see cref="BatchPushResult.InvalidTokens"/> for database cleanup.
    /// </summary>
    Task<BatchPushResult> SendBatchAsync(
        IEnumerable<string> deviceTokens,
        ApnMessage message,
        CancellationToken cancellationToken = default);
}
