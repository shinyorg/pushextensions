using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Shiny.Extensions.Push;

namespace Shiny.Extensions.Push.WebPush;


/// <summary>
/// Delivers to browsers via the Web Push protocol (VAPID auth + RFC 8291 <c>aes128gcm</c> payload
/// encryption). The subscription endpoint is the registration's <see cref="DeviceRegistration.DeviceToken"/>;
/// the <c>p256dh</c> and <c>auth</c> keys live in <see cref="DeviceRegistration.Data"/>. This is the keyed
/// (static, single-app) provider; see the tenant-aware provider for multi-tenant servers.
/// </summary>
public sealed class WebPushProvider(
    string appKey,
    IHttpClientFactory httpClientFactory,
    WebPushVapid vapid,
    IOptionsMonitor<WebPushOptions> options,
    ILogger<WebPushProvider> logger
) : IPushProvider
{
    public const string HttpClientName = "shiny-webpush";
    public const string P256dhKey = WebPushSender.P256dhKey;
    public const string AuthKey = WebPushSender.AuthKey;

    readonly string appKey = appKey ?? string.Empty;
    readonly WebPushOptions options = options.Get(appKey ?? string.Empty);


    public string Identifier => appKey.Length == 0 ? "webpush" : $"webpush:{appKey}";

    public bool CanDeliver(DeviceRegistration registration)
        => registration.Platform == DevicePlatform.WebBrowser
            && string.Equals(registration.AppId ?? string.Empty, appKey, StringComparison.Ordinal);


    public Task<PushDeliveryResult> Send(PushNotification notification, DeviceRegistration registration, CancellationToken cancellationToken = default)
        => WebPushSender.Send(
            httpClientFactory.CreateClient(HttpClientName),
            options.DefaultTimeToLive,
            endpoint => new ValueTask<string>(vapid.CreateAuthorizationHeader(endpoint, DateTimeOffset.UtcNow)),
            notification,
            registration,
            logger,
            cancellationToken
        );
}
