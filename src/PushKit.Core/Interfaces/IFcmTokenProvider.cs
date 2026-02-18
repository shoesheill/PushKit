namespace PushKit.Interfaces;

/// <summary>
/// Provides a valid Google OAuth2 Bearer token for the Firebase Messaging scope.
/// The default implementation uses a service account JSON key.
/// Replace this with your own implementation for GKE Workload Identity,
/// Azure Managed Identity, or unit test mocks.
/// </summary>
public interface IFcmTokenProvider
{
    /// <summary>
    /// Returns a valid access token. Implementations MUST cache and auto-refresh.
    /// </summary>
    Task<string> GetAccessTokenAsync(CancellationToken cancellationToken = default);
}
