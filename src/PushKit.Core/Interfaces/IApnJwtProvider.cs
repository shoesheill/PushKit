namespace PushKit.Interfaces;

/// <summary>
/// Provides a valid ES256 provider-authentication JWT for APNs requests.
/// Implementations MUST cache and auto-refresh — Apple rate-limits how often a
/// new token may be minted per key (HTTP 429 / TooManyProviderTokenUpdates) and
/// expects the same token to be reused across many requests.
/// </summary>
public interface IApnJwtProvider
{
    Task<string> GetJwtAsync(CancellationToken cancellationToken = default);
}
