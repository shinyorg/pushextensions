using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Shiny.Extensions.Push;

namespace Shiny.Extensions.Push.Apns;


/// <summary>
/// Delivers to Apple devices (iOS, macOS) over APNs directly — HTTP/2, token-based (.p8) auth. No
/// FCM/Google dependency. The token environment (sandbox vs production) is chosen per registration.
/// This is the keyed (static, single-app) provider; see the tenant-aware provider for multi-tenant servers.
/// </summary>
public sealed class ApnsProvider(
    string appKey,
    IHttpClientFactory httpClientFactory,
    ApnsJwtProvider jwt,
    IOptionsMonitor<ApnsOptions> options,
    ILogger<ApnsProvider> logger
) : IPushProvider
{
    public const string HttpClientName = "shiny-apns";

    readonly string appKey = appKey ?? string.Empty;
    readonly ApnsOptions options = options.Get(appKey ?? string.Empty);


    public string Identifier => appKey.Length == 0 ? "apns" : $"apns:{appKey}";

    public bool CanDeliver(DeviceRegistration registration)
        => registration.Platform is DevicePlatform.iOS or DevicePlatform.MacOS
            && string.Equals(registration.AppId ?? string.Empty, appKey, StringComparison.Ordinal);


    public Task<PushDeliveryResult> Send(PushNotification notification, DeviceRegistration registration, CancellationToken cancellationToken = default)
        => ApnsSender.Send(
            httpClientFactory.CreateClient(HttpClientName),
            options,
            jwt.GetToken(),
            jwt.Invalidate,
            notification,
            registration,
            logger,
            cancellationToken
        );
}
