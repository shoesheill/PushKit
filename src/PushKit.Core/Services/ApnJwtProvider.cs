using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PushKit.Configuration;
using PushKit.Exceptions;
using PushKit.Interfaces;

namespace PushKit.Services;

/// <summary>
/// Mints and caches the ES256 provider-authentication JWT used to authorize APNs requests.
/// Registered as a singleton so the cache survives across the per-request/per-scope
/// <see cref="ApnSender"/> instances created by the typed HttpClient factory — Apple throttles
/// (429 TooManyProviderTokenUpdates) if a fresh token is generated more often than necessary.
/// Apple allows a token to be reused for up to 60 minutes; we refresh at 45 to be safe.
/// Thread-safe: concurrent refresh attempts are serialised via a SemaphoreSlim.
/// </summary>
internal sealed class ApnJwtProvider : IApnJwtProvider
{
    private readonly ApnOptions _options;
    private readonly ILogger<ApnJwtProvider> _logger;
    private readonly SemaphoreSlim _lock = new(1, 1);

    private string? _cachedJwt;
    private DateTime _jwtExpiresAt = DateTime.MinValue;
    private ECDsa? _ecdsa;
    private const int JwtLifetimeMinutes = 45;

    public ApnJwtProvider(IOptions<ApnOptions> options, ILogger<ApnJwtProvider> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public async Task<string> GetJwtAsync(CancellationToken cancellationToken = default)
    {
        if (_cachedJwt is not null && DateTime.UtcNow < _jwtExpiresAt)
            return _cachedJwt;

        await _lock.WaitAsync(cancellationToken);
        try
        {
            if (_cachedJwt is not null && DateTime.UtcNow < _jwtExpiresAt)
                return _cachedJwt;

            _cachedJwt = CreateJwt();
            _jwtExpiresAt = DateTime.UtcNow.AddMinutes(JwtLifetimeMinutes);
            _logger.LogDebug("[APNs] JWT refreshed. Valid until {Expiry:HH:mm:ss} UTC", _jwtExpiresAt);
            return _cachedJwt;
        }
        finally
        {
            _lock.Release();
        }
    }

    private string CreateJwt()
    {
        var iat = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var header = Base64UrlEncode(JsonSerializer.SerializeToUtf8Bytes(new { alg = "ES256", kid = _options.P8PrivateKeyId }));
        var payload = Base64UrlEncode(JsonSerializer.SerializeToUtf8Bytes(new { iss = _options.TeamId, iat }));
        var signingInput = Encoding.UTF8.GetBytes($"{header}.{payload}");

        _ecdsa ??= LoadEcdsa();

        var signature = _ecdsa.SignData(signingInput, HashAlgorithmName.SHA256);
        return $"{header}.{payload}.{Base64UrlEncode(signature)}";
    }

    private ECDsa LoadEcdsa()
    {
        try
        {
            var keyBytes = Convert.FromBase64String(_options.P8PrivateKey.Trim());
            var ecdsa = ECDsa.Create();
            ecdsa.ImportPkcs8PrivateKey(keyBytes, out _);
            return ecdsa;
        }
        catch (Exception ex)
        {
            throw new PushKitConfigurationException(
                $"Failed to load APNs p8 private key: {ex.Message}. " +
                "Ensure P8PrivateKey is the base64 content only (no header/footer/whitespace).");
        }
    }

    private static string Base64UrlEncode(byte[] input) =>
        Convert.ToBase64String(input)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
}
