using Shiny.Extensions.Push.Apns;
using Shiny.Extensions.Push.Fcm;
using Shiny.Extensions.Push.Wns;

// NB: WebPush's VAPID config is Shiny.Extensions.Push.WebPush.WebPushOptions, which collides by simple name
// with the per-notification Shiny.Extensions.Push.WebPushOptions — so it is fully qualified below rather than
// imported.
namespace Shiny.Extensions.Push;


/// <summary>
/// Supplies per-app push configuration to the multi-app providers at send time. The delivery providers
/// resolve a device's credentials from this by its <see cref="DeviceRegistration.AppId"/>, so the same
/// providers serve one app, many statically-configured apps, or an unbounded set of dynamically-managed
/// tenants — the only difference is which implementation you register:
/// <list type="bullet">
/// <item>the built-in static provider (<c>AddStaticConfiguration</c>), which returns fixed values it was
/// configured with — the "just like before" path; or</item>
/// <item>your own implementation (<c>UseConfigurationProvider</c>) backed by a database, secrets vault, or
/// admin API, so apps/tenants can be onboarded and their keys rotated without an app restart.</item>
/// </list>
/// </summary>
/// <remarks>
/// <para>
/// This is called on the send hot path, so an implementation that hits a database or network should cache
/// internally — the library does not cache the <see cref="PushConfiguration"/> it gets back. What the library
/// <em>does</em> reuse is the minted APNs JWT / OAuth bearer per app, refreshed on the transport's normal
/// token lifetime; when a token refreshes it re-reads whatever this provider currently returns, so a rotated
/// key/secret is picked up within one token-lifetime window with no extra bookkeeping.
/// </para>
/// <para>Implementations must be thread-safe — configuration is resolved concurrently.</para>
/// </remarks>
public interface IPushConfigurationProvider
{
    /// <summary>
    /// Return the configuration for <paramref name="appId"/> (a device's <see cref="DeviceRegistration.AppId"/>,
    /// null/empty for the default app), or <c>null</c> if the app is unknown. A returned
    /// <see cref="PushConfiguration"/> only needs the option objects for the transports that app actually uses;
    /// the rest stay null and the corresponding provider reports the device as "not configured" rather than
    /// delivering.
    /// </summary>
    ValueTask<PushConfiguration?> GetConfiguration(string appId, CancellationToken cancellationToken = default);
}


/// <summary>
/// One app's push configuration: the same per-transport option objects the keyed registration uses
/// (<see cref="ApnsOptions"/>, <see cref="FcmOptions"/>, <see cref="WebPush.WebPushOptions"/>,
/// <see cref="WnsOptions"/>), bundled per app. Leave a transport null if the app does not use it.
/// </summary>
public sealed record PushConfiguration
{
    /// <summary>The app/tenant identifier — matches a device's <see cref="DeviceRegistration.AppId"/> (empty = default).</summary>
    public required string AppId { get; init; }

    /// <summary>APNs (iOS/macOS) configuration for this app, or null if unused.</summary>
    public ApnsOptions? Apns { get; init; }

    /// <summary>FCM (Android) configuration for this app, or null if unused.</summary>
    public FcmOptions? Fcm { get; init; }

    /// <summary>Web Push (browser) VAPID configuration for this app, or null if unused.</summary>
    public WebPush.WebPushOptions? WebPush { get; init; }

    /// <summary>WNS (Windows) configuration for this app, or null if unused.</summary>
    public WnsOptions? Wns { get; init; }
}
