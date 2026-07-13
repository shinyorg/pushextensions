using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Shiny.Extensions.Push;

namespace Shiny.Extensions.Push.Fcm;


public static class FcmRegistration
{
    /// <summary>Adds the default (keyless) FCM provider. Routes registrations with null/empty AppId.</summary>
    public static IPushBuilder AddFcm(this IPushBuilder builder, Action<FcmOptions> configure)
        => builder.AddFcm(Options.DefaultName, configure);


    /// <summary>
    /// Adds the configuration-driven FCM provider: it resolves each device's <see cref="FcmOptions"/> from the
    /// registered <see cref="IPushConfigurationProvider"/> (see <c>UsePushConfiguration</c>) at send time, keyed by
    /// <see cref="DeviceRegistration.AppId"/>. Batches are split per app. Use this instead of the keyed
    /// <c>AddFcm("key", …)</c> overloads — not both.
    /// </summary>
    public static IPushBuilder AddFcm(this IPushBuilder builder)
    {
        builder.Services.AddHttpClient(FcmProvider.HttpClientName);
        builder.Services.AddSingleton<IPushProvider, FcmTenantProvider>();
        return builder;
    }


    /// <summary>
    /// Adds a keyed FCM provider for multi-app servers. The <paramref name="key"/> matches a device's
    /// <see cref="DeviceRegistration.AppId"/>; each app gets its own Firebase project + cached token.
    /// </summary>
    public static IPushBuilder AddFcm(this IPushBuilder builder, string key, Action<FcmOptions> configure)
    {
        builder.Services
            .AddOptions<FcmOptions>(key)
            .Configure(configure)
            .Validate(
                o => !string.IsNullOrWhiteSpace(o.ServiceAccountJson) || !string.IsNullOrWhiteSpace(o.ServiceAccountJsonPath),
                "FCM requires ServiceAccountJson or ServiceAccountJsonPath."
            );

        builder.Services.AddKeyedSingleton<FcmAccessTokenProvider>(key, (sp, k) =>
            new FcmAccessTokenProvider(
                (string)k!,
                sp.GetRequiredService<IOptionsMonitor<FcmOptions>>(),
                sp.GetRequiredService<IHttpClientFactory>()
            ));

        builder.Services.AddHttpClient(FcmProvider.HttpClientName);

        builder.Services.AddSingleton<IPushProvider>(sp => new FcmProvider(
            key,
            sp.GetRequiredService<IHttpClientFactory>(),
            sp.GetRequiredKeyedService<FcmAccessTokenProvider>(key),
            sp.GetRequiredService<ILogger<FcmProvider>>()
        ));

        return builder;
    }
}
