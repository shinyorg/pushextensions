using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Shiny.Extensions.Push.Wns;


/// <summary>
/// The transport core shared by the keyed <see cref="WnsProvider"/> and the multi-tenant provider: POSTs the
/// built payload to the channel URI and maps the response (including WNS's 200-with-drop/throttle header). The
/// caller supplies the bearer token and an invalidation callback for the 401 path.
/// </summary>
static class WnsSender
{
    // onUnauthorized is invoked on a 401 so the caller can drop its cached bearer token.
    public static async Task<PushDeliveryResult> Send(
        HttpClient client,
        string accessToken,
        Action onUnauthorized,
        PushNotification notification,
        DeviceRegistration registration,
        ILogger logger,
        CancellationToken cancellationToken
    )
    {
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

        using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        return Map(registration, response, onUnauthorized, logger);
    }


    static PushDeliveryResult Map(DeviceRegistration registration, HttpResponseMessage response, Action onUnauthorized, ILogger logger)
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
                onUnauthorized();
                logger.LogWarning("WNS rejected the access token (401); cache invalidated");
                return PushDeliveryResult.Failed(registration, PushDeliveryStatus.Error, "Unauthorized");
        }

        logger.LogWarning("WNS send failed: {Status}", response.StatusCode);
        return PushDeliveryResult.Failed(registration, PushDeliveryStatus.Error, response.StatusCode.ToString());
    }


    static string? Header(HttpResponseMessage response, string name)
        => response.Headers.TryGetValues(name, out var values) ? values.FirstOrDefault() : null;
}
