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
/// Sends FCM messages via the HTTP v1 API.
/// - Returns <see cref="PushResult"/> for all FCM protocol errors (no throwing on UNREGISTERED, etc.)
/// - Built-in Polly retry via the named HttpClient (configured in DI registration)
/// - Batch send with configurable parallelism cap
/// </summary>
internal sealed class FcmSender : IFcmSender
{
    private readonly HttpClient _http;
    private readonly IFcmTokenProvider _tokenProvider;
    private readonly FcmOptions _options;
    private readonly ILogger<FcmSender> _logger;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false
    };

    public FcmSender(
        HttpClient http,
        IFcmTokenProvider tokenProvider,
        IOptions<FcmOptions> options,
        ILogger<FcmSender> logger)
    {
        _http = http;
        _tokenProvider = tokenProvider;
        _options = options.Value;
        _logger = logger;
    }

    // ─── Convenience overloads ────────────────────────────────────────────────

    public Task<PushResult> SendToTokenAsync(string token, PushMessage msg, CancellationToken ct = default) =>
        SendAsync(PushTarget.Token(token), msg, ct);

    public Task<PushResult> SendToTopicAsync(string topic, PushMessage msg, CancellationToken ct = default) =>
        SendAsync(PushTarget.Topic(topic), msg, ct);

    public Task<PushResult> SendToConditionAsync(string cond, PushMessage msg, CancellationToken ct = default) =>
        SendAsync(PushTarget.Condition(cond), msg, ct);

    // ─── Core send ────────────────────────────────────────────────────────────

    public async Task<PushResult> SendAsync(PushTarget target, PushMessage message, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(message);

        var traceId = message.MessageId ?? Guid.NewGuid().ToString("N")[..8];

        _logger.LogInformation("[FCM:{Id}] Sending to {Type}:{Target}",
            traceId, target.Type, target.Masked());

        try
        {
            return await ExecuteAsync(target, message, traceId, ct);
        }
        catch (PushKitException) { throw; }
        catch (Exception ex)
        {
            throw new PushKitTransportException(
                $"[FCM:{traceId}] Unexpected transport error: {ex.Message}", inner: ex);
        }
    }

    // ─── Batch send ───────────────────────────────────────────────────────────

    public async Task<BatchPushResult> SendBatchAsync(
        IEnumerable<string> deviceTokens,
        PushMessage message,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(deviceTokens);
        ArgumentNullException.ThrowIfNull(message);

        var tokens = deviceTokens
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Distinct()
            .ToList();

        if (tokens.Count == 0)
        {
            _logger.LogWarning("[FCM:Batch] Called with zero valid tokens.");
            return new BatchPushResult { Results = [] };
        }

        _logger.LogInformation("[FCM:Batch] Sending to {Count} tokens (parallelism={P})",
            tokens.Count, _options.BatchParallelism);

        using var semaphore = new SemaphoreSlim(_options.BatchParallelism);

        var tasks = tokens.Select(async token =>
        {
            await semaphore.WaitAsync(ct);
            try { return await SendToTokenAsync(token, message, ct); }
            finally { semaphore.Release(); }
        });

        var results = await Task.WhenAll(tasks);
        var batch = new BatchPushResult { Results = results };

        _logger.LogInformation("[FCM:Batch] Done: {Ok}/{Total} ok, {Fail} failed",
            batch.SuccessCount, batch.TotalCount, batch.FailureCount);

        return batch;
    }

    // ─── Private helpers ──────────────────────────────────────────────────────

    private async Task<PushResult> ExecuteAsync(
        PushTarget target, PushMessage message, string traceId, CancellationToken ct)
    {
        var url = $"{_options.FcmBaseUrl}/v1/projects/{_options.ProjectId}/messages:send";
        var token = await _tokenProvider.GetAccessTokenAsync(ct);
        var body = BuildRequestBody(target, message);
        var json = JsonSerializer.Serialize(body, JsonOpts);

        _logger.LogDebug("[FCM:{Id}] POST {Url} | Payload: {Json}", traceId, url, json);

        using var req = new HttpRequestMessage(HttpMethod.Post, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        req.Content = new StringContent(json, Encoding.UTF8, "application/json");

        using var resp = await _http.SendAsync(req, ct);
        var responseBody = await resp.Content.ReadAsStringAsync(ct);

        _logger.LogDebug("[FCM:{Id}] HTTP {Status} | Body: {Body}", traceId, (int)resp.StatusCode, responseBody);

        if (resp.IsSuccessStatusCode)
        {
            var ok = JsonSerializer.Deserialize<FcmSendResponse>(responseBody, JsonOpts);
            _logger.LogInformation("[FCM:{Id}] Accepted → {Name}", traceId, ok?.Name);
            return PushResult.Success(target, ok?.Name ?? traceId, (int)resp.StatusCode);
        }

        return ParseError(target, resp, responseBody, traceId);
    }

    private PushResult ParseError(
        PushTarget target, HttpResponseMessage resp, string body, string traceId)
    {
        string code;
        string msg;

        try
        {
            var err = JsonSerializer.Deserialize<FcmErrorEnvelope>(body, JsonOpts)?.Error;
            // FCM puts the specific code (e.g. UNREGISTERED) inside details[].errorCode
            code = err?.Details?.FirstOrDefault()?.ErrorCode
                   ?? err?.Status
                   ?? resp.StatusCode.ToString();
            msg = err?.Message ?? body;
        }
        catch
        {
            code = resp.StatusCode.ToString();
            msg = body;
        }

        _logger.LogWarning("[FCM:{Id}] Failed → {Target} | {Code}: {Message}",
            traceId, target.Masked(), code, msg);

        return PushResult.Failure(target, code, msg, (int)resp.StatusCode);
    }

    private static FcmSendRequest BuildRequestBody(PushTarget target, PushMessage msg)
    {
        var payload = new FcmMessage
        {
            Data = msg.Data.Count > 0 ? msg.Data : null,
            Notification = msg.Notification is { } n
                ? new FcmNotification { Title = n.Title, Body = n.Body, Image = n.ImageUrl }
                : null
        };

        switch (target.Type)
        {
            case TargetType.Token:     payload.Token     = target.Value; break;
            case TargetType.Topic:     payload.Topic     = target.Value; break;
            case TargetType.Condition: payload.Condition = target.Value; break;
        }

        if (msg.Android is { } a)
        {
            payload.Android = new FcmAndroid
            {
                Priority = a.Priority == AndroidPriority.High ? "high" : "normal",
                CollapseKey = a.CollapseKey,
                Ttl = a.TtlSeconds.HasValue ? $"{a.TtlSeconds}s" : null,
                Notification = a.ChannelId is not null
                    ? new FcmAndroidNotification { ChannelId = a.ChannelId }
                    : null
            };
        }

        if (msg.Apns is { } apns)
        {
            payload.Apns = new FcmApns
            {
                Headers = apns.Headers.Count > 0 ? apns.Headers : null,
                Payload = apns.ApsPayload.Count > 0 ? apns.ApsPayload : null
            };
        }

        if (msg.WebPush is { } wp)
        {
            payload.WebPush = new FcmWebPush
            {
                Headers = wp.Headers.Count > 0 ? wp.Headers : null,
                Data    = wp.Data.Count    > 0 ? wp.Data    : null
            };
        }

        return new FcmSendRequest { ValidateOnly = msg.ValidateOnly, Message = payload };
    }
}
