using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.Extensions.Logging;
using Shiny.Extensions.Push;

namespace Shiny.Extensions.Push.Wns;


/// <summary>
/// Delivers to Windows devices via WNS (Windows Notification Service) using the modern Windows App SDK /
/// Entra auth model. The registration's <see cref="DeviceRegistration.DeviceToken"/> is the WNS channel
/// URI; the notification is POSTed to it with a cached bearer token. AOT-safe.
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
        var content = WnsPayloadBuilder.Build(notification);

        using var request = new HttpRequestMessage(HttpMethod.Post, registration.DeviceToken)
        {
            Content = new StringContent(content.Body, Encoding.UTF8)
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue(content.ContentType);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.TryAddWithoutValidation("X-WNS-Type", content.WnsType);
        request.Headers.TryAddWithoutValidation("X-WNS-RequestForStatus", "true");
        request.Headers.TryAddWithoutValidation("X-WNS-PRIORITY", notification.Priority == PushPriority.High ? "1" : "3");
        if (notification.TimeToLive is { } ttl)
            request.Headers.TryAddWithoutValidation("X-WNS-TTL", ((long)ttl.TotalSeconds).ToString(CultureInfo.InvariantCulture));

        var client = httpClientFactory.CreateClient(HttpClientName);
        using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);

        return this.Map(registration, response);
    }


    PushDeliveryResult Map(DeviceRegistration registration, HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode)
        {
            // WNS still 200s when it drops/throttles a delivery — the real outcome is in this header.
            var notificationStatus = Header(response, "X-WNS-NotificationStatus");
            if (string.Equals(notificationStatus, "dropped", StringComparison.OrdinalIgnoreCase))
                return PushDeliveryResult.Failed(registration, PushDeliveryStatus.Error, "dropped");
            if (string.Equals(notificationStatus, "channelthrottled", StringComparison.OrdinalIgnoreCase))
                return PushDeliveryResult.Failed(registration, PushDeliveryStatus.RateLimited, "channelthrottled");

            return PushDeliveryResult.Success(registration, providerMessageId: Header(response, "X-WNS-Msg-ID"));
        }

        switch (response.StatusCode)
        {
            // The channel has expired (app/device gone) — prune it.
            case HttpStatusCode.Gone:
                return PushDeliveryResult.Failed(registration, PushDeliveryStatus.TokenExpired, "Gone");

            // The channel URI is unknown/invalid — prune it.
            case HttpStatusCode.NotFound:
                return PushDeliveryResult.Failed(registration, PushDeliveryStatus.InvalidToken, "NotFound");

            // 406 Not Acceptable / 429 Too Many Requests — WNS is throttling us.
            case HttpStatusCode.NotAcceptable:
            case HttpStatusCode.TooManyRequests:
                return PushDeliveryResult.Failed(registration, PushDeliveryStatus.RateLimited, response.StatusCode.ToString());

            // The bearer token expired/was rejected — drop the cache so the next send re-mints.
            case HttpStatusCode.Unauthorized:
                tokenProvider.Invalidate();
                logger.LogWarning("WNS rejected the access token (401); cache invalidated");
                return PushDeliveryResult.Failed(registration, PushDeliveryStatus.Error, "Unauthorized");
        }

        logger.LogWarning("WNS send failed: {Status}", response.StatusCode);
        return PushDeliveryResult.Failed(registration, PushDeliveryStatus.Error, response.StatusCode.ToString());
    }


    static string? Header(HttpResponseMessage response, string name)
        => response.Headers.TryGetValues(name, out var values) ? values.FirstOrDefault() : null;
}
