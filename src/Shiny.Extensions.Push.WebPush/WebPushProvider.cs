using System.Buffers.Text;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Shiny.Extensions.Push;

namespace Shiny.Extensions.Push.WebPush;


/// <summary>
/// Delivers to browsers via the Web Push protocol (VAPID auth + RFC 8291 <c>aes128gcm</c> payload
/// encryption). The subscription endpoint is the registration's <see cref="DeviceRegistration.DeviceToken"/>;
/// the <c>p256dh</c> and <c>auth</c> keys live in <see cref="DeviceRegistration.Data"/>.
/// </summary>
public sealed class WebPushProvider : IPushProvider
{
    public const string HttpClientName = "shiny-webpush";
    public const string P256dhKey = "p256dh";
    public const string AuthKey = "auth";

    readonly string appKey;
    readonly IHttpClientFactory httpClientFactory;
    readonly WebPushVapid vapid;
    readonly WebPushOptions options;
    readonly ILogger<WebPushProvider> logger;


    public WebPushProvider(
        string appKey,
        IHttpClientFactory httpClientFactory,
        WebPushVapid vapid,
        IOptionsMonitor<WebPushOptions> options,
        ILogger<WebPushProvider> logger
    )
    {
        this.appKey = appKey ?? string.Empty;
        this.httpClientFactory = httpClientFactory;
        this.vapid = vapid;
        this.options = options.Get(this.appKey);
        this.logger = logger;
    }


    public string Identifier => this.appKey.Length == 0 ? "webpush" : $"webpush:{this.appKey}";

    public bool CanDeliver(DeviceRegistration registration)
        => registration.Platform == DevicePlatform.WebBrowser
            && string.Equals(registration.AppId ?? string.Empty, this.appKey, StringComparison.Ordinal);


    public async Task<PushDeliveryResult> Send(PushNotification notification, DeviceRegistration registration, CancellationToken cancellationToken = default)
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

        var ttl = (long)(notification.TimeToLive ?? this.options.DefaultTimeToLive).TotalSeconds;

        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new ByteArrayContent(body)
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        request.Content.Headers.ContentEncoding.Add("aes128gcm");
        request.Headers.TryAddWithoutValidation("TTL", ttl.ToString());
        request.Headers.TryAddWithoutValidation("Authorization", this.vapid.CreateAuthorizationHeader(endpoint, DateTimeOffset.UtcNow));
        if (notification.WebPush?.Urgency is { } urgency)
            request.Headers.TryAddWithoutValidation("Urgency", urgency);

        var client = this.httpClientFactory.CreateClient(HttpClientName);
        using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);

        if (response.IsSuccessStatusCode)
        {
            var location = response.Headers.Location?.ToString();
            return PushDeliveryResult.Success(registration, providerMessageId: location);
        }

        return this.MapFailure(registration, response.StatusCode);
    }


    PushDeliveryResult MapFailure(DeviceRegistration registration, HttpStatusCode status)
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
                this.logger.LogWarning("WebPush send failed: {Status}", status);
                return PushDeliveryResult.Failed(registration, PushDeliveryStatus.Error, status.ToString());
        }
    }
}
