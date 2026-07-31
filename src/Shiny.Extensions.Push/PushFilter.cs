namespace Shiny.Extensions.Push;


/// <summary>
/// A structured, declarative description of which device registrations to target. Replaces an
/// <c>Expression&lt;Func&lt;DeviceRegistration,bool&gt;&gt;</c> deliberately: a structured filter is
/// fully AOT/trim-safe (no expression interpreter, no reflection), and it translates cleanly to a
/// native query in any repository backend (DocumentDB, EF, Dapper, in-memory).
/// </summary>
/// <remarks>
/// All set criteria are combined with AND. A property left null means "no constraint on this".
/// An empty filter (<see cref="Broadcast"/>) matches every registration.
/// </remarks>
public record PushFilter
{
    /// <summary>Target a single user across all their devices.</summary>
    public string? UserIdentifier { get; init; }

    /// <summary>Target by tags. Combined per <see cref="TagMatch"/>.</summary>
    public IReadOnlyList<string>? Tags { get; init; }

    /// <summary>Whether <see cref="Tags"/> must all be present (<see cref="TagMatch.All"/>) or any (<see cref="TagMatch.Any"/>).</summary>
    public TagMatch TagMatch { get; init; } = TagMatch.Any;

    /// <summary>Restrict to specific platforms.</summary>
    public IReadOnlyList<DevicePlatform>? Platforms { get; init; }

    /// <summary>Target explicit device tokens.</summary>
    public IReadOnlyList<string>? DeviceTokens { get; init; }

    /// <summary>Restrict to a single environment (sandbox vs production).</summary>
    public PushEnvironment? Environment { get; init; }

    /// <summary>Restrict to a single application/provider key (multi-app servers).</summary>
    public string? AppId { get; init; }

    /// <summary>Target registrations subscribed to this topic.</summary>
    public string? Topic { get; init; }

    /// <summary>
    /// Which kind of token to target. Unlike the other clauses this always constrains — it defaults to
    /// <see cref="PushTokenKind.Device"/> so a broadcast can never accidentally fire an alert at a Live
    /// Activity token (APNs would reject it and the manager would prune it). Live Activity sends set this,
    /// which <see cref="LiveActivityPushExtensions.SendLiveActivity"/> does for you.
    /// </summary>
    public PushTokenKind TokenKind { get; init; } = PushTokenKind.Device;

    /// <summary>A filter that matches every registration.</summary>
    public static PushFilter Broadcast { get; } = new();

    /// <summary>
    /// Evaluates this filter against a registration in memory. Used by the in-memory repository and
    /// available to backends that cannot translate every clause natively (apply the rest in-process).
    /// </summary>
    public bool Matches(DeviceRegistration registration)
    {
        if (this.TokenKind != registration.TokenKind)
            return false;

        if (this.UserIdentifier is not null &&
            !string.Equals(this.UserIdentifier, registration.UserIdentifier, StringComparison.Ordinal))
            return false;

        if (this.Environment is not null && this.Environment != registration.Environment)
            return false;

        if (this.AppId is not null &&
            !string.Equals(this.AppId, registration.AppId, StringComparison.Ordinal))
            return false;

        if (this.Topic is not null && !registration.Topics.Contains(this.Topic))
            return false;

        if (this.Platforms is { Count: > 0 } && !this.Platforms.Contains(registration.Platform))
            return false;

        if (this.DeviceTokens is { Count: > 0 } &&
            !this.DeviceTokens.Contains(registration.DeviceToken, StringComparer.Ordinal))
            return false;

        if (this.Tags is { Count: > 0 })
        {
            var regTags = registration.Tags;
            var matched = this.TagMatch == TagMatch.All
                ? this.Tags.All(t => regTags.Contains(t))
                : this.Tags.Any(t => regTags.Contains(t));

            if (!matched)
                return false;
        }
        return true;
    }
}
