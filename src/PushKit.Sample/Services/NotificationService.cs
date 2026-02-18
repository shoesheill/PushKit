using System.Text.Json;
using PushKit.Builders;
using PushKit.Interfaces;
using PushKit.Models;

namespace PushKit.Sample.Services;

/// <summary>
/// Real-world business service demonstrating best practices for PushKit usage:
/// - Route to FCM or APNs based on which token is available
/// - Clean up invalid tokens automatically
/// - Log structured data for observability
/// - Never let push failures break the main business flow
/// </summary>
public sealed class NotificationService
{
    private readonly IFcmSender _fcm;
    private readonly IApnSender _apn;
    private readonly ILogger<NotificationService> _logger;

    public NotificationService(IFcmSender fcm, IApnSender apn, ILogger<NotificationService> logger)
    {
        _fcm = fcm;
        _apn = apn;
        _logger = logger;
    }

    /// <summary>
    /// Notify a user their order shipped. Sends to FCM (Android/Web) and/or APNs (iOS) 
    /// based on which tokens exist. Never throws — push failures are logged only.
    /// </summary>
    public async Task NotifyOrderShippedAsync(
        string? fcmToken,
        string? apnToken,
        string orderId,
        string trackingNumber,
        CancellationToken ct = default)
    {
        var payload = JsonSerializer.Serialize(new
        {
            orderId,
            trackingNumber,
            shippedAt = DateTimeOffset.UtcNow.ToString("O")
        });

        var tasks = new List<Task>();

        if (!string.IsNullOrWhiteSpace(fcmToken))
            tasks.Add(SendFcmOrderShippedAsync(fcmToken, orderId, trackingNumber, payload, ct));

        if (!string.IsNullOrWhiteSpace(apnToken))
            tasks.Add(SendApnOrderShippedAsync(apnToken, orderId, trackingNumber, payload, ct));

        if (tasks.Count == 0)
        {
            _logger.LogWarning("[Notify] No tokens available for order {OrderId}", orderId);
            return;
        }

        await Task.WhenAll(tasks);
    }

    private async Task SendFcmOrderShippedAsync(
        string token, string orderId, string tracking, string payload, CancellationToken ct)
    {
        try
        {
            var msg = PushMessageBuilder.Create()
                .WithData("event", "ORDER_SHIPPED")
                .WithData("orderId", orderId)
                .WithData("trackingNumber", tracking)
                .WithData("payload", payload)
                // Show a visible notification on Android/Web
                .WithNotification(
                    title: "Your order has shipped! 📦",
                    body: $"Tracking: {tracking}")
                .WithAndroid(
                    priority: AndroidPriority.High,
                    ttlSeconds: 86_400,
                    collapseKey: $"order_{orderId}",     // Keep only last per order
                    channelId: "orders")
                .WithApns(a =>
                {
                    a.Headers["apns-priority"] = "10";
                    a.Headers["apns-collapse-id"] = orderId;
                })
                .Build();

            var result = await _fcm.SendToTokenAsync(token, msg, ct);

            if (result.IsSuccess)
            {
                _logger.LogInformation("[FCM] Order shipped sent for {OrderId}", orderId);
            }
            else
            {
                _logger.LogWarning("[FCM] Send failed for order {OrderId}: [{Code}] {Msg}",
                    orderId, result.ErrorCode, result.ErrorMessage);

                if (result.IsTokenInvalid)
                {
                    _logger.LogWarning("[FCM] Token invalid — should be removed: {Token}", token[..8] + "…");
                    // TODO: await _userRepo.RemoveFcmTokenAsync(token, ct);
                }
            }
        }
        catch (Exception ex)
        {
            // Never let push failure break the order flow
            _logger.LogError(ex, "[FCM] Exception sending order shipped for {OrderId}", orderId);
        }
    }

    private async Task SendApnOrderShippedAsync(
        string token, string orderId, string tracking, string payload, CancellationToken ct)
    {
        try
        {
            var msg = ApnMessageBuilder.Create()
                .WithAlert("Your order has shipped! 📦", $"Tracking: {tracking}")
                .WithSound("default")
                .WithMutableContent()                       // Allow notification service extension
                .WithCategory("ORDER_SHIPPED")
                .WithThreadId($"order-{orderId}")           // Group notifications per order
                .WithCollapseId(orderId)                    // Replace prior notification
                .WithCustomData("event", "ORDER_SHIPPED")
                .WithCustomData("orderId", orderId)
                .WithCustomData("trackingNumber", tracking)
                .WithCustomData("payload", payload)
                .Build();

            var result = await _apn.SendAsync(token, msg, ct);

            if (result.IsSuccess)
            {
                _logger.LogInformation("[APNs] Order shipped sent for {OrderId}, apns-id: {Id}",
                    orderId, result.MessageId);
            }
            else
            {
                _logger.LogWarning("[APNs] Send failed for order {OrderId}: [{Code}] {Msg}",
                    orderId, result.ErrorCode, result.ErrorMessage);

                if (result.IsTokenInvalid)
                {
                    _logger.LogWarning("[APNs] Token invalid — should be removed.");
                    // TODO: await _userRepo.RemoveApnTokenAsync(token, ct);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[APNs] Exception sending order shipped for {OrderId}", orderId);
        }
    }

    /// <summary>
    /// Send a silent data-only update to many Android/Web clients.
    /// Returns the list of invalid tokens for the caller to clean up.
    /// </summary>
    public async Task<IReadOnlyList<string>> BroadcastDataUpdateAsync(
        IEnumerable<string> fcmTokens,
        string eventType,
        object eventData,
        CancellationToken ct = default)
    {
        var msg = PushMessageBuilder.Create()
            .WithData("event", eventType)
            .WithData("payload", JsonSerializer.Serialize(eventData))
            .WithData("timestamp", DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString())
            .Build();

        var batch = await _fcm.SendBatchAsync(fcmTokens, msg, ct);

        _logger.LogInformation("[FCM:Broadcast] {Event}: {Ok}/{Total} delivered",
            eventType, batch.SuccessCount, batch.TotalCount);

        return batch.InvalidTokens.ToList();
    }

    /// <summary>
    /// Send a batch of native APNs background pushes (e.g. to refresh cached content).
    /// Returns the list of invalid tokens for cleanup.
    /// </summary>
    public async Task<IReadOnlyList<string>> BroadcastApnSilentAsync(
        IEnumerable<string> apnTokens,
        string eventType,
        object eventData,
        CancellationToken ct = default)
    {
        var msg = ApnMessageBuilder.Create()
            .AsBackground()
            .WithCustomData("event", eventType)
            .WithCustomData("payload", JsonSerializer.Serialize(eventData))
            .Build();

        var batch = await _apn.SendBatchAsync(apnTokens, msg, ct);

        _logger.LogInformation("[APNs:Broadcast] {Event}: {Ok}/{Total} delivered",
            eventType, batch.SuccessCount, batch.TotalCount);

        return batch.InvalidTokens.ToList();
    }
}
