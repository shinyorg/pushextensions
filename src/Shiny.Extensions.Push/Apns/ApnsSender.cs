using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Shiny.Extensions.Push.Apns;


/// <summary>
/// The transport core shared by the keyed <see cref="ApnsProvider"/> and the multi-tenant provider: builds the
/// HTTP/2 request from a resolved <see cref="ApnsOptions"/> + provider JWT and maps the response to a
/// normalized <see cref="PushDeliveryResult"/>. No caching/auth state of its own — the caller supplies the JWT.
/// </summary>
static class ApnsSender
{
    const string ProductionHost = "https://api.push.apple.com";
    const string SandboxHost = "https://api.sandbox.push.apple.com";


    // onProviderTokenRejected is invoked when APNs rejects the provider JWT (expired/invalid) so the caller
    // can drop its cached token.
    public static async Task<PushDeliveryResult> Send(
        HttpClient client,
        ApnsOptions options,
        string jwt,
        Action onProviderTokenRejected,
        PushNotification notification,
        DeviceRegistration registration,
        ILogger logger,
        CancellationToken cancellationToken
    )
    {
        var env = options.ForceEnvironment ?? registration.Environment;
        var host = env == PushEnvironment.Sandbox ? SandboxHost : ProductionHost;

        var apple = notification.Apple;
        var silent = apple?.ContentAvailable == true && notification.Title is null && notification.Message is null;

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{host}/3/device/{registration.DeviceToken}")
        {
            Version = HttpVersion.Version20,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact,
            Content = new ByteArrayContent(ApnsPayloadBuilder.Build(notification))
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

        request.Headers.TryAddWithoutValidation("authorization", $"bearer {jwt}");
        request.Headers.TryAddWithoutValidation("apns-topic", apple?.TopicOverride ?? options.BundleId);
        request.Headers.TryAddWithoutValidation("apns-push-type", apple?.PushTypeOverride ?? (silent ? "background" : "alert"));

        // Background pushes must be priority 5.
        var priority = silent || notification.Priority == PushPriority.Normal ? "5" : "10";
        request.Headers.TryAddWithoutValidation("apns-priority", priority);

        if (notification.CollapseId is { } collapseId)
            request.Headers.TryAddWithoutValidation("apns-collapse-id", collapseId);

        if (notification.TimeToLive is { } ttl)
            request.Headers.TryAddWithoutValidation("apns-expiration", DateTimeOffset.UtcNow.Add(ttl).ToUnixTimeSeconds().ToString());

        using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.IsSuccessStatusCode)
        {
            var apnsId = response.Headers.TryGetValues("apns-id", out var ids) ? ids.FirstOrDefault() : null;
            return PushDeliveryResult.Success(registration, providerMessageId: apnsId);
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var reason = ParseReason(body);
        return MapFailure(registration, response.StatusCode, reason, onProviderTokenRejected, logger);
    }


    static PushDeliveryResult MapFailure(DeviceRegistration registration, HttpStatusCode status, string? reason, Action onProviderTokenRejected, ILogger logger)
    {
        // Token is gone (app uninstalled / no longer registered).
        if (status == HttpStatusCode.Gone || reason == "Unregistered")
            return PushDeliveryResult.Failed(registration, PushDeliveryStatus.TokenExpired, reason);

        // Token is structurally invalid or not for this app.
        if (reason is "BadDeviceToken" or "DeviceTokenNotForTopic" or "MissingDeviceToken")
            return PushDeliveryResult.Failed(registration, PushDeliveryStatus.InvalidToken, reason);

        // Provider token problems — refresh and treat as transient.
        if (reason is "ExpiredProviderToken" or "InvalidProviderToken" or "MissingProviderToken")
        {
            onProviderTokenRejected();
            logger.LogWarning("APNs rejected provider token ({Reason}); invalidated cache", reason);
            return PushDeliveryResult.Failed(registration, PushDeliveryStatus.Error, reason);
        }

        if (status == HttpStatusCode.TooManyRequests || reason == "TooManyRequests")
            return PushDeliveryResult.Failed(registration, PushDeliveryStatus.RateLimited, reason);

        return PushDeliveryResult.Failed(registration, PushDeliveryStatus.Error, reason ?? status.ToString());
    }


    static string? ParseReason(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.TryGetProperty("reason", out var r) ? r.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
