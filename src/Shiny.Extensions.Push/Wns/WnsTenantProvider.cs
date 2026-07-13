using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Shiny.Extensions.Push;
using Shiny.Extensions.Push.Infrastructure;

namespace Shiny.Extensions.Push.Wns;


/// <summary>
/// The multi-app WNS provider. Claims <see cref="DevicePlatform.Windows"/> for <em>any</em> app and resolves
/// each device's <see cref="WnsOptions"/> from the scoped <see cref="IPushConfigurationProvider"/> at send time.
/// The Entra bearer is reused per app on its normal lifetime; a refresh re-reads the configuration so a rotated
/// client secret is picked up within that window.
/// </summary>
public sealed class WnsTenantProvider(
    IHttpClientFactory httpClientFactory,
    IServiceScopeFactory scopeFactory,
    ILogger<WnsTenantProvider> logger
) : IPushProvider
{
    static readonly TimeSpan Skew = TimeSpan.FromMinutes(5);

    readonly CredentialTokenCache tokenCache = new();


    public string Identifier => "wns";

    public bool CanDeliver(DeviceRegistration registration)
        => registration.Platform == DevicePlatform.Windows;


    public async Task<PushDeliveryResult> Send(PushNotification notification, DeviceRegistration registration, CancellationToken cancellationToken = default)
    {
        var appId = registration.AppId ?? string.Empty;

        WnsOptions options;
        try
        {
            var config = await ScopedConfiguration.Resolve(scopeFactory, appId, cancellationToken).ConfigureAwait(false);
            if (config?.Wns is not { } wns)
                return PushDeliveryResult.Failed(registration, PushDeliveryStatus.Error, "app not configured for WNS");

            options = wns;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to resolve WNS configuration for app {AppId}", appId);
            return PushDeliveryResult.Failed(registration, PushDeliveryStatus.Error, "configuration resolution failed", ex);
        }

        var accessToken = await tokenCache
            .Get(appId,
                Skew,
                ct => WnsToken.Exchange(Client(), options.ResolveTokenEndpoint(), options.ClientId ?? "", options.ClientSecret ?? "", ct),
                cancellationToken
            )
            .ConfigureAwait(false);

        return await WnsSender
            .Send(Client(), accessToken, () => tokenCache.Invalidate(appId), notification, registration, logger, cancellationToken)
            .ConfigureAwait(false);
    }


    HttpClient Client() => httpClientFactory.CreateClient(WnsProvider.HttpClientName);
}
