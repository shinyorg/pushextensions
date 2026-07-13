using Microsoft.Extensions.Logging;
using Shiny.Extensions.Push;

namespace Shiny.Extensions.Push.Wns;


/// <summary>
/// Delivers to Windows devices via WNS (Windows Notification Service) using the modern Windows App SDK /
/// Entra auth model. The registration's <see cref="DeviceRegistration.DeviceToken"/> is the WNS channel
/// URI; the notification is POSTed to it with a cached bearer token. AOT-safe. This is the keyed (static,
/// single-app) provider; see the tenant-aware provider for multi-tenant servers.
/// </summary>
public sealed class WnsProvider(
    string appKey,
    IHttpClientFactory httpClientFactory,
    WnsAccessTokenProvider tokenProvider,
    ILogger<WnsProvider> logger
) : IPushProvider
{
    public const string HttpClientName = "shiny-wns";

    readonly string appKey = appKey ?? string.Empty;


    public string Identifier => appKey.Length == 0 ? "wns" : $"wns:{appKey}";

    public bool CanDeliver(DeviceRegistration registration)
        => registration.Platform == DevicePlatform.Windows
            && string.Equals(registration.AppId ?? string.Empty, appKey, StringComparison.Ordinal);


    public async Task<PushDeliveryResult> Send(PushNotification notification, DeviceRegistration registration, CancellationToken cancellationToken = default)
    {
        var accessToken = await tokenProvider.GetAccessToken(cancellationToken).ConfigureAwait(false);
        return await WnsSender
            .Send(httpClientFactory.CreateClient(HttpClientName), accessToken, tokenProvider.Invalidate, notification, registration, logger, cancellationToken)
            .ConfigureAwait(false);
    }
}
