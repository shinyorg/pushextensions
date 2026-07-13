using System.Buffers.Text;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;

namespace Shiny.Extensions.Push.WebPush;


/// <summary>
/// The transport core shared by the keyed <see cref="WebPushProvider"/> and the multi-tenant provider:
/// RFC 8291 payload encryption + POST + error mapping. The VAPID <c>Authorization</c> header is supplied by
/// the caller via a delegate (built from a held key for the keyed provider, or from per-tenant options for the
/// tenant provider), keyed on the resolved endpoint origin.
/// </summary>
static class WebPushSender
{
    internal const string P256dhKey = "p256dh";
    internal const string AuthKey = "auth";


    public static async Task<PushDeliveryResult> Send(
        HttpClient client,
        TimeSpan defaultTtl,
        Func<Uri, ValueTask<string>> authorization,
        PushNotification notification,
        DeviceRegistration registration,
        ILogger logger,
        CancellationToken cancellationToken
    )
    {
        if (registration.Data is null ||
            !registration.Data.TryGetValue(P256dhKey, out var p256dh) ||
            !registration.Data.TryGetValue(AuthKey, out var auth))
        {
            return PushDeliveryResult.Failed(registration, PushDeliveryStatus.InvalidToken, "missing p256dh/auth keys");
        }

        if (!Uri.TryCreate(registration.DeviceToken, UriKind.Absolute, out var endpoint))
            return PushDeliveryResult.Failed(registration, PushDeliveryStatus.InvalidToken, "invalid endpoint");

        byte[] body;
        try
        {
            var uaPublic = Base64Url.DecodeFromChars(p256dh);
            var authSecret = Base64Url.DecodeFromChars(auth);
            var payload = WebPushPayloadBuilder.Build(notification);
            body = WebPushCrypto.Encrypt(payload, uaPublic, authSecret);
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException or CryptographicException)
        {
            return PushDeliveryResult.Failed(registration, PushDeliveryStatus.InvalidToken, "key/encryption error", ex);
        }

        var ttl = (long)(notification.TimeToLive ?? defaultTtl).TotalSeconds;
        var authHeader = await authorization(endpoint).ConfigureAwait(false);

        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new ByteArrayContent(body)
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        request.Content.Headers.ContentEncoding.Add("aes128gcm");
        request.Headers.TryAddWithoutValidation("TTL", ttl.ToString());
        request.Headers.TryAddWithoutValidation("Authorization", authHeader);
        if (notification.WebPush?.Urgency is { } urgency)
            request.Headers.TryAddWithoutValidation("Urgency", urgency);

        using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);

        if (response.IsSuccessStatusCode)
        {
            var location = response.Headers.Location?.ToString();
            return PushDeliveryResult.Success(registration, providerMessageId: location);
        }

        return MapFailure(registration, response.StatusCode, logger);
    }


    static PushDeliveryResult MapFailure(DeviceRegistration registration, HttpStatusCode status, ILogger logger)
    {
        switch (status)
        {
            case HttpStatusCode.NotFound:
            case HttpStatusCode.Gone:
                return PushDeliveryResult.Failed(registration, PushDeliveryStatus.TokenExpired, status.ToString());

            case HttpStatusCode.TooManyRequests:
            case HttpStatusCode.ServiceUnavailable:
                return PushDeliveryResult.Failed(registration, PushDeliveryStatus.RateLimited, status.ToString());

            default:
                logger.LogWarning("WebPush send failed: {Status}", status);
                return PushDeliveryResult.Failed(registration, PushDeliveryStatus.Error, status.ToString());
        }
    }
}
