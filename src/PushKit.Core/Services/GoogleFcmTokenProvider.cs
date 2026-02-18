using Google.Apis.Auth.OAuth2;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PushKit.Configuration;
using PushKit.Exceptions;
using PushKit.Interfaces;

namespace PushKit.Services;

/// <summary>
/// Fetches and caches a Google OAuth2 access token using a Firebase service account.
/// Thread-safe: concurrent refresh attempts are serialised via a SemaphoreSlim.
/// Token is refreshed 60 seconds before its 3600-second expiry.
/// </summary>
internal sealed class GoogleFcmTokenProvider : IFcmTokenProvider
{
    private readonly FcmOptions _options;
    private readonly ILogger<GoogleFcmTokenProvider> _logger;
    private readonly SemaphoreSlim _lock = new(1, 1);

    private string? _cachedToken;
    private DateTime _expiresAt = DateTime.MinValue;
    private const int RefreshBufferSeconds = 60;

    public GoogleFcmTokenProvider(IOptions<FcmOptions> options, ILogger<GoogleFcmTokenProvider> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public async Task<string> GetAccessTokenAsync(CancellationToken cancellationToken = default)
    {
        // Fast path — no lock needed if token is still valid
        if (_cachedToken is not null && DateTime.UtcNow < _expiresAt)
            return _cachedToken;

        await _lock.WaitAsync(cancellationToken);
        try
        {
            // Double-check inside the lock
            if (_cachedToken is not null && DateTime.UtcNow < _expiresAt)
                return _cachedToken;

            _logger.LogDebug("Refreshing FCM OAuth2 access token...");

            var credential = await LoadCredentialAsync(cancellationToken);
            var token = await credential.GetAccessTokenForRequestAsync(_options.TokenScope, cancellationToken)
                ?? throw new PushKitAuthException("Google returned a null access token.");

            _cachedToken = token;
            _expiresAt = DateTime.UtcNow.AddSeconds(3600 - RefreshBufferSeconds);

            _logger.LogInformation("FCM token refreshed. Valid until {Expiry:HH:mm:ss} UTC", _expiresAt);
            return _cachedToken;
        }
        catch (Exception ex) when (ex is not PushKitException)
        {
            throw new PushKitAuthException($"Failed to obtain Google access token: {ex.Message}", ex);
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task<ServiceAccountCredential> LoadCredentialAsync(CancellationToken ct)
    {
        GoogleCredential googleCredential;

        if (!string.IsNullOrWhiteSpace(_options.ServiceAccountJson))
        {
            _logger.LogDebug("Loading FCM credentials from inline JSON.");
            using var ms = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(_options.ServiceAccountJson));
            googleCredential = GoogleCredential.FromStream(ms);
        }
        else if (!string.IsNullOrWhiteSpace(_options.ServiceAccountKeyFilePath))
        {
            _logger.LogDebug("Loading FCM credentials from file: {Path}", _options.ServiceAccountKeyFilePath);
            googleCredential = await GoogleCredential.FromFileAsync(_options.ServiceAccountKeyFilePath, ct);
        }
        else
        {
            throw new PushKitConfigurationException(
                "No FCM credentials provided. Set FcmOptions.ServiceAccountJson or FcmOptions.ServiceAccountKeyFilePath.");
        }

        var sac = googleCredential
            .CreateScoped(_options.TokenScope)
            .UnderlyingCredential as ServiceAccountCredential;

        return sac ?? throw new PushKitAuthException(
            "Credential is not a ServiceAccountCredential. Ensure the JSON is a valid Firebase service account key.");
    }
}
