using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Shiny.Extensions.Push;
using Shiny.Extensions.Push.Infrastructure;

namespace Shiny.Extensions.Push.Fcm;


/// <summary>
/// The multi-app FCM provider. Claims Android for <em>any</em> app and resolves each device's
/// <see cref="FcmOptions"/> from the scoped <see cref="IPushConfigurationProvider"/> at send time. The OAuth
/// bearer is reused per app on its normal lifetime; the Firebase project id is re-read from the configuration
/// each send. Requests use the supported HTTP v1 <c>messages:send</c> endpoint.
/// </summary>
public sealed class FcmTenantProvider(
    IHttpClientFactory httpClientFactory,
    IServiceScopeFactory scopeFactory,
    ILogger<FcmTenantProvider> logger
) : IPushProvider
{
    static readonly TimeSpan Skew = TimeSpan.FromMinutes(5);

    readonly CredentialTokenCache tokenCache = new();


    public string Identifier => "fcm";

    public bool CanDeliver(DeviceRegistration registration)
        => registration.Platform == DevicePlatform.Android;


    public async Task<PushDeliveryResult> Send(PushNotification notification, DeviceRegistration registration, CancellationToken cancellationToken = default)
    {
        var appId = registration.AppId ?? string.Empty;
        var resolved = await Resolve(appId, cancellationToken).ConfigureAwait(false);
        if (!resolved.Ok)
            return PushDeliveryResult.Failed(registration, PushDeliveryStatus.Error, resolved.Reason, resolved.Error);

        return await FcmSender
            .Send(Client(), resolved.Bearer!, resolved.Account!.ProjectId, notification, registration, logger, cancellationToken)
            .ConfigureAwait(false);
    }


    HttpClient Client() => httpClientFactory.CreateClient(FcmProvider.HttpClientName);


    async ValueTask<Resolution> Resolve(string appId, CancellationToken cancellationToken)
    {
        FcmServiceAccount account;
        try
        {
            var config = await ScopedConfiguration.Resolve(scopeFactory, appId, cancellationToken).ConfigureAwait(false);
            if (config?.Fcm is not { } fcm)
                return Resolution.Fail("app not configured for FCM");

            account = fcm.ResolveServiceAccount();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to resolve FCM configuration for app {AppId}", appId);
            return Resolution.Fail("configuration resolution failed", ex);
        }

        var bearer = await tokenCache
            .Get(appId, Skew, ct => FcmToken.Exchange(Client(), account, ct), cancellationToken)
            .ConfigureAwait(false);

        return new Resolution(true, account, bearer, null, null);
    }


    readonly record struct Resolution(bool Ok, FcmServiceAccount? Account, string? Bearer, string? Reason, Exception? Error)
    {
        public static Resolution Fail(string reason, Exception? error = null) => new(false, null, null, reason, error);
    }
}
