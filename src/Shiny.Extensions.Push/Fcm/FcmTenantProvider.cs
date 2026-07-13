using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Shiny.Extensions.Push;
using Shiny.Extensions.Push.Infrastructure;

namespace Shiny.Extensions.Push.Fcm;


/// <summary>
/// The multi-app FCM provider. Claims Android for <em>any</em> app and resolves each device's
/// <see cref="FcmOptions"/> from the scoped <see cref="IPushConfigurationProvider"/> at send time. The OAuth
/// bearer is reused per app on its normal lifetime; the Firebase project id is re-read from the configuration
/// each send. Batches are split by app, so one <c>/batch</c> request is issued per app with that app's
/// credentials.
/// </summary>
public sealed class FcmTenantProvider(
    IHttpClientFactory httpClientFactory,
    IServiceScopeFactory scopeFactory,
    ILogger<FcmTenantProvider> logger
) : IPushProvider, IPushBatchProvider
{
    static readonly TimeSpan Skew = TimeSpan.FromMinutes(5);

    readonly CredentialTokenCache tokenCache = new();


    public string Identifier => "fcm";

    public int MaxBatchSize => FcmSender.BatchLimit;

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


    public async Task<IReadOnlyList<PushDeliveryResult>> SendBatch(PushNotification notification, IReadOnlyList<DeviceRegistration> registrations, CancellationToken cancellationToken = default)
    {
        if (registrations.Count == 0)
            return [];

        // Registrations in one batch can span tenants; each tenant needs its own bearer + project + request.
        var byTenant = registrations
            .Select((reg, index) => (reg, index))
            .GroupBy(x => x.reg.AppId ?? string.Empty, StringComparer.Ordinal);

        var results = new PushDeliveryResult?[registrations.Count];

        foreach (var group in byTenant)
        {
            var items = group.ToList();
            var resolved = await Resolve(group.Key, cancellationToken).ConfigureAwait(false);

            if (!resolved.Ok)
            {
                foreach (var (reg, index) in items)
                    results[index] = PushDeliveryResult.Failed(reg, PushDeliveryStatus.Error, resolved.Reason, resolved.Error);
                continue;
            }

            var regs = items.Select(x => x.reg).ToList();
            var groupResults = await FcmSender
                .SendBatch(Client(), resolved.Bearer!, resolved.Account!.ProjectId, notification, regs, logger, cancellationToken)
                .ConfigureAwait(false);

            for (var i = 0; i < items.Count; i++)
                results[items[i].index] = groupResults[i];
        }

        // Every slot is filled — each tenant group writes a result (success or the Error fallback) per device.
        return results!;
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
