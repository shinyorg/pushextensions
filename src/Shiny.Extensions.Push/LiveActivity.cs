using System.Text.Json;

namespace Shiny.Extensions.Push;


/// <summary>
/// The ActivityKit lifecycle event a Live Activity push carries, written as <c>aps.event</c>.
/// </summary>
public enum LiveActivityEvent
{
    /// <summary>
    /// Starts a brand new Live Activity without the app running (iOS 17.2+ "push to start"). Requires
    /// <see cref="LiveActivityPushOptions.AttributesType"/> + <see cref="LiveActivityPushOptions.Attributes"/>
    /// and must be sent to the device's <em>push-to-start</em> token.
    /// </summary>
    Start,

    /// <summary>Updates an existing activity. Sent to that activity's own push token.</summary>
    Update,

    /// <summary>
    /// Ends an existing activity. Optionally carries a final content state and a
    /// <see cref="LiveActivityPushOptions.DismissalDate"/> controlling when it leaves the Lock Screen.
    /// </summary>
    End
}


/// <summary>How a <see cref="DateTimeOffset"/> is encoded into a Live Activity content state.</summary>
/// <remarks>
/// This matters more than it looks. ActivityKit decodes <c>content-state</c> with a stock Swift
/// <c>JSONDecoder</c>, whose default date strategy (<c>.deferredToDate</c>) reads a <c>Date</c> as
/// <em>seconds since Apple's reference date</em> (2001-01-01 UTC) — not the Unix epoch. Send the wrong
/// one and the widget silently shows a date ~31 years off, or fails to decode the whole state. The
/// default here matches an unmodified Swift <c>Codable</c> struct.
/// </remarks>
public enum LiveActivityDateEncoding
{
    /// <summary>
    /// Seconds since 2001-01-01T00:00:00Z, matching Swift's default <c>Date</c> Codable representation.
    /// Use this unless your widget declares a custom decoding strategy.
    /// </summary>
    AppleReference,

    /// <summary>Seconds since 1970-01-01T00:00:00Z. Use when the widget's ContentState decodes a Unix timestamp.</summary>
    UnixSeconds,

    /// <summary>An ISO-8601 string. Use when the widget's ContentState field is a <c>String</c>, or it sets <c>.iso8601</c>.</summary>
    Iso8601
}


/// <summary>The JSON shape a <see cref="LiveActivityValue"/> writes.</summary>
public enum LiveActivityValueKind
{
    /// <summary>JSON null.</summary>
    Null,
    /// <summary>A JSON string.</summary>
    String,
    /// <summary>A JSON integer.</summary>
    Integer,
    /// <summary>A JSON floating point number.</summary>
    Number,
    /// <summary>A JSON boolean.</summary>
    Boolean,
    /// <summary>A JSON array.</summary>
    Array,
    /// <summary>A nested JSON object.</summary>
    Object,
    /// <summary>Pre-serialized JSON, written verbatim.</summary>
    Raw
}


/// <summary>
/// A single value inside a Live Activity <c>content-state</c> or <c>attributes</c> object.
/// </summary>
/// <remarks>
/// Live Activity state cannot be a <c>string</c> dictionary the way the rest of this library's
/// <see cref="PushNotification.Data"/> is: the widget's Swift <c>ContentState</c> is a strongly typed
/// <c>Codable</c> struct, so a numeric field must arrive as a JSON number and a boolean as a JSON
/// boolean or decoding fails and the update is dropped. This union carries real JSON types while
/// staying AOT/trim-safe (values are written by hand with <see cref="Utf8JsonWriter"/> — no reflection).
/// <para>
/// Implicit conversions exist for <see cref="string"/>, <see cref="int"/>, <see cref="long"/>,
/// <see cref="double"/>, <see cref="bool"/> and <see cref="DateTimeOffset"/>, so a state object usually
/// reads naturally:
/// </para>
/// <code>
/// new Dictionary&lt;string, LiveActivityValue&gt;
/// {
///     ["driverName"] = "Sam",
///     ["stopsRemaining"] = 3,
///     ["progress"] = 0.65,
///     ["isDelivered"] = false,
///     ["eta"] = DateTimeOffset.UtcNow.AddMinutes(12)
/// }
/// </code>
/// </remarks>
public readonly struct LiveActivityValue
{
    LiveActivityValue(LiveActivityValueKind kind, string? text = null, double number = 0, long integer = 0, bool boolean = false, object? complex = null)
    {
        this.Kind = kind;
        this.Text = text;
        this.Number = number;
        this.Integer = integer;
        this.Boolean = boolean;
        this.Complex = complex;
    }


    /// <summary>The JSON shape this value writes.</summary>
    public LiveActivityValueKind Kind { get; }

    internal string? Text { get; }
    internal double Number { get; }
    internal long Integer { get; }
    internal bool Boolean { get; }
    internal object? Complex { get; }


    /// <summary>JSON null.</summary>
    public static LiveActivityValue Null { get; } = new(LiveActivityValueKind.Null);

    /// <summary>A JSON string value.</summary>
    public static LiveActivityValue String(string value) => new(LiveActivityValueKind.String, text: value);

    /// <summary>A JSON integer value.</summary>
    public static LiveActivityValue Int(long value) => new(LiveActivityValueKind.Integer, integer: value);

    /// <summary>A JSON floating point value.</summary>
    public static LiveActivityValue Double(double value) => new(LiveActivityValueKind.Number, number: value);

    /// <summary>A JSON boolean value.</summary>
    public static LiveActivityValue Bool(bool value) => new(LiveActivityValueKind.Boolean, boolean: value);


    /// <summary>
    /// A date encoded for a Swift <c>Codable</c> <c>Date</c> field. Defaults to
    /// <see cref="LiveActivityDateEncoding.AppleReference"/> — see that enum before overriding it.
    /// </summary>
    public static LiveActivityValue Date(DateTimeOffset value, LiveActivityDateEncoding encoding = LiveActivityDateEncoding.AppleReference)
        => encoding switch
        {
            LiveActivityDateEncoding.UnixSeconds => Double(value.ToUnixTimeMilliseconds() / 1000d),
            LiveActivityDateEncoding.Iso8601 => String(value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fffK")),
            // Swift's reference date is 2001-01-01T00:00:00Z == 978307200 Unix seconds.
            _ => Double((value.ToUnixTimeMilliseconds() / 1000d) - 978307200d)
        };


    /// <summary>A JSON array of values.</summary>
    public static LiveActivityValue Array(params LiveActivityValue[] values)
        => new(LiveActivityValueKind.Array, complex: values);

    /// <summary>A JSON array of values.</summary>
    public static LiveActivityValue Array(IReadOnlyList<LiveActivityValue> values)
        => new(LiveActivityValueKind.Array, complex: values);

    /// <summary>A nested JSON object, for a ContentState field that is itself a Codable struct.</summary>
    public static LiveActivityValue Object(IReadOnlyDictionary<string, LiveActivityValue> values)
        => new(LiveActivityValueKind.Object, complex: values);

    /// <summary>
    /// Pre-serialized JSON written verbatim. The escape hatch for a shape this union doesn't model;
    /// the caller owns its validity (it is validated on write and will throw if malformed).
    /// </summary>
    public static LiveActivityValue Json(string rawJson) => new(LiveActivityValueKind.Raw, text: rawJson);


    /// <summary>Converts a string to a JSON string value.</summary>
    public static implicit operator LiveActivityValue(string value) => String(value);

    /// <summary>Converts an int to a JSON integer value.</summary>
    public static implicit operator LiveActivityValue(int value) => Int(value);

    /// <summary>Converts a long to a JSON integer value.</summary>
    public static implicit operator LiveActivityValue(long value) => Int(value);

    /// <summary>Converts a double to a JSON number value.</summary>
    public static implicit operator LiveActivityValue(double value) => Double(value);

    /// <summary>Converts a bool to a JSON boolean value.</summary>
    public static implicit operator LiveActivityValue(bool value) => Bool(value);

    /// <summary>Converts a date to a Swift-reference-date encoded number. See <see cref="LiveActivityDateEncoding"/>.</summary>
    public static implicit operator LiveActivityValue(DateTimeOffset value) => Date(value);


    internal void Write(Utf8JsonWriter writer)
    {
        switch (this.Kind)
        {
            case LiveActivityValueKind.String:
                writer.WriteStringValue(this.Text);
                break;

            case LiveActivityValueKind.Integer:
                writer.WriteNumberValue(this.Integer);
                break;

            case LiveActivityValueKind.Number:
                writer.WriteNumberValue(this.Number);
                break;

            case LiveActivityValueKind.Boolean:
                writer.WriteBooleanValue(this.Boolean);
                break;

            case LiveActivityValueKind.Raw:
                writer.WriteRawValue(this.Text ?? "null");
                break;

            case LiveActivityValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in (IReadOnlyList<LiveActivityValue>)this.Complex!)
                    item.Write(writer);
                writer.WriteEndArray();
                break;

            case LiveActivityValueKind.Object:
                WriteObject(writer, (IReadOnlyDictionary<string, LiveActivityValue>)this.Complex!);
                break;

            default:
                writer.WriteNullValue();
                break;
        }
    }


    internal static void WriteObject(Utf8JsonWriter writer, IReadOnlyDictionary<string, LiveActivityValue> values)
    {
        writer.WriteStartObject();
        foreach (var kvp in values)
        {
            writer.WritePropertyName(kvp.Key);
            kvp.Value.Write(writer);
        }
        writer.WriteEndObject();
    }
}


/// <summary>
/// Turns a <see cref="PushNotification"/> into an ActivityKit Live Activity push (iOS 16.1+). Set it on
/// <see cref="ApplePushOptions.LiveActivity"/> and the APNs transport switches the whole request over:
/// <c>apns-push-type: liveactivity</c>, the <c>&lt;bundle-id&gt;.push-type.liveactivity</c> topic, and an
/// <c>aps</c> body of <c>event</c>/<c>timestamp</c>/<c>content-state</c> instead of an alert.
/// </summary>
/// <remarks>
/// Which token you send to depends on <see cref="Event"/>:
/// <list type="bullet">
/// <item><see cref="LiveActivityEvent.Start"/> goes to the device's <em>push-to-start</em> token
/// (<see cref="PushTokenKind.LiveActivityStart"/>) — one per app install, valid before any activity exists.</item>
/// <item><see cref="LiveActivityEvent.Update"/> and <see cref="LiveActivityEvent.End"/> go to that specific
/// activity's token (<see cref="PushTokenKind.LiveActivityUpdate"/>), which the app reports when the
/// activity starts and which dies with it.</item>
/// </list>
/// Neither is the device's ordinary APNs token, and APNs rejects a mismatch with <c>DeviceTokenNotForTopic</c>.
/// Payloads are capped at 4KB.
/// </remarks>
public record LiveActivityPushOptions
{
    /// <summary>The lifecycle event. Defaults to <see cref="LiveActivityEvent.Update"/>.</summary>
    public LiveActivityEvent Event { get; init; } = LiveActivityEvent.Update;

    /// <summary>
    /// The new dynamic state, matching the widget's <c>ContentState</c> struct exactly — every
    /// non-optional Swift property must be present or ActivityKit drops the update. Optional (only) for
    /// <see cref="LiveActivityEvent.End"/>, where omitting it keeps the last state on screen.
    /// </summary>
    public IReadOnlyDictionary<string, LiveActivityValue>? ContentState { get; init; }

    /// <summary>
    /// The Swift type name of the <c>ActivityAttributes</c> struct (e.g. <c>"DeliveryAttributes"</c>).
    /// Required for <see cref="LiveActivityEvent.Start"/>, ignored otherwise.
    /// </summary>
    public string? AttributesType { get; init; }

    /// <summary>
    /// The static attributes for a push-started activity — the fields that never change for its lifetime.
    /// Required for <see cref="LiveActivityEvent.Start"/>, ignored otherwise.
    /// </summary>
    public IReadOnlyDictionary<string, LiveActivityValue>? Attributes { get; init; }

    /// <summary>
    /// The <c>aps.timestamp</c> (Unix seconds) ActivityKit uses to discard out-of-order updates. Defaults
    /// to the moment the payload is built. Set it explicitly only if you are replaying or scheduling.
    /// </summary>
    public DateTimeOffset? Timestamp { get; init; }

    /// <summary>
    /// When the state should be considered out of date, so the widget can render its stale view. Maps to
    /// <c>aps.stale-date</c>.
    /// </summary>
    public DateTimeOffset? StaleDate { get; init; }

    /// <summary>
    /// For <see cref="LiveActivityEvent.End"/>: when the ended activity should disappear from the Lock
    /// Screen. Past/omitted dismisses per the system default (up to four hours). Maps to <c>aps.dismissal-date</c>.
    /// </summary>
    public DateTimeOffset? DismissalDate { get; init; }

    /// <summary>
    /// Ranks this activity against the app's others when several are active. Maps to <c>aps.relevance-score</c>.
    /// </summary>
    public double? RelevanceScore { get; init; }

    /// <summary>
    /// Asks the system to issue an update token for the activity this push starts, so the server can then
    /// address it directly. Maps to <c>aps.input-push-token</c>; only meaningful with
    /// <see cref="LiveActivityEvent.Start"/> (iOS 17.2+).
    /// </summary>
    public bool RequestPushToken { get; init; }

    /// <summary>
    /// Binds the started activity to a broadcast channel so later channel pushes update it. Maps to
    /// <c>aps.input-push-channel</c>; only meaningful with <see cref="LiveActivityEvent.Start"/> (iOS 18+).
    /// See <see cref="Apns.ApnsBroadcastClient"/> for managing channels.
    /// </summary>
    public string? InputPushChannel { get; init; }

    /// <summary>
    /// Alerts the user about this update (Lock Screen banner, Apple Watch tap) rather than updating
    /// silently. The alert text comes from the notification's <see cref="PushNotification.Title"/>,
    /// <see cref="PushNotification.Message"/> and <see cref="PushNotification.Sound"/>.
    /// </summary>
    public bool Alert { get; init; }
}


/// <summary>
/// Builds the three Live Activity pushes. Thin sugar over
/// <c>new PushNotification { Apple = new() { LiveActivity = … } }</c> that makes the required-field rules
/// per event hard to get wrong.
/// </summary>
public static class LiveActivityPush
{
    /// <summary>
    /// A push-to-start notification (iOS 17.2+), addressed to a device's
    /// <see cref="PushTokenKind.LiveActivityStart"/> token.
    /// </summary>
    /// <param name="attributesType">The Swift <c>ActivityAttributes</c> type name.</param>
    /// <param name="attributes">The activity's static attributes.</param>
    /// <param name="contentState">The initial dynamic state.</param>
    /// <param name="alertTitle">Optional alert title. Supplying a title makes the start alerting.</param>
    /// <param name="alertBody">Optional alert body.</param>
    /// <param name="staleDate">Optional stale date for the initial state.</param>
    public static PushNotification Start(
        string attributesType,
        IReadOnlyDictionary<string, LiveActivityValue> attributes,
        IReadOnlyDictionary<string, LiveActivityValue> contentState,
        string? alertTitle = null,
        string? alertBody = null,
        DateTimeOffset? staleDate = null
    ) => new()
    {
        Title = alertTitle,
        Message = alertBody,
        Apple = new ApplePushOptions
        {
            LiveActivity = new LiveActivityPushOptions
            {
                Event = LiveActivityEvent.Start,
                AttributesType = attributesType,
                Attributes = attributes,
                ContentState = contentState,
                StaleDate = staleDate,
                Alert = alertTitle is not null || alertBody is not null
            }
        }
    };


    /// <summary>
    /// An update, addressed to a specific activity's <see cref="PushTokenKind.LiveActivityUpdate"/> token.
    /// </summary>
    /// <param name="contentState">The new dynamic state (the complete ContentState, not a delta).</param>
    /// <param name="alertTitle">Optional alert title. Supplying a title makes the update alerting.</param>
    /// <param name="alertBody">Optional alert body.</param>
    /// <param name="staleDate">Optional stale date for this state.</param>
    public static PushNotification Update(
        IReadOnlyDictionary<string, LiveActivityValue> contentState,
        string? alertTitle = null,
        string? alertBody = null,
        DateTimeOffset? staleDate = null
    ) => new()
    {
        Title = alertTitle,
        Message = alertBody,
        Apple = new ApplePushOptions
        {
            LiveActivity = new LiveActivityPushOptions
            {
                Event = LiveActivityEvent.Update,
                ContentState = contentState,
                StaleDate = staleDate,
                Alert = alertTitle is not null || alertBody is not null
            }
        }
    };


    /// <summary>
    /// Ends an activity, addressed to its <see cref="PushTokenKind.LiveActivityUpdate"/> token.
    /// </summary>
    /// <param name="contentState">Optional final state. Omit to leave the last state on screen.</param>
    /// <param name="dismissalDate">When the ended activity should disappear. Omit for the system default.</param>
    public static PushNotification End(
        IReadOnlyDictionary<string, LiveActivityValue>? contentState = null,
        DateTimeOffset? dismissalDate = null
    ) => new()
    {
        Apple = new ApplePushOptions
        {
            LiveActivity = new LiveActivityPushOptions
            {
                Event = LiveActivityEvent.End,
                ContentState = contentState,
                DismissalDate = dismissalDate
            }
        }
    };
}
