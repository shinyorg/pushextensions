namespace Shiny.Extensions.Push;


/// <summary>
/// The platform a device registration belongs to. Note this is the device platform, NOT the
/// transport provider — an <see cref="IPushProvider"/> decides which platforms it can deliver to
/// (e.g. the APNs provider claims <see cref="iOS"/> and <see cref="MacOS"/>).
/// </summary>
public enum DevicePlatform
{
    iOS,
    MacOS,
    Android,
    Windows,
    WebBrowser
}


/// <summary>
/// APNs/FCM credentials and tokens are not interchangeable between the sandbox and production
/// environments. This is tracked per-registration so a single server can serve both (e.g. TestFlight
/// builds vs App Store builds).
/// </summary>
public enum PushEnvironment
{
    Production,
    Sandbox
}


/// <summary>
/// Delivery priority. Maps to <c>apns-priority</c> (10 = high, 5 = low) and the equivalent on other
/// transports. Low priority is required by Apple for silent/background (content-available) pushes.
/// </summary>
public enum PushPriority
{
    Normal,
    High
}


/// <summary>
/// The outcome of a single device delivery, normalized across providers so the manager can react
/// (prune dead tokens, retry, etc.) without knowing transport specifics.
/// </summary>
public enum PushDeliveryStatus
{
    /// <summary>The push was accepted by the provider.</summary>
    Success,

    /// <summary>The token is valid-looking but no longer registered (app uninstalled). Prune it.</summary>
    TokenExpired,

    /// <summary>The token is malformed or rejected as invalid. Prune it.</summary>
    InvalidToken,

    /// <summary>The provider throttled us. Safe to retry later with backoff.</summary>
    RateLimited,

    /// <summary>A transient or unknown provider/transport error. The token is left intact.</summary>
    Error,

    /// <summary>No registered provider could deliver to this registration's platform.</summary>
    NoProvider,

    /// <summary>An interceptor chose to skip this device.</summary>
    Skipped
}


/// <summary>
/// What a <see cref="DeviceRegistration.DeviceToken"/> actually addresses. Apple issues Live Activity
/// tokens that look like device tokens but are only valid for the <c>liveactivity</c> push type and its
/// own topic — sending an ordinary alert to one is rejected (<c>DeviceTokenNotForTopic</c>) and would get
/// the token pruned as invalid. Tracking the kind lets one repository hold all three safely, and makes
/// <see cref="PushFilter"/> exclude Live Activity tokens from normal sends by default.
/// </summary>
public enum PushTokenKind
{
    /// <summary>An ordinary APNs/FCM/WNS/WebPush token for alerts and background pushes. The default.</summary>
    Device,

    /// <summary>
    /// An ActivityKit push-to-start token (iOS 17.2+). One per app install, long-lived, and the only token
    /// that can start an activity while the app isn't running.
    /// </summary>
    LiveActivityStart,

    /// <summary>
    /// A single Live Activity's update token. Issued when that activity starts and dead when it ends, so
    /// expect these to churn — a 410/Unregistered on one simply means the activity is over.
    /// </summary>
    LiveActivityUpdate
}


/// <summary>How a registration's tags are matched against a <see cref="PushFilter"/>.</summary>
public enum TagMatch
{
    /// <summary>The registration matches if it has at least one of the requested tags.</summary>
    Any,

    /// <summary>The registration matches only if it has all of the requested tags.</summary>
    All
}


/// <summary>An interceptor's decision for a single device, returned from <see cref="IPushInterceptor.BeforeSend"/>.</summary>
public enum InterceptorDecision
{
    /// <summary>Proceed with delivery (possibly with a mutated notification).</summary>
    Continue,

    /// <summary>Skip this device. No further interceptors run and the provider is not called.</summary>
    Skip
}


/// <summary>
/// The kind of WNS notification, mapped to the <c>X-WNS-Type</c> header by the Windows provider. Set via
/// <see cref="WindowsPushOptions.Type"/>. Defaults to <see cref="Toast"/>.
/// </summary>
public enum WnsNotificationType
{
    /// <summary>A toast (pop-up) notification — the default. Built as a <c>ToastGeneric</c> payload.</summary>
    Toast,

    /// <summary>A live-tile update. Supply the tile XML via <see cref="WindowsPushOptions.Payload"/>.</summary>
    Tile,

    /// <summary>A badge update (a number or glyph on the app tile).</summary>
    Badge,

    /// <summary>A raw notification — an app-defined payload delivered while the app is running.</summary>
    Raw
}
