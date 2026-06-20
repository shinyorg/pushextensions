using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Shiny.Extensions.Push;

namespace Shiny.Extensions.Push.Apns;


/// <summary>
/// Delivers to Apple devices (iOS, macOS) over APNs directly — HTTP/2, token-based (.p8) auth. No
/// FCM/Google dependency. The token environment (sandbox vs production) is chosen per registration.
/// </summary>
public sealed class ApnsProvider : IPushProvider
{
    public const string HttpClientName = "shiny-apns";

    const string ProductionHost = "https://api.push.apple.com";
    const string SandboxHost = "https://api.sandbox.push.apple.com";

    readonly string appKey;
    readonly IHttpClientFactory httpClientFactory;
    readonly ApnsJwtProvider jwt;
    readonly ApnsOptions options;
    readonly ILogger<ApnsProvider> logger;


    public ApnsProvider(
        string appKey,
        IHttpClientFactory httpClientFactory,
        ApnsJwtProvider jwt,
        IOptionsMonitor<ApnsOptions> options,
        ILogger<ApnsProvider> logger
    )
    {
        this.appKey = appKey ?? string.Empty;
        this.httpClientFactory = httpClientFactory;
        this.jwt = jwt;
        this.options = options.Get(this.appKey);
        this.logger = logger;
    }


    public string Identifier => this.appKey.Length == 0 ? "apns" : $"apns:{this.appKey}";

    public bool CanDeliver(DeviceRegistration registration)
        => registration.Platform is DevicePlatform.iOS or DevicePlatform.MacOS
            && string.Equals(registration.AppId ?? string.Empty, this.appKey, StringComparison.Ordinal);


    public async Task<PushDeliveryResult> Send(PushNotification notification, DeviceRegistration registration, CancellationToken cancellationToken = default)
    {
        var env = this.options.ForceEnvironment ?? registration.Environment;
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

        request.Headers.TryAddWithoutValidation("authorization", $"bearer {this.jwt.GetToken()}");
        request.Headers.TryAddWithoutValidation("apns-topic", apple?.TopicOverride ?? this.options.BundleId);
        request.Headers.TryAddWithoutValidation("apns-push-type", apple?.PushTypeOverride ?? (silent ? "background" : "alert"));

        // Background pushes must be priority 5.
        var priority = silent || notification.Priority == PushPriority.Normal ? "5" : "10";
        request.Headers.TryAddWithoutValidation("apns-priority", priority);

        if (notification.CollapseId is { } collapseId)
            request.Headers.TryAddWithoutValidation("apns-collapse-id", collapseId);

        if (notification.TimeToLive is { } ttl)
            request.Headers.TryAddWithoutValidation("apns-expiration", DateTimeOffset.UtcNow.Add(ttl).ToUnixTimeSeconds().ToString());

        var client = this.httpClientFactory.CreateClient(HttpClientName);

        using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.IsSuccessStatusCode)
        {
            var apnsId = response.Headers.TryGetValues("apns-id", out var ids) ? ids.FirstOrDefault() : null;
            return PushDeliveryResult.Success(registration, providerMessageId: apnsId);
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var reason = ParseReason(body);
        return this.MapFailure(registration, response.StatusCode, reason);
    }


    PushDeliveryResult MapFailure(DeviceRegistration registration, HttpStatusCode status, string? reason)
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
            this.jwt.Invalidate();
            this.logger.LogWarning("APNs rejected provider token ({Reason}); invalidated cache", reason);
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
