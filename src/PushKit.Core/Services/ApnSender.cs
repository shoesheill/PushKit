using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PushKit.Configuration;
using PushKit.Exceptions;
using PushKit.Interfaces;
using PushKit.Models;
using PushKit.Models.Internal;

namespace PushKit.Services;

/// <summary>
/// Sends native APNs messages directly to Apple's HTTP/2 servers using p8 ES256 JWT authentication.
/// 
/// Advantages over CorePush:
/// - JWT is cached and only refreshed after 45 minutes (Apple allows 60 min max; we're conservative)
/// - Built-in batch parallelism cap to avoid Apple HTTP 429 throttling
/// - Result-based error model — NEVER throws on APNs protocol errors
/// - Thread-safe JWT generation via SemaphoreSlim
/// - All push types supported: alert, background, voip, location, etc.
/// </summary>
internal sealed class ApnSender : IApnSender
{
    private readonly HttpClient _http;
    private readonly ApnOptions _options;
    private readonly ILogger<ApnSender> _logger;

    // JWT token cache — Apple allows up to 60 min; we refresh at 45 min to be safe
    private string? _cachedJwt;
    private DateTime _jwtExpiresAt = DateTime.MinValue;
    private readonly SemaphoreSlim _jwtLock = new(1, 1);
    private ECDsa? _ecdsa;
    private const int JwtLifetimeMinutes = 45;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false
    };

    public ApnSender(HttpClient http, IOptions<ApnOptions> options, ILogger<ApnSender> logger)
    {
        _http = http;
        _options = options.Value;
        _logger = logger;
        ValidateConfiguration();
    }

    // ─── Public API ───────────────────────────────────────────────────────────

    public async Task<PushResult> SendAsync(string deviceToken, ApnMessage message, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceToken);
        ArgumentNullException.ThrowIfNull(message);

        var target = PushTarget.Token(deviceToken);
        _logger.LogInformation("[APNs] Sending {PushType} to {Token}", message.PushType, target.Masked());

        try
        {
            return await ExecuteAsync(deviceToken, message, ct);
        }
        catch (PushKitException) { throw; }
        catch (Exception ex)
        {
            throw new PushKitTransportException($"[APNs] Transport error: {ex.Message}", inner: ex);
        }
    }

    public async Task<BatchPushResult> SendBatchAsync(
        IEnumerable<string> deviceTokens,
        ApnMessage message,
        CancellationToken ct = default)
    {
        var tokens = deviceTokens
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Distinct()
            .ToList();

        if (tokens.Count == 0)
        {
            _logger.LogWarning("[APNs:Batch] Called with zero valid tokens.");
            return new BatchPushResult { Results = [] };
        }

        _logger.LogInformation("[APNs:Batch] Sending to {Count} tokens (parallelism={P})",
            tokens.Count, _options.BatchParallelism);

        // Apple is more sensitive to hammering than FCM. Cap and add a small jitter.
        using var semaphore = new SemaphoreSlim(_options.BatchParallelism);

        var tasks = tokens.Select(async token =>
        {
            await semaphore.WaitAsync(ct);
            try { return await SendAsync(token, message, ct); }
            finally { semaphore.Release(); }
        });

        var results = await Task.WhenAll(tasks);
        var batch = new BatchPushResult { Results = results };

        _logger.LogInformation("[APNs:Batch] Done: {Ok}/{Total} ok, {Fail} failed",
            batch.SuccessCount, batch.TotalCount, batch.FailureCount);

        return batch;
    }

    // ─── Core execution ───────────────────────────────────────────────────────

    private async Task<PushResult> ExecuteAsync(string deviceToken, ApnMessage message, CancellationToken ct)
    {
        var host = _options.GetApnHost();
        var url = $"{host}/3/device/{deviceToken}";
        var jwt = await GetOrRefreshJwtAsync(ct);
        var payloadJson = BuildPayload(message);
        var target = PushTarget.Token(deviceToken);

        _logger.LogDebug("[APNs] POST {Url} | Payload: {Payload}", url, payloadJson);

        using var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Version = new Version(2, 0),  // HTTP/2 required by Apple
            VersionPolicy = HttpVersionPolicy.RequestVersionOrHigher
        };

        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", jwt);
        req.Headers.TryAddWithoutValidation("apns-topic", _options.BundleId);
        req.Headers.TryAddWithoutValidation("apns-push-type", message.PushType.ToString().ToLowerInvariant());
        req.Headers.TryAddWithoutValidation("apns-priority", message.Priority.ToString());

        if (message.ExpirationSeconds > 0)
        {
            var expiry = DateTimeOffset.UtcNow.AddSeconds(message.ExpirationSeconds).ToUnixTimeSeconds();
            req.Headers.TryAddWithoutValidation("apns-expiration", expiry.ToString());
        }

        if (!string.IsNullOrWhiteSpace(message.CollapseId))
            req.Headers.TryAddWithoutValidation("apns-collapse-id", message.CollapseId);

        req.Content = new StringContent(payloadJson, Encoding.UTF8, "application/json");

        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        var respBody = await resp.Content.ReadAsStringAsync(ct);

        _logger.LogDebug("[APNs] HTTP {Status} | Body: {Body}", (int)resp.StatusCode, respBody);

        if (resp.IsSuccessStatusCode)
        {
            // apns-id response header is the message identifier
            var apnsId = resp.Headers.TryGetValues("apns-id", out var vals)
                ? vals.FirstOrDefault() ?? "ok"
                : "ok";
            _logger.LogInformation("[APNs] Delivered → apns-id: {Id}", apnsId);
            return PushResult.Success(target, apnsId, (int)resp.StatusCode);
        }

        return ParseApnError(target, resp, respBody);
    }

    private PushResult ParseApnError(PushTarget target, HttpResponseMessage resp, string body)
    {
        string code;
        string msg;

        try
        {
            var err = JsonSerializer.Deserialize<ApnErrorResponse>(body, JsonOpts);
            code = err?.Reason ?? resp.StatusCode.ToString();
            msg = string.IsNullOrWhiteSpace(body) ? resp.ReasonPhrase ?? code : body;
        }
        catch
        {
            code = resp.StatusCode.ToString();
            msg = body;
        }

        _logger.LogWarning("[APNs] Failed → {Token} | {Code}", target.Masked(), code);
        return PushResult.Failure(target, code, msg, (int)resp.StatusCode);
    }

    // ─── Payload builder ─────────────────────────────────────────────────────

    private static string BuildPayload(ApnMessage message)
    {
        // Build as a dictionary so we can merge custom fields at the root level
        var root = new Dictionary<string, object>();

        var aps = new ApnApsInternal
        {
            Badge = message.Aps.Badge,
            Sound = message.Aps.Sound,
            ContentAvailable = message.Aps.ContentAvailable,
            MutableContent = message.Aps.MutableContent,
            Category = message.Aps.Category,
            ThreadId = message.Aps.ThreadId
        };

        if (message.Aps.Alert is { } alert)
        {
            aps.Alert = new ApnAlertInternal
            {
                Title = alert.Title,
                Subtitle = alert.Subtitle,
                Body = alert.Body
            };
        }

        root["aps"] = aps;

        // Merge custom root-level fields
        foreach (var (k, v) in message.CustomData)
            root[k] = v;

        return JsonSerializer.Serialize(root, JsonOpts);
    }

    // ─── JWT management ───────────────────────────────────────────────────────

    private async Task<string> GetOrRefreshJwtAsync(CancellationToken ct)
    {
        if (_cachedJwt is not null && DateTime.UtcNow < _jwtExpiresAt)
            return _cachedJwt;

        await _jwtLock.WaitAsync(ct);
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
            _jwtLock.Release();
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

    private void ValidateConfiguration()
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(_options.P8PrivateKey))      errors.Add("P8PrivateKey is required.");
        if (string.IsNullOrWhiteSpace(_options.P8PrivateKeyId))    errors.Add("P8PrivateKeyId is required.");
        if (string.IsNullOrWhiteSpace(_options.TeamId))            errors.Add("TeamId is required.");
        if (string.IsNullOrWhiteSpace(_options.BundleId))          errors.Add("BundleId is required.");

        if (errors.Count > 0)
            throw new PushKitConfigurationException(
                $"ApnOptions invalid: {string.Join(" | ", errors)}");
    }
}
