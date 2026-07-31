namespace Shiny.Extensions.Push;


/// <summary>
/// Send/registration helpers for Live Activity pushes. These live as extensions (rather than on
/// <see cref="IPushManager"/>) so a custom manager implementation isn't broken by their addition.
/// </summary>
public static class LiveActivityPushExtensions
{
    /// <summary>
    /// Stores a Live Activity token the app reported. Identical to
    /// <see cref="IPushManager.RegisterDevice"/> except it stamps <see cref="DeviceRegistration.TokenKind"/>,
    /// which keeps the token out of ordinary alert/broadcast sends (those would be rejected by APNs and
    /// the token pruned as invalid).
    /// </summary>
    /// <param name="manager">The push manager.</param>
    /// <param name="registration">The registration, whose <c>DeviceToken</c> is the Live Activity token.</param>
    /// <param name="kind">Which Live Activity token this is.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    public static Task RegisterLiveActivityToken(
        this IPushManager manager,
        DeviceRegistration registration,
        PushTokenKind kind,
        CancellationToken cancellationToken = default
    )
    {
        if (kind == PushTokenKind.Device)
            throw new ArgumentException("Use RegisterDevice for ordinary device tokens", nameof(kind));

        return manager.RegisterDevice(registration with { TokenKind = kind }, cancellationToken);
    }


    /// <summary>
    /// Sends a Live Activity push, targeting the token kind its <see cref="LiveActivityPushOptions.Event"/>
    /// requires — push-to-start tokens for <see cref="LiveActivityEvent.Start"/>, per-activity tokens for
    /// update/end — unless <paramref name="filter"/> already names a Live Activity kind.
    /// </summary>
    /// <param name="manager">The push manager.</param>
    /// <param name="notification">A notification carrying <see cref="ApplePushOptions.LiveActivity"/>.</param>
    /// <param name="filter">Audience. Defaults to every registration of the inferred token kind.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    public static Task<PushSendResult> SendLiveActivity(
        this IPushManager manager,
        PushNotification notification,
        PushFilter? filter = null,
        CancellationToken cancellationToken = default
    )
    {
        var live = notification.Apple?.LiveActivity
            ?? throw new ArgumentException("The notification has no Apple.LiveActivity options — use LiveActivityPush.Start/Update/End", nameof(notification));

        filter ??= PushFilter.Broadcast;
        if (filter.TokenKind == PushTokenKind.Device)
        {
            filter = filter with
            {
                TokenKind = live.Event == LiveActivityEvent.Start
                    ? PushTokenKind.LiveActivityStart
                    : PushTokenKind.LiveActivityUpdate
            };
        }
        return manager.Send(notification, filter, cancellationToken);
    }


    /// <summary>
    /// Sends a Live Activity push to explicit tokens — the common case for update/end, where the server
    /// already knows the activity token it wants to address.
    /// </summary>
    /// <param name="manager">The push manager.</param>
    /// <param name="tokens">The Live Activity tokens to target.</param>
    /// <param name="notification">A notification carrying <see cref="ApplePushOptions.LiveActivity"/>.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    public static Task<PushSendResult> SendLiveActivityToTokens(
        this IPushManager manager,
        IEnumerable<string> tokens,
        PushNotification notification,
        CancellationToken cancellationToken = default
    ) => manager.SendLiveActivity(
        notification,
        new PushFilter { DeviceTokens = [.. tokens] },
        cancellationToken
    );
}
