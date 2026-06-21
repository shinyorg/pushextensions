using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Shiny.Extensions.Push;

namespace Shiny.Extensions.Push.Apns;


public static class ApnsRegistration
{
    /// <summary>
    /// Adds the default (keyless) APNs provider. Use this when the server pushes for a single app.
    /// Registrations with a null/empty <see cref="DeviceRegistration.AppId"/> route here.
    /// </summary>
    public static IPushBuilder AddApns(this IPushBuilder builder, Action<ApnsOptions> configure)
        => builder.AddApns(Options.DefaultName, configure);


    /// <summary>
    /// Adds a keyed APNs provider for multi-app servers. The <paramref name="key"/> matches a device's
    /// <see cref="DeviceRegistration.AppId"/>, so each app gets its own bundle id, team, .p8 key and
    /// JWT cache. Call once per app. Honours each registration's sandbox/production environment unless
    /// <see cref="ApnsOptions.ForceEnvironment"/> is set.
    /// </summary>
    public static IPushBuilder AddApns(this IPushBuilder builder, string key, Action<ApnsOptions> configure)
    {
        builder.Services
            .AddOptions<ApnsOptions>(key)
            .Configure(configure)
            // Manual validation (no reflection) keeps the provider AOT/trim-safe.
            .Validate(
                o => !string.IsNullOrWhiteSpace(o.TeamId)
                    && !string.IsNullOrWhiteSpace(o.KeyId)
                    && !string.IsNullOrWhiteSpace(o.BundleId)
                    && (!string.IsNullOrWhiteSpace(o.PrivateKey) || !string.IsNullOrWhiteSpace(o.PrivateKeyPath)),
                "APNs requires TeamId, KeyId, BundleId and one of PrivateKey/PrivateKeyPath."
            );

        // One JWT cache (and ECDsa key) per app key, owned by DI for disposal.
        builder.Services.AddKeyedSingleton<ApnsJwtProvider>(key, (sp, k) =>
            new ApnsJwtProvider(sp.GetRequiredService<IOptionsMonitor<ApnsOptions>>().Get((string)k!)));

        // The HTTP/2 client is shared across all apps (same APNs hosts; per-request auth/URI differ).
        builder.Services
            .AddHttpClient(ApnsProvider.HttpClientName)
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                // Keep the HTTP/2 connection warm and multiplexed across sends.
                EnableMultipleHttp2Connections = true,
                PooledConnectionLifetime = TimeSpan.FromMinutes(10),
                AutomaticDecompression = DecompressionMethods.All
            });

        // Each key contributes its own IPushProvider; the manager routes by AppId via CanDeliver.
        builder.Services.AddSingleton<IPushProvider>(sp => new ApnsProvider(
            key,
            sp.GetRequiredService<IHttpClientFactory>(),
            sp.GetRequiredKeyedService<ApnsJwtProvider>(key),
            sp.GetRequiredService<IOptionsMonitor<ApnsOptions>>(),
            sp.GetRequiredService<ILogger<ApnsProvider>>()
        ));

        return builder;
    }
}
