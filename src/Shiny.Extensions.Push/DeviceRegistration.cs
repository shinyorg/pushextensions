namespace Shiny.Extensions.Push;


/// <summary>
/// A single device's push registration. This is the unit stored by <see cref="IPushRepository"/> and
/// targeted by <see cref="PushFilter"/>.
/// </summary>
/// <remarks>
/// Tokens rotate. A device's <see cref="DeviceToken"/> can change over the life of an install, so the
/// stable identity is <see cref="DeviceId"/> (an installation id supplied by the client). Repositories
/// should upsert on (<see cref="DeviceId"/>) when present, falling back to (<see cref="DeviceToken"/>,
/// <see cref="Platform"/>).
/// </remarks>
public record DeviceRegistration
{
    /// <summary>
    /// The transport token. For APNs/FCM this is the device token string. For WebPush this is the
    /// subscription endpoint — the p256dh/auth keys live in <see cref="Data"/>.
    /// </summary>
    public required string DeviceToken { get; init; }

    /// <summary>The device platform. Determines which <see cref="IPushProvider"/> handles delivery.</summary>
    public required DevicePlatform Platform { get; init; }

    /// <summary>
    /// Which application/provider key this device belongs to, for multi-app servers. Matches the key
    /// passed to a keyed provider registration (e.g. <c>AddApns("my-app", …)</c>). Null/empty means the
    /// default (keyless) provider for the platform.
    /// </summary>
    public string? AppId { get; init; }

    /// <summary>
    /// Stable per-install identity, independent of the rotating <see cref="DeviceToken"/>. Optional but
    /// strongly recommended so re-registration updates rather than duplicates.
    /// </summary>
    public string? DeviceId { get; init; }

    /// <summary>The user this device belongs to, enabling "notify a user across all their devices".</summary>
    public string? UserIdentifier { get; init; }

    /// <summary>Free-form tags/segments for targeting (e.g. "beta", "sports", "en-US").</summary>
    public IReadOnlyList<string> Tags { get; init; } = [];

    /// <summary>
    /// Named topic subscriptions for pub/sub-style fan-out. Managed via
    /// <see cref="IPushManager.SubscribeToTopic"/> / <see cref="IPushManager.UnsubscribeFromTopic"/> and
    /// targeted via <see cref="IPushManager.SendToTopic"/>. Distinct from <see cref="Tags"/>, which are
    /// arbitrary segmentation.
    /// </summary>
    public IReadOnlyList<string> Topics { get; init; } = [];

    /// <summary>BCP-47 locale (e.g. "en-US") used by localization interceptors.</summary>
    public string? Locale { get; init; }

    /// <summary>Client app version, for version-gated payloads.</summary>
    public string? AppVersion { get; init; }

    /// <summary>Sandbox vs production. APNs/FCM tokens are environment-specific.</summary>
    public PushEnvironment Environment { get; init; } = PushEnvironment.Production;

    /// <summary>Optional expiry / last-seen marker for housekeeping and stale-token pruning.</summary>
    public DateTimeOffset? ExpiresAt { get; init; }

    /// <summary>
    /// Provider-specific data. For WebPush this carries the "p256dh" and "auth" keys. Kept as a string
    /// dictionary deliberately — it stays AOT/trim-safe and serializes trivially in any repository.
    /// </summary>
    public IReadOnlyDictionary<string, string>? Data { get; init; }
}
