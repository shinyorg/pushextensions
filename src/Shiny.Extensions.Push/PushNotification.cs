namespace Shiny.Extensions.Push;


/// <summary>
/// A platform-neutral push notification. Cross-cutting fields live here; per-platform escape hatches
/// live in the <see cref="Apple"/>/<see cref="Android"/>/<see cref="WebPush"/>/<see cref="Windows"/>
/// option objects so a provider can honour native capabilities without the core model having to model
/// all of them.
/// </summary>
/// <remarks>
/// <see cref="Title"/> and <see cref="Message"/> are nullable on purpose: a data-only/silent push
/// (background sync) carries no visible alert, only <see cref="Data"/> + the platform's
/// content-available flag.
/// </remarks>
public record PushNotification
{
    public string? Title { get; init; }
    public string? Message { get; init; }

    /// <summary>A deep link / route the client app should open when the notification is tapped.</summary>
    public string? DeepLink { get; init; }

    /// <summary>Badge count. Null leaves the badge untouched; 0 clears it.</summary>
    public int? Badge { get; init; }

    /// <summary>Sound name, or "default".</summary>
    public string? Sound { get; init; }

    /// <summary>
    /// Collapse/coalesce key. Newer notifications with the same id replace undelivered older ones
    /// (maps to <c>apns-collapse-id</c> / FCM <c>collapse_key</c>).
    /// </summary>
    public string? CollapseId { get; init; }

    /// <summary>How long the transport should attempt delivery before discarding. Null = provider default.</summary>
    public TimeSpan? TimeToLive { get; init; }

    public PushPriority Priority { get; init; } = PushPriority.High;

    /// <summary>Custom key/value payload delivered to the app. String-typed to stay AOT/trim-safe.</summary>
    public IReadOnlyDictionary<string, string> Data { get; init; } = new Dictionary<string, string>();

    /// <summary>Apple (APNs) specific overrides. Honoured by the APNs provider.</summary>
    public ApplePushOptions? Apple { get; init; }

    /// <summary>Android (FCM) specific overrides. Reserved for the FCM provider.</summary>
    public AndroidPushOptions? Android { get; init; }

    /// <summary>WebPush specific overrides. Reserved for the WebPush provider.</summary>
    public WebPushOptions? WebPush { get; init; }

    /// <summary>Windows (WNS) specific overrides. Honoured by the WNS provider.</summary>
    public WindowsPushOptions? Windows { get; init; }
}


/// <summary>Apple/APNs specific notification overrides.</summary>
public record ApplePushOptions
{
    /// <summary>Maps to <c>aps.category</c> for actionable notifications.</summary>
    public string? Category { get; init; }

    /// <summary>Maps to <c>aps.thread-id</c> for grouping.</summary>
    public string? ThreadId { get; init; }

    /// <summary>Maps to <c>aps.subtitle</c>.</summary>
    public string? Subtitle { get; init; }

    /// <summary>Sets <c>aps.mutable-content</c> so a Notification Service Extension can modify the payload.</summary>
    public bool MutableContent { get; init; }

    /// <summary>
    /// Sends a silent/background push: sets <c>aps.content-available</c> and suppresses the alert.
    /// Apple requires low priority for these.
    /// </summary>
    public bool ContentAvailable { get; init; }

    /// <summary>
    /// Overrides <c>apns-topic</c> for this push (e.g. a VoIP or complication topic). Defaults to the
    /// provider's configured bundle id.
    /// </summary>
    public string? TopicOverride { get; init; }

    /// <summary>Overrides <c>apns-push-type</c> (e.g. "voip", "background"). Defaults are inferred.</summary>
    public string? PushTypeOverride { get; init; }
}


/// <summary>Android/FCM specific notification overrides. Reserved for a future FCM provider.</summary>
public record AndroidPushOptions
{
    /// <summary>Android notification channel id.</summary>
    public string? ChannelId { get; init; }

    /// <summary>Notification icon resource name.</summary>
    public string? Icon { get; init; }

    /// <summary>Notification color (e.g. "#RRGGBB").</summary>
    public string? Color { get; init; }

    /// <summary>Big-picture / large image url.</summary>
    public string? ImageUrl { get; init; }
}


/// <summary>WebPush specific notification overrides. Reserved for a future WebPush provider.</summary>
public record WebPushOptions
{
    /// <summary>Icon url shown in the browser notification.</summary>
    public string? Icon { get; init; }

    /// <summary>Urgency hint sent to the push service ("very-low", "low", "normal", "high").</summary>
    public string? Urgency { get; init; }
}


/// <summary>Windows (WNS) specific notification overrides. Honoured by the WNS provider.</summary>
public record WindowsPushOptions
{
    /// <summary>
    /// The WNS notification kind, which sets the <c>X-WNS-Type</c> header (and content type). Defaults to
    /// <see cref="WnsNotificationType.Toast"/>.
    /// </summary>
    public WnsNotificationType Type { get; init; } = WnsNotificationType.Toast;

    /// <summary>
    /// A complete WNS payload sent verbatim instead of the one built from the cross-cutting fields — toast/
    /// tile/badge XML, or an arbitrary string for a raw notification. Use for tile/badge updates or advanced
    /// toast templates the neutral model doesn't cover.
    /// </summary>
    public string? Payload { get; init; }

    /// <summary>
    /// The toast <c>launch</c> activation argument (passed to the app when the toast is tapped). Falls back
    /// to the notification's <see cref="PushNotification.DeepLink"/>.
    /// </summary>
    public string? Launch { get; init; }
}
