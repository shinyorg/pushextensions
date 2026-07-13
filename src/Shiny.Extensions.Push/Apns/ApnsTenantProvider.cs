using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Shiny.Extensions.Push;
using Shiny.Extensions.Push.Infrastructure;

namespace Shiny.Extensions.Push.Apns;


/// <summary>
/// The multi-app APNs provider. Claims iOS/macOS for <em>any</em> app and resolves each device's
/// <see cref="ApnsOptions"/> from the scoped <see cref="IPushConfigurationProvider"/> at send time (keyed by the
/// device's <see cref="DeviceRegistration.AppId"/>). The provider JWT is reused per app on Apple's token
/// lifetime; a refresh re-reads the configuration, so a rotated .p8 is picked up within that window.
/// </summary>
public sealed class ApnsTenantProvider(
    IHttpClientFactory httpClientFactory,
    IServiceScopeFactory scopeFactory,
    ILogger<ApnsTenantProvider> logger
) : IPushProvider
{
    // Apple allows a provider token up to 60 min; refresh a little early.
    static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(50);
    static readonly TimeSpan Skew = TimeSpan.FromMinutes(10);

    readonly CredentialTokenCache jwtCache = new();


    public string Identifier => "apns";

    public bool CanDeliver(DeviceRegistration registration)
        => registration.Platform is DevicePlatform.iOS or DevicePlatform.MacOS;


    public async Task<PushDeliveryResult> Send(PushNotification notification, DeviceRegistration registration, CancellationToken cancellationToken = default)
    {
        var appId = registration.AppId ?? string.Empty;

        ApnsOptions options;
        try
        {
            var config = await ScopedConfiguration.Resolve(scopeFactory, appId, cancellationToken).ConfigureAwait(false);
            if (config?.Apns is not { } apns)
                return PushDeliveryResult.Failed(registration, PushDeliveryStatus.Error, "app not configured for APNs");

            options = apns;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to resolve APNs configuration for app {AppId}", appId);
            return PushDeliveryResult.Failed(registration, PushDeliveryStatus.Error, "configuration resolution failed", ex);
        }

        // The JWT (KeyId/TeamId/.p8) is reused across the lifetime; BundleId/ForceEnvironment come from the
        // just-resolved options so they always reflect the store's current value.
        var jwt = await jwtCache
            .Get(appId, Skew, _ => new((ApnsJwt.Generate(options), (int)Lifetime.Add(Skew).TotalSeconds)), cancellationToken)
            .ConfigureAwait(false);

        return await ApnsSender
            .Send(
                httpClientFactory.CreateClient(ApnsProvider.HttpClientName),
                options,
                jwt,
                () => jwtCache.Invalidate(appId),
                notification,
                registration,
                logger,
                cancellationToken
            )
            .ConfigureAwait(false);
    }
}
