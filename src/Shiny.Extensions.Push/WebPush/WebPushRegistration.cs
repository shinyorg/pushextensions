using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Shiny.Extensions.Push;

namespace Shiny.Extensions.Push.WebPush;


public static class WebPushRegistration
{
    /// <summary>Adds the default (keyless) Web Push provider. Routes registrations with null/empty AppId.</summary>
    public static IPushBuilder AddWebPush(this IPushBuilder builder, Action<WebPushOptions> configure)
        => builder.AddWebPush(Options.DefaultName, configure);


    /// <summary>
    /// Adds a keyed Web Push provider for multi-app servers. The <paramref name="key"/> matches a device's
    /// <see cref="DeviceRegistration.AppId"/>; each app gets its own VAPID identity.
    /// </summary>
    public static IPushBuilder AddWebPush(this IPushBuilder builder, string key, Action<WebPushOptions> configure)
    {
        builder.Services
            .AddOptions<WebPushOptions>(key)
            .Configure(configure)
            .Validate(
                o => !string.IsNullOrWhiteSpace(o.PublicKey) && !string.IsNullOrWhiteSpace(o.PrivateKey) && !string.IsNullOrWhiteSpace(o.Subject),
                "Web Push requires PublicKey, PrivateKey and Subject."
            );

        builder.Services.AddKeyedSingleton<WebPushVapid>(key, (sp, k) =>
        {
            var o = sp.GetRequiredService<IOptionsMonitor<WebPushOptions>>().Get((string)k!);
            return new WebPushVapid(o.PublicKey, o.PrivateKey, o.Subject);
        });

        builder.Services.AddHttpClient(WebPushProvider.HttpClientName);

        builder.Services.AddSingleton<IPushProvider>(sp => new WebPushProvider(
            key,
            sp.GetRequiredService<IHttpClientFactory>(),
            sp.GetRequiredKeyedService<WebPushVapid>(key),
            sp.GetRequiredService<IOptionsMonitor<WebPushOptions>>(),
            sp.GetRequiredService<ILogger<WebPushProvider>>()
        ));

        return builder;
    }
}
