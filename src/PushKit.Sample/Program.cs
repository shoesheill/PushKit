using PushKit.Builders;
using PushKit.Extensions;
using PushKit.Interfaces;
using PushKit.Models;
using PushKit.Sample.Models;
using PushKit.Sample.Services;

var builder = WebApplication.CreateBuilder(args);

// ══════════════════════════════════════════════════════════════════════════════
// REGISTRATION
// ══════════════════════════════════════════════════════════════════════════════

// Option A — Register both FCM + APNs from appsettings.json (recommended)
builder.Services.AddPushKit(builder.Configuration);

// Option B — FCM only with inline config (env-var friendly for containers)
// builder.Services.AddFcmSender(opts =>
// {
//     opts.ProjectId            = Environment.GetEnvironmentVariable("FIREBASE_PROJECT_ID")!;
//     opts.ServiceAccountJson   = Environment.GetEnvironmentVariable("FIREBASE_SA_JSON");
//     opts.MaxRetryAttempts     = 3;
//     opts.BatchParallelism     = 100;
// });

// Option C — APNs only with inline config
// builder.Services.AddApnSender(opts =>
// {
//     opts.P8PrivateKey   = Environment.GetEnvironmentVariable("APN_P8_KEY")!;
//     opts.P8PrivateKeyId = Environment.GetEnvironmentVariable("APN_KEY_ID")!;
//     opts.TeamId         = Environment.GetEnvironmentVariable("APN_TEAM_ID")!;
//     opts.BundleId       = "com.mycompany.myapp";
//     opts.Environment    = ApnEnvironment.Production;
// });

// Register application services
builder.Services.AddScoped<NotificationService>();
builder.Services.AddOpenApi();

var app = builder.Build();
app.MapOpenApi();

// ══════════════════════════════════════════════════════════════════════════════
// FCM ENDPOINTS
// ══════════════════════════════════════════════════════════════════════════════

var fcm = app.MapGroup("/fcm").WithTags("FCM");

// ── Single device token (data only) ──────────────────────────────────────────
fcm.MapPost("/data/token", async (DataToTokenRequest req, IFcmSender fcmSender) =>
{
    var msg = PushMessageBuilder.Create()
        .WithData("event", req.Event)
        .WithData("payload", req.JsonPayload)
        .WithData("sentAt", DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString())
        .WithAndroid(priority: AndroidPriority.High, ttlSeconds: 86_400)
        .WithApns(a =>
        {
            a.Headers["apns-priority"] = "10";
            a.Headers["apns-push-type"] = "background";
        })
        .Build();

    var result = await fcmSender.SendToTokenAsync(req.DeviceToken, msg);

    if (result.IsTokenInvalid)
    {
        // In a real app: await userService.RemoveFcmTokenAsync(req.DeviceToken);
        return Results.Ok(new { sent = false, reason = "invalid_token", result.ErrorCode });
    }

    return result.IsSuccess
        ? Results.Ok(new { sent = true, result.MessageId })
        : Results.Problem(result.ErrorMessage, statusCode: 502);
});

// ── Data + visible notification ────────────────────────────────────────────
fcm.MapPost("/notification/token", async (NotifyTokenRequest req, IFcmSender fcmSender) =>
{
    var msg = PushMessageBuilder.Create()
        .WithNotification(req.Title, req.Body, req.ImageUrl)
        .WithData("event", req.Event)
        .WithData("deepLink", req.DeepLink ?? string.Empty)
        .WithAndroid(priority: AndroidPriority.High, channelId: "important")
        .Build();

    var result = await fcmSender.SendToTokenAsync(req.DeviceToken, msg);
    return result.IsSuccess ? Results.Ok(result) : Results.BadRequest(result);
});

// ── Topic broadcast ────────────────────────────────────────────────────────
fcm.MapPost("/data/topic", async (TopicRequest req, IFcmSender fcmSender) =>
{
    var msg = PushMessageBuilder.Create()
        .WithData("event", req.Event)
        .WithData("topic", req.Topic)
        .WithNotification(req.Title, req.Body)
        .Build();

    var result = await fcmSender.SendToTopicAsync(req.Topic, msg);
    return result.IsSuccess ? Results.Ok(result) : Results.BadRequest(result);
});

// ── Condition send ─────────────────────────────────────────────────────────
fcm.MapPost("/data/condition", async (ConditionRequest req, IFcmSender fcmSender) =>
{
    // e.g. condition: "'sports' in topics && 'news' in topics"
    var msg = PushMessageBuilder.Create()
        .WithData("event", req.Event)
        .Build();

    var result = await fcmSender.SendToConditionAsync(req.Condition, msg);
    return result.IsSuccess ? Results.Ok(result) : Results.BadRequest(result);
});

// ── Batch to many tokens ────────────────────────────────────────────────────
fcm.MapPost("/data/batch", async (BatchRequest req, IFcmSender fcmSender) =>
{
    var msg = PushMessageBuilder.Create()
        .WithData("event", req.Event)
        .WithData("payload", req.JsonPayload)
        .WithAndroid(priority: AndroidPriority.High)
        .Build();

    var batch = await fcmSender.SendBatchAsync(req.DeviceTokens, msg);

    // Return invalid tokens so the caller can remove them from their database
    return Results.Ok(new
    {
        batch.TotalCount,
        batch.SuccessCount,
        batch.FailureCount,
        batch.IsFullSuccess,
        InvalidTokens  = batch.InvalidTokens.ToList(),
        RetryableTokens = batch.RetryableTokens.ToList()
    });
});

// ── Dry run (validate payload, no delivery) ───────────────────────────────
fcm.MapPost("/validate", async (DataToTokenRequest req, IFcmSender fcmSender) =>
{
    var msg = PushMessageBuilder.Create()
        .WithData("event", req.Event)
        .WithData("payload", req.JsonPayload)
        .DryRun()     // ← FCM validates but doesn't deliver
        .Build();

    var result = await fcmSender.SendToTokenAsync(req.DeviceToken, msg);
    return result.IsSuccess
        ? Results.Ok(new { valid = true })
        : Results.BadRequest(new { valid = false, result.ErrorCode, result.ErrorMessage });
});

// ══════════════════════════════════════════════════════════════════════════════
// NATIVE APNs ENDPOINTS
// ══════════════════════════════════════════════════════════════════════════════

var apns = app.MapGroup("/apns").WithTags("APNs");

// ── Alert push ─────────────────────────────────────────────────────────────
apns.MapPost("/alert", async (ApnAlertRequest req, IApnSender apnSender) =>
{
    var msg = ApnMessageBuilder.Create()
        .WithAlert(req.Title, req.Body)
        .WithBadge(req.Badge ?? 1)
        .WithSound("default")
        .WithCustomData("event", req.Event)
        .WithCustomData("deepLink", req.DeepLink ?? string.Empty)
        .WithCollapseId(req.CollapseId ?? req.Event)
        .Build();

    var result = await apnSender.SendAsync(req.DeviceToken, msg);

    if (result.IsTokenInvalid)
    {
        // In a real app: await userService.RemoveApnTokenAsync(req.DeviceToken);
        return Results.Ok(new { sent = false, reason = "invalid_token" });
    }

    return result.IsSuccess ? Results.Ok(result) : Results.BadRequest(result);
});

// ── Silent background push ─────────────────────────────────────────────────
apns.MapPost("/background", async (ApnDataRequest req, IApnSender apnSender) =>
{
    var msg = ApnMessageBuilder.Create()
        .AsBackground()               // content-available=1, priority=5
        .WithCustomData("event", req.Event)
        .WithCustomData("payload", req.JsonPayload)
        .Build();

    var result = await apnSender.SendAsync(req.DeviceToken, msg);
    return result.IsSuccess ? Results.Ok(result) : Results.BadRequest(result);
});

// ── Batch APNs ─────────────────────────────────────────────────────────────
apns.MapPost("/batch", async (ApnBatchRequest req, IApnSender apnSender) =>
{
    var msg = ApnMessageBuilder.Create()
        .WithAlert(req.Title, req.Body)
        .WithSound("default")
        .WithCustomData("event", req.Event)
        .Build();

    var batch = await apnSender.SendBatchAsync(req.DeviceTokens, msg);
    return Results.Ok(new
    {
        batch.TotalCount,
        batch.SuccessCount,
        batch.FailureCount,
        InvalidTokens = batch.InvalidTokens.ToList()
    });
});

// ══════════════════════════════════════════════════════════════════════════════
// BUSINESS-LOGIC LAYER EXAMPLE
// ══════════════════════════════════════════════════════════════════════════════

app.MapPost("/notify/order-shipped", async (
    OrderShippedRequest req,
    NotificationService notifier) =>
{
    await notifier.NotifyOrderShippedAsync(
        req.FcmToken, req.ApnToken, req.OrderId, req.TrackingNumber);
    return Results.Accepted();
}).WithTags("Business");

app.Run();
