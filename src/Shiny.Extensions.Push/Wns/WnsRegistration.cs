using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Shiny.Extensions.Push;

namespace Shiny.Extensions.Push.Wns;


public static class WnsRegistration
{
    /// <summary>Adds the default (keyless) WNS provider. Routes registrations with null/empty AppId.</summary>
    public static IPushBuilder AddWns(this IPushBuilder builder, Action<WnsOptions> configure)
        => builder.AddWns(Options.DefaultName, configure);


    /// <summary>
    /// Adds the configuration-driven WNS provider: it resolves each device's <see cref="WnsOptions"/> from the
    /// registered <see cref="IPushConfigurationProvider"/> (see <c>UsePushConfiguration</c>) at send time, keyed by
    /// <see cref="DeviceRegistration.AppId"/>. Use this instead of the keyed <c>AddWns("key", …)</c> overloads —
    /// not both.
    /// </summary>
    public static IPushBuilder AddWns(this IPushBuilder builder)
    {
        builder.Services.AddHttpClient(WnsProvider.HttpClientName);
        builder.Services.AddSingleton<IPushProvider, WnsTenantProvider>();
        return builder;
    }


    /// <summary>
    /// Adds a keyed WNS provider for multi-app servers. The <paramref name="key"/> matches a device's
    /// <see cref="DeviceRegistration.AppId"/>; each app gets its own Entra app registration + cached token.
    /// </summary>
    public static IPushBuilder AddWns(this IPushBuilder builder, string key, Action<WnsOptions> configure)
    {
        builder.Services
            .AddOptions<WnsOptions>(key)
            .Configure(configure)
            // Manual validation (no reflection) keeps the provider AOT/trim-safe.
            .Validate(
                o => !string.IsNullOrWhiteSpace(o.TenantId)
                    && !string.IsNullOrWhiteSpace(o.ClientId)
                    && !string.IsNullOrWhiteSpace(o.ClientSecret),
                "WNS requires TenantId, ClientId and ClientSecret."
            );

        // One cached access token per app key, owned by DI for disposal.
        builder.Services.AddKeyedSingleton<WnsAccessTokenProvider>(key, (sp, k) =>
            new WnsAccessTokenProvider(
                (string)k!,
                sp.GetRequiredService<IOptionsMonitor<WnsOptions>>(),
                sp.GetRequiredService<IHttpClientFactory>()
            ));

        builder.Services.AddHttpClient(WnsProvider.HttpClientName);

        builder.Services.AddSingleton<IPushProvider>(sp => new WnsProvider(
            key,
            sp.GetRequiredService<IHttpClientFactory>(),
            sp.GetRequiredKeyedService<WnsAccessTokenProvider>(key),
            sp.GetRequiredService<ILogger<WnsProvider>>()
        ));

        return builder;
    }
}
