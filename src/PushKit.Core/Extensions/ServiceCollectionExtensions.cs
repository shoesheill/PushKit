using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Polly;
using Polly.Extensions.Http;
using PushKit.Configuration;
using PushKit.Interfaces;
using PushKit.Services;

namespace PushKit.Extensions;

/// <summary>
/// Registers PushKit services into the .NET DI container.
/// </summary>
public static class ServiceCollectionExtensions
{
    // ─── Main entry points ────────────────────────────────────────────────────

    /// <summary>
    /// Register PushKit with both FCM and APNs support,
    /// reading configuration from "PushKit:Fcm" and "PushKit:Apn" sections.
    /// </summary>
    public static IServiceCollection AddPushKit(this IServiceCollection services,IConfiguration configuration)
    {
        services.AddFcmSender(configuration);
        services.AddApnSender(configuration);
        return services;
    }

    /// <summary>Register only FCM support (no APNs).</summary>
    public static IServiceCollection AddFcmSender(
        this IServiceCollection services,
        IConfiguration configuration) =>
        services.AddFcmSender(o => configuration.GetSection(FcmOptions.SectionName).Bind(o));

    /// <summary>Register only APNs support (no FCM).</summary>
    public static IServiceCollection AddApnSender(
        this IServiceCollection services,
        IConfiguration configuration) =>
        services.AddApnSender(o => configuration.GetSection(ApnOptions.SectionName).Bind(o));

    // ─── Delegate-based overloads ─────────────────────────────────────────────

    /// <summary>Register FCM using an inline configuration delegate.</summary>
    public static IServiceCollection AddFcmSender(
        this IServiceCollection services,
        Action<FcmOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);

        services.Configure(configure);
        services.AddOptions<FcmOptions>().Validate(ValidateFcmOptions,
            "FcmOptions: ProjectId is required and at least one of ServiceAccountJson / ServiceAccountKeyFilePath must be set.");

        // Singleton: holds the cached OAuth2 token
        services.AddSingleton<IFcmTokenProvider, GoogleFcmTokenProvider>();

        // Named HttpClient with Polly retry
        services
            .AddHttpClient<IFcmSender, FcmSender>("PushKit.Fcm", (sp, client) =>
            {
                var opts = sp.GetRequiredService<IOptions<FcmOptions>>().Value;
                client.Timeout = TimeSpan.FromSeconds(opts.RequestTimeoutSeconds);
            })
            .AddPolicyHandler(static (sp, _) =>
            {
                var opts = sp.GetRequiredService<IOptions<FcmOptions>>().Value;
                return BuildRetryPolicy(opts.MaxRetryAttempts, opts.RetryBaseDelayMs, "FCM");
            });

        return services;
    }

    /// <summary>Register APNs using an inline configuration delegate.</summary>
    public static IServiceCollection AddApnSender(
        this IServiceCollection services,
        Action<ApnOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);

        services.Configure(configure);
        services.AddOptions<ApnOptions>().Validate(ValidateApnOptions,
            "ApnOptions: P8PrivateKey, P8PrivateKeyId, TeamId, and BundleId are all required.");

        // APNs requires HTTP/2 — configure the primary handler accordingly
        services
            .AddHttpClient<IApnSender, ApnSender>("PushKit.Apn", (sp, client) =>
            {
                var opts = sp.GetRequiredService<IOptions<ApnOptions>>().Value;
                client.BaseAddress = new Uri(opts.GetApnHost());
                client.Timeout = TimeSpan.FromSeconds(opts.RequestTimeoutSeconds);
            })
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                // HTTP/2 is mandatory for APNs; allow upgrade
                EnableMultipleHttp2Connections = true,
                PooledConnectionIdleTimeout = TimeSpan.FromMinutes(10),
                KeepAlivePingDelay = TimeSpan.FromSeconds(60),
                KeepAlivePingTimeout = TimeSpan.FromSeconds(30)
            })
            .AddPolicyHandler((sp, _) =>
            {
                var opts = sp.GetRequiredService<IOptions<ApnOptions>>().Value;
                return BuildRetryPolicy(opts.MaxRetryAttempts, 500, "APNs");
            });

        return services;
    }

    // ─── Advanced: replace the token provider ─────────────────────────────────

    /// <summary>
    /// Replace the default Google service account token provider with a custom implementation.
    /// Call this AFTER <see cref="AddFcmSender(IServiceCollection, Action{FcmOptions})"/>.
    /// 
    /// Use cases: GKE Workload Identity, Azure Managed Identity, unit test mocks.
    /// </summary>
    public static IServiceCollection UseCustomFcmTokenProvider<TProvider>(this IServiceCollection services)
        where TProvider : class, IFcmTokenProvider
    {
        var existing = services.FirstOrDefault(d => d.ServiceType == typeof(IFcmTokenProvider));
        if (existing is not null) services.Remove(existing);
        services.AddSingleton<IFcmTokenProvider, TProvider>();
        return services;
    }

    // ─── Polly retry policy ───────────────────────────────────────────────────

    private static IAsyncPolicy<HttpResponseMessage> BuildRetryPolicy(
        int maxRetries, int baseDelayMs, string providerName)
    {
        if (maxRetries <= 0)
            return Policy.NoOpAsync<HttpResponseMessage>();

        return HttpPolicyExtensions
            .HandleTransientHttpError()
            .OrResult(r => r.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
            .WaitAndRetryAsync(
                retryCount: maxRetries,
                sleepDurationProvider: (attempt, outcome, _) =>
                {
                    // Honour provider's Retry-After header if present
                    if (outcome.Result?.Headers.RetryAfter?.Delta is { } retryAfter)
                        return retryAfter;

                    // Exponential backoff with jitter
                    var exponential = TimeSpan.FromMilliseconds(baseDelayMs * Math.Pow(2, attempt - 1));
                    var jitter = TimeSpan.FromMilliseconds(Random.Shared.Next(0, 200));
                    return exponential + jitter;
                },
                onRetryAsync: (outcome, wait, attempt, _) =>
                {
                    var reason = outcome.Exception?.Message ?? outcome.Result?.StatusCode.ToString();
                    Console.WriteLine($"[PushKit/{providerName}] Retry {attempt}/{maxRetries} after {wait.TotalMilliseconds:F0}ms — {reason}");
                    return Task.CompletedTask;
                });
    }

    // ─── Validators ───────────────────────────────────────────────────────────

    private static bool ValidateFcmOptions(FcmOptions o) =>
        !string.IsNullOrWhiteSpace(o.ProjectId) &&
        (!string.IsNullOrWhiteSpace(o.ServiceAccountJson) ||
         !string.IsNullOrWhiteSpace(o.ServiceAccountKeyFilePath));

    private static bool ValidateApnOptions(ApnOptions o) =>
        !string.IsNullOrWhiteSpace(o.P8PrivateKey) &&
        !string.IsNullOrWhiteSpace(o.P8PrivateKeyId) &&
        !string.IsNullOrWhiteSpace(o.TeamId) &&
        !string.IsNullOrWhiteSpace(o.BundleId);
}
