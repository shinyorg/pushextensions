using Microsoft.Extensions.Logging;
using Shiny.Extensions.Push;

namespace Shiny.Extensions.Push.Fcm;


/// <summary>
/// Delivers to Android devices via Firebase Cloud Messaging (HTTP v1). OAuth2 service-account auth with a
/// cached bearer token. Single sends hit <c>messages:send</c>; the manager can also batch up to
/// <see cref="MaxBatchSize"/> devices into one multipart <c>/batch</c> request (FCM multicast). AOT-safe.
/// This is the keyed (static, single-app) provider; see the tenant-aware provider for multi-tenant servers.
/// </summary>
public sealed class FcmProvider(
    string appKey,
    IHttpClientFactory httpClientFactory,
    FcmAccessTokenProvider tokenProvider,
    ILogger<FcmProvider> logger
) : IPushProvider, IPushBatchProvider
{
    public const string HttpClientName = "shiny-fcm";

    readonly string appKey = appKey ?? string.Empty;


    public string Identifier => appKey.Length == 0 ? "fcm" : $"fcm:{appKey}";

    public int MaxBatchSize => FcmSender.BatchLimit;

    public bool CanDeliver(DeviceRegistration registration)
        => registration.Platform == DevicePlatform.Android
            && string.Equals(registration.AppId ?? string.Empty, appKey, StringComparison.Ordinal);


    public async Task<PushDeliveryResult> Send(PushNotification notification, DeviceRegistration registration, CancellationToken cancellationToken = default)
    {
        var accessToken = await tokenProvider.GetAccessToken(cancellationToken).ConfigureAwait(false);
        return await FcmSender
            .Send(httpClientFactory.CreateClient(HttpClientName), accessToken, tokenProvider.ProjectId, notification, registration, logger, cancellationToken)
            .ConfigureAwait(false);
    }


    public async Task<IReadOnlyList<PushDeliveryResult>> SendBatch(PushNotification notification, IReadOnlyList<DeviceRegistration> registrations, CancellationToken cancellationToken = default)
    {
        if (registrations.Count == 0)
            return [];

        var accessToken = await tokenProvider.GetAccessToken(cancellationToken).ConfigureAwait(false);
        return await FcmSender
            .SendBatch(httpClientFactory.CreateClient(HttpClientName), accessToken, tokenProvider.ProjectId, notification, registrations, logger, cancellationToken)
            .ConfigureAwait(false);
    }


    // Retained for tests that exercise the FCM error-detail parser directly.
    internal static string? ParseErrorCode(string body) => FcmSender.ParseErrorCode(body);
}
