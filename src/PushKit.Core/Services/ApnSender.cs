using System.Net.Http.Headers;
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
/// - JWT is cached and only refreshed after 45 minutes via the singleton <see cref="IApnJwtProvider"/>
///   (Apple allows 60 min max; we're conservative)
/// - Built-in batch parallelism cap to avoid Apple HTTP 429 throttling
/// - Result-based error model — NEVER throws on APNs protocol errors
/// - All push types supported: alert, background, voip, location, etc.
/// </summary>
internal sealed class ApnSender : IApnSender
{
    private readonly HttpClient _http;
    private readonly ApnOptions _options;
    private readonly IApnJwtProvider _jwtProvider;
    private readonly ILogger<ApnSender> _logger;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false
    };

    public ApnSender(HttpClient http, IOptions<ApnOptions> options, IApnJwtProvider jwtProvider, ILogger<ApnSender> logger)
    {
        _http = http;
        _options = options.Value;
        _jwtProvider = jwtProvider;
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
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                // A single token's transport failure (timeout, dropped connection, etc.) must
                // not blow up Task.WhenAll and lose every other result in the batch — isolate it
                // as a retryable per-token failure instead. Real caller-requested cancellation
                // still propagates.
                var target = PushTarget.Token(token);
                _logger.LogError(ex, "[APNs:Batch] Transport error for {Token}", target.Masked());
                return PushResult.Failure(target, "TRANSPORT_ERROR", ex.Message);
            }
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
        var jwt = await _jwtProvider.GetJwtAsync(ct);
        var payloadJson = BuildPayload(message);
        var target = PushTarget.Token(deviceToken);

        _logger.LogDebug("[APNs] POST {Url} | Payload: {Payload}", url, payloadJson);

        using var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Version = new Version(2, 0),  // HTTP/2 required by Apple
            VersionPolicy = HttpVersionPolicy.RequestVersionOrHigher
        };

        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", jwt);
        req.Headers.TryAddWithoutValidation("apns-topic", ResolveTopic(message));
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

    /// <summary>
    /// Resolves the apns-topic header. An explicit <see cref="ApnMessage.Topic"/> wins; otherwise the
    /// bundle id is used, with a ".voip" suffix automatically appended for VoIP pushes — Apple rejects
    /// a VoIP push (apns-push-type: voip) whose topic is not "{BundleId}.voip" with BadTopic (400).
    /// </summary>
    private string ResolveTopic(ApnMessage message)
    {
        if (!string.IsNullOrWhiteSpace(message.Topic))
            return message.Topic;

        if (message.PushType == ApnPushType.Voip &&
            !_options.BundleId.EndsWith(".voip", StringComparison.OrdinalIgnoreCase))
            return _options.BundleId + ".voip";

        return _options.BundleId;
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
