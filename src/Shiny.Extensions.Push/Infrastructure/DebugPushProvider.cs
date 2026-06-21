using Microsoft.Extensions.Logging;

using Shiny.Extensions.Push;

namespace Shiny.Extensions.Push.Infrastructure;


/// <summary>
/// A provider that "delivers" by logging. Claims every platform. Useful for local development, tests,
/// and verifying targeting/interceptors without real APNs/FCM credentials.
/// </summary>
public sealed class DebugPushProvider(ILogger<DebugPushProvider> logger) : IPushProvider
{
    public string Identifier => "debug";
    public bool CanDeliver(DeviceRegistration registration) => true;

    public Task<PushDeliveryResult> Send(PushNotification notification, DeviceRegistration registration, CancellationToken cancellationToken = default)
    {
        logger.LogInformation(
            "[DEBUG PUSH] -> {Platform} {Token}: \"{Title}\" / \"{Message}\" (data: {DataCount})",
            registration.Platform, registration.DeviceToken, notification.Title, notification.Message, notification.Data.Count
        );
        return Task.FromResult(PushDeliveryResult.Success(registration));
    }
}
