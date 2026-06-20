using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Shiny.Extensions.Push;

namespace Shiny.Extensions.Push.Fcm;


/// <summary>
/// Delivers to Android devices via Firebase Cloud Messaging (HTTP v1). OAuth2 service-account auth; one
/// HTTP/1.1+ request per device with a cached bearer token. AOT-safe.
/// </summary>
public sealed class FcmProvider : IPushProvider
{
    public const string HttpClientName = "shiny-fcm";

    readonly string appKey;
    readonly IHttpClientFactory httpClientFactory;
    readonly FcmAccessTokenProvider tokenProvider;
    readonly ILogger<FcmProvider> logger;


    public FcmProvider(
        string appKey,
        IHttpClientFactory httpClientFactory,
        FcmAccessTokenProvider tokenProvider,
        ILogger<FcmProvider> logger
    )
    {
        this.appKey = appKey ?? string.Empty;
        this.httpClientFactory = httpClientFactory;
        this.tokenProvider = tokenProvider;
        this.logger = logger;
    }


    public string Identifier => this.appKey.Length == 0 ? "fcm" : $"fcm:{this.appKey}";

    public bool CanDeliver(DeviceRegistration registration)
        => registration.Platform == DevicePlatform.Android
            && string.Equals(registration.AppId ?? string.Empty, this.appKey, StringComparison.Ordinal);


    public async Task<PushDeliveryResult> Send(PushNotification notification, DeviceRegistration registration, CancellationToken cancellationToken = default)
    {
        var accessToken = await this.tokenProvider.GetAccessToken(cancellationToken).ConfigureAwait(false);
        var url = $"https://fcm.googleapis.com/v1/projects/{this.tokenProvider.ProjectId}/messages:send";

        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new ByteArrayContent(FcmPayloadBuilder.Build(notification, registration.DeviceToken))
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        var client = this.httpClientFactory.CreateClient(HttpClientName);
        using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        if (response.IsSuccessStatusCode)
            return PushDeliveryResult.Success(registration, providerMessageId: ParseName(body));

        var errorCode = ParseErrorCode(body);
        return this.MapFailure(registration, response.StatusCode, errorCode);
    }


    PushDeliveryResult MapFailure(DeviceRegistration registration, HttpStatusCode status, string? errorCode)
    {
        switch (errorCode)
        {
            case "UNREGISTERED":
                return PushDeliveryResult.Failed(registration, PushDeliveryStatus.TokenExpired, errorCode);

            case "INVALID_ARGUMENT":
            case "SENDER_ID_MISMATCH":
                return PushDeliveryResult.Failed(registration, PushDeliveryStatus.InvalidToken, errorCode);

            case "QUOTA_EXCEEDED":
            case "UNAVAILABLE":
                return PushDeliveryResult.Failed(registration, PushDeliveryStatus.RateLimited, errorCode);
        }

        if (status == HttpStatusCode.NotFound)
            return PushDeliveryResult.Failed(registration, PushDeliveryStatus.TokenExpired, errorCode ?? "NotFound");

        if (status == HttpStatusCode.TooManyRequests || status == HttpStatusCode.ServiceUnavailable)
            return PushDeliveryResult.Failed(registration, PushDeliveryStatus.RateLimited, errorCode ?? status.ToString());

        this.logger.LogWarning("FCM send failed: {Status} {Reason}", status, errorCode);
        return PushDeliveryResult.Failed(registration, PushDeliveryStatus.Error, errorCode ?? status.ToString());
    }


    internal static string? ParseName(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.TryGetProperty("name", out var n) ? n.GetString() : null;
        }
        catch (JsonException) { return null; }
    }


    // Error shape: { "error": { "status": "...", "details": [ { "@type": "...FcmError", "errorCode": "UNREGISTERED" } ] } }
    internal static string? ParseErrorCode(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("error", out var error))
                return null;

            if (error.TryGetProperty("details", out var details) && details.ValueKind == JsonValueKind.Array)
            {
                foreach (var detail in details.EnumerateArray())
                {
                    if (detail.TryGetProperty("errorCode", out var code))
                        return code.GetString();
                }
            }
            return error.TryGetProperty("status", out var status) ? status.GetString() : null;
        }
        catch (JsonException) { return null; }
    }
}
