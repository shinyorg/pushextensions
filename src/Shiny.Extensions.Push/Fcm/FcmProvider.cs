using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Shiny.Extensions.Push;

namespace Shiny.Extensions.Push.Fcm;


/// <summary>
/// Delivers to Android devices via Firebase Cloud Messaging (HTTP v1). OAuth2 service-account auth with a
/// cached bearer token. Single sends hit <c>messages:send</c>; the manager can also batch up to
/// <see cref="MaxBatchSize"/> devices into one multipart <c>/batch</c> request (FCM multicast). AOT-safe.
/// </summary>
public sealed class FcmProvider(
    string appKey,
    IHttpClientFactory httpClientFactory,
    FcmAccessTokenProvider tokenProvider,
    ILogger<FcmProvider> logger
) : IPushProvider, IPushBatchProvider
{
    public const string HttpClientName = "shiny-fcm";

    // FCM HTTP v1 caps a multipart batch at 500 sub-requests.
    const int BatchLimit = 500;
    const string BatchBoundary = "shiny_push_fcm_batch";

    readonly string appKey = appKey ?? string.Empty;


    public string Identifier => appKey.Length == 0 ? "fcm" : $"fcm:{appKey}";

    public int MaxBatchSize => BatchLimit;

    public bool CanDeliver(DeviceRegistration registration)
        => registration.Platform == DevicePlatform.Android
            && string.Equals(registration.AppId ?? string.Empty, appKey, StringComparison.Ordinal);


    public async Task<PushDeliveryResult> Send(PushNotification notification, DeviceRegistration registration, CancellationToken cancellationToken = default)
    {
        var accessToken = await tokenProvider.GetAccessToken(cancellationToken).ConfigureAwait(false);
        var url = $"https://fcm.googleapis.com/v1/projects/{tokenProvider.ProjectId}/messages:send";

        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new ByteArrayContent(FcmPayloadBuilder.Build(notification, registration.DeviceToken))
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        var client = httpClientFactory.CreateClient(HttpClientName);
        using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        if (response.IsSuccessStatusCode)
            return PushDeliveryResult.Success(registration, providerMessageId: ParseName(body));

        var errorCode = ParseErrorCode(body);
        return MapFailure(registration, response.StatusCode, errorCode);
    }


    /// <summary>
    /// Delivers to many devices in a single multipart <c>/batch</c> request (FCM multicast). Each device
    /// gets its own <c>messages:send</c> sub-request, so per-device outcomes (dead tokens, rotated ids) are
    /// preserved. Results are returned in the same order as <paramref name="registrations"/>.
    /// </summary>
    public async Task<IReadOnlyList<PushDeliveryResult>> SendBatch(PushNotification notification, IReadOnlyList<DeviceRegistration> registrations, CancellationToken cancellationToken = default)
    {
        if (registrations.Count == 0)
            return [];

        // A single device isn't worth the multipart envelope — use the normal endpoint.
        if (registrations.Count == 1)
            return [await Send(notification, registrations[0], cancellationToken).ConfigureAwait(false)];

        var accessToken = await tokenProvider.GetAccessToken(cancellationToken).ConfigureAwait(false);
        var path = $"/v1/projects/{tokenProvider.ProjectId}/messages:send";

        var sb = new StringBuilder();
        for (var i = 0; i < registrations.Count; i++)
        {
            var json = Encoding.UTF8.GetString(FcmPayloadBuilder.Build(notification, registrations[i].DeviceToken));
            sb.Append("--").Append(BatchBoundary).Append("\r\n");
            sb.Append("Content-Type: application/http\r\n");
            sb.Append("Content-Transfer-Encoding: binary\r\n");
            sb.Append("Content-ID: ").Append(i + 1).Append("\r\n\r\n");
            sb.Append("POST ").Append(path).Append("\r\n");
            sb.Append("Content-Type: application/json\r\n\r\n");
            sb.Append(json).Append("\r\n");
        }
        sb.Append("--").Append(BatchBoundary).Append("--\r\n");

        using var request = new HttpRequestMessage(HttpMethod.Post, "https://fcm.googleapis.com/batch");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Content = new StringContent(sb.ToString());
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("multipart/mixed")
        {
            Parameters = { new NameValueHeaderValue("boundary", BatchBoundary) }
        };

        var client = httpClientFactory.CreateClient(HttpClientName);
        using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        // A non-2xx on the batch envelope itself (auth, malformed request) fails every device uniformly.
        if (!response.IsSuccessStatusCode)
        {
            logger.LogWarning("FCM batch request failed: {Status}", response.StatusCode);
            var errorCode = ParseErrorCode(body);
            var failed = new List<PushDeliveryResult>(registrations.Count);
            foreach (var registration in registrations)
                failed.Add(MapFailure(registration, response.StatusCode, errorCode));
            return failed;
        }

        var responseBoundary = response.Content.Headers.ContentType?.Parameters
            .FirstOrDefault(p => string.Equals(p.Name, "boundary", StringComparison.OrdinalIgnoreCase))?.Value?.Trim('"');

        return ParseBatchResponse(body, responseBoundary, registrations);
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


    // Splits the multipart/mixed batch response into its sub-responses (one per device, in request order)
    // and maps each back to a delivery result. Missing/extra parts are padded/truncated to one-per-device.
    List<PushDeliveryResult> ParseBatchResponse(string body, string? boundary, IReadOnlyList<DeviceRegistration> registrations)
    {
        var results = new List<PushDeliveryResult>(registrations.Count);

        if (!string.IsNullOrEmpty(boundary))
        {
            var normalized = body.Replace("\r\n", "\n");
            var segments = normalized.Split("--" + boundary);
            foreach (var segment in segments)
            {
                if (results.Count >= registrations.Count)
                    break;

                var part = segment.TrimStart('\n', ' ', '\t');
                if (part.Length == 0 || part.StartsWith("--", StringComparison.Ordinal)) // preamble or closing delimiter
                    continue;
                if (!part.Contains("HTTP/", StringComparison.Ordinal)) // not an application/http part
                    continue;

                var (status, json) = ExtractHttpPart(part);
                results.Add(MapBatchPart(registrations[results.Count], status, json));
            }
        }
        else
        {
            logger.LogWarning("FCM batch response had no multipart boundary; failing the batch");
        }

        // If the server returned fewer parts than we sent, fail the remainder rather than silently dropping.
        while (results.Count < registrations.Count)
            results.Add(PushDeliveryResult.Failed(registrations[results.Count], PushDeliveryStatus.Error, "missing batch response part"));

        return results;
    }


    // A part is: "<part headers>\n\nHTTP/1.1 <code> <reason>\n<inner headers>\n\n<json>" (CRLF already
    // normalized to LF). Pull out the inner status code and JSON body.
    static (HttpStatusCode Status, string Json) ExtractHttpPart(string part)
    {
        var afterPartHeaders = part.IndexOf("\n\n", StringComparison.Ordinal);
        var inner = afterPartHeaders >= 0 ? part[(afterPartHeaders + 2)..] : part;

        var statusCode = HttpStatusCode.OK;
        var statusLineEnd = inner.IndexOf('\n');
        var statusLine = statusLineEnd >= 0 ? inner[..statusLineEnd] : inner;
        var tokens = statusLine.Split(' ');
        if (tokens.Length >= 2 && int.TryParse(tokens[1], out var code))
            statusCode = (HttpStatusCode)code;

        var bodyStart = inner.IndexOf("\n\n", StringComparison.Ordinal);
        var json = bodyStart >= 0 ? inner[(bodyStart + 2)..].Trim() : string.Empty;
        return (statusCode, json);
    }


    PushDeliveryResult MapBatchPart(DeviceRegistration registration, HttpStatusCode status, string json)
        => (int)status is >= 200 and < 300
            ? PushDeliveryResult.Success(registration, providerMessageId: ParseName(json))
            : MapFailure(registration, status, ParseErrorCode(json));
}
