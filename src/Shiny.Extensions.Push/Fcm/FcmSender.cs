using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Shiny.Extensions.Push.Fcm;


/// <summary>
/// The transport core shared by the keyed <see cref="FcmProvider"/> and the multi-tenant provider: HTTP v1
/// sends plus FCM error mapping. Auth/project are supplied by the caller (resolved per app or per tenant), so
/// this holds no state.
/// </summary>
static class FcmSender
{
    public static async Task<PushDeliveryResult> Send(
        HttpClient client,
        string accessToken,
        string projectId,
        PushNotification notification,
        DeviceRegistration registration,
        ILogger logger,
        CancellationToken cancellationToken
    )
    {
        var url = $"https://fcm.googleapis.com/v1/projects/{projectId}/messages:send";

        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new ByteArrayContent(FcmPayloadBuilder.Build(notification, registration.DeviceToken))
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        if (response.IsSuccessStatusCode)
            return PushDeliveryResult.Success(registration, providerMessageId: ParseName(body));

        return MapFailure(registration, response.StatusCode, ParseErrorCode(body), logger);
    }


    static PushDeliveryResult MapFailure(DeviceRegistration registration, HttpStatusCode status, string? errorCode, ILogger logger)
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

        if (status == HttpStatusCode.TooManyRequests || status == HttpStatusCode.ServiceUnavailable)
            return PushDeliveryResult.Failed(registration, PushDeliveryStatus.RateLimited, errorCode ?? status.ToString());

        logger.LogWarning("FCM send failed: {Status} {Reason}", status, errorCode);
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
