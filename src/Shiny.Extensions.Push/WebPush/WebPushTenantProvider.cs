using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Shiny.Extensions.Push;
using Shiny.Extensions.Push.Infrastructure;

namespace Shiny.Extensions.Push.WebPush;


/// <summary>
/// The multi-app Web Push provider. Claims <see cref="DevicePlatform.WebBrowser"/> for <em>any</em> app and
/// resolves each device's <see cref="WebPushOptions"/> (VAPID keys) from the scoped
/// <see cref="IPushConfigurationProvider"/> at send time. The VAPID <c>Authorization</c> header is reused per
/// (app, endpoint-origin) on the JWT's lifetime; a refresh re-reads the configuration so rotated keys are
/// picked up within that window.
/// </summary>
public sealed class WebPushTenantProvider(
    IHttpClientFactory httpClientFactory,
    IServiceScopeFactory scopeFactory,
    ILogger<WebPushTenantProvider> logger
) : IPushProvider
{
    // The VAPID JWT is minted with a 12h exp (RFC 8292 allows <= 24h); refresh an hour early.
    static readonly TimeSpan Lifetime = TimeSpan.FromHours(12);
    static readonly TimeSpan Skew = TimeSpan.FromHours(1);

    readonly CredentialTokenCache headerCache = new();


    public string Identifier => "webpush";

    public bool CanDeliver(DeviceRegistration registration)
        => registration.Platform == DevicePlatform.WebBrowser;


    public async Task<PushDeliveryResult> Send(PushNotification notification, DeviceRegistration registration, CancellationToken cancellationToken = default)
    {
        var appId = registration.AppId ?? string.Empty;

        WebPushOptions options;
        try
        {
            var config = await ScopedConfiguration.Resolve(scopeFactory, appId, cancellationToken).ConfigureAwait(false);
            if (config?.WebPush is not { } webPush)
                return PushDeliveryResult.Failed(registration, PushDeliveryStatus.Error, "app not configured for WebPush");

            options = webPush;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to resolve WebPush configuration for app {AppId}", appId);
            return PushDeliveryResult.Failed(registration, PushDeliveryStatus.Error, "configuration resolution failed", ex);
        }

        return await WebPushSender
            .Send(
                httpClientFactory.CreateClient(WebPushProvider.HttpClientName),
                options.DefaultTimeToLive,
                endpoint => Authorization(appId, options, endpoint, cancellationToken),
                notification,
                registration,
                logger,
                cancellationToken
            )
            .ConfigureAwait(false);
    }


    // VAPID headers are per push-service origin, so the cache key includes the endpoint authority.
    ValueTask<string> Authorization(string appId, WebPushOptions options, Uri endpoint, CancellationToken cancellationToken)
    {
        var key = $"{appId}|{endpoint.Scheme}://{endpoint.Authority}";
        return headerCache.Get(
            key,
            Skew,
            _ => new((WebPushVapid.CreateAuthorizationHeader(options, endpoint, DateTimeOffset.UtcNow), (int)Lifetime.TotalSeconds)),
            cancellationToken
        );
    }
}
