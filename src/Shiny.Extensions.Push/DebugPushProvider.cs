using Microsoft.Extensions.Logging;

namespace Shiny.Extensions.Push;


/// <summary>
/// A provider that "delivers" by logging. Claims every platform. Useful for local development, tests,
/// and verifying targeting/interceptors without real APNs/FCM credentials.
/// </summary>
public sealed class DebugPushProvider : IPushProvider
{
    readonly ILogger<DebugPushProvider> logger;
    public DebugPushProvider(ILogger<DebugPushProvider> logger) => this.logger = logger;

    public string Identifier => "debug";
    public bool CanDeliver(DeviceRegistration registration) => true;

    public Task<PushDeliveryResult> Send(PushNotification notification, DeviceRegistration registration, CancellationToken cancellationToken = default)
    {
        this.logger.LogInformation(
            "[DEBUG PUSH] -> {Platform} {Token}: \"{Title}\" / \"{Message}\" (data: {DataCount})",
            registration.Platform, registration.DeviceToken, notification.Title, notification.Message, notification.Data.Count
        );
        return Task.FromResult(PushDeliveryResult.Success(registration));
    }
}
