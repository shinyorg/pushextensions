using System.Text.Json;
using Shiny.Extensions.Push;

namespace Shiny.Extensions.Push.Apns;


/// <summary>
/// Builds the APNs JSON payload by hand with <see cref="Utf8JsonWriter"/>. Done manually (rather than
/// via serializer reflection) so the library stays fully AOT/trim-safe and we keep exact control over
/// the <c>aps</c> dictionary shape.
/// </summary>
static class ApnsPayloadBuilder
{
    public static byte[] Build(PushNotification n)
    {
        var apple = n.Apple;
        var silent = apple?.ContentAvailable == true && n.Title is null && n.Message is null;

        var buffer = new System.Buffers.ArrayBufferWriter<byte>(256);
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            w.WritePropertyName("aps");
            w.WriteStartObject();

            // A Live Activity push replaces the entire aps body — no badge/sound/category/thread-id.
            if (apple?.LiveActivity is { } live)
            {
                WriteLiveActivity(w, n, live);
            }
            else
            {
                if (!silent && (n.Title is not null || n.Message is not null || apple?.Subtitle is not null))
                {
                    w.WritePropertyName("alert");
                    w.WriteStartObject();
                    if (n.Title is not null) w.WriteString("title", n.Title);
                    if (apple?.Subtitle is not null) w.WriteString("subtitle", apple.Subtitle);
                    if (n.Message is not null) w.WriteString("body", n.Message);
                    w.WriteEndObject();
                }

                if (n.Badge is { } badge)
                    w.WriteNumber("badge", badge);

                if (n.Sound is { } sound)
                    w.WriteString("sound", sound);

                if (apple?.ContentAvailable == true)
                    w.WriteNumber("content-available", 1);

                if (apple?.MutableContent == true)
                    w.WriteNumber("mutable-content", 1);

                if (apple?.Category is { } category)
                    w.WriteString("category", category);

                if (apple?.ThreadId is { } threadId)
                    w.WriteString("thread-id", threadId);
            }

            w.WriteEndObject(); // aps

            // Custom data goes at the top level of the payload (string values only — AOT-safe).
            foreach (var kvp in n.Data)
                w.WriteString(kvp.Key, kvp.Value);

            if (n.DeepLink is { } link && !n.Data.ContainsKey("deeplink"))
                w.WriteString("deeplink", link);

            w.WriteEndObject();
        }
        return buffer.WrittenSpan.ToArray();
    }


    // https://developer.apple.com/documentation/activitykit/starting-and-updating-live-activities-with-activitykit-push-notifications
    static void WriteLiveActivity(Utf8JsonWriter w, PushNotification n, LiveActivityPushOptions live)
    {
        var isStart = live.Event == LiveActivityEvent.Start;

        if (isStart && string.IsNullOrWhiteSpace(live.AttributesType))
            throw new InvalidOperationException("A Live Activity 'start' push requires LiveActivity.AttributesType (the Swift ActivityAttributes type name)");

        if (isStart && live.Attributes is null)
            throw new InvalidOperationException("A Live Activity 'start' push requires LiveActivity.Attributes");

        if (live.ContentState is null && live.Event != LiveActivityEvent.End)
            throw new InvalidOperationException("A Live Activity 'start'/'update' push requires LiveActivity.ContentState");

        // Required. ActivityKit uses it to drop updates that arrive out of order.
        w.WriteNumber("timestamp", (live.Timestamp ?? DateTimeOffset.UtcNow).ToUnixTimeSeconds());
        w.WriteString("event", live.Event switch
        {
            LiveActivityEvent.Start => "start",
            LiveActivityEvent.End => "end",
            _ => "update"
        });

        if (isStart)
        {
            w.WriteString("attributes-type", live.AttributesType!);
            w.WritePropertyName("attributes");
            LiveActivityValue.WriteObject(w, live.Attributes!);
        }

        if (live.ContentState is { } state)
        {
            w.WritePropertyName("content-state");
            LiveActivityValue.WriteObject(w, state);
        }

        if (live.StaleDate is { } stale)
            w.WriteNumber("stale-date", stale.ToUnixTimeSeconds());

        if (live.Event == LiveActivityEvent.End && live.DismissalDate is { } dismissal)
            w.WriteNumber("dismissal-date", dismissal.ToUnixTimeSeconds());

        if (live.RelevanceScore is { } relevance)
            w.WriteNumber("relevance-score", relevance);

        if (isStart && live.RequestPushToken)
            w.WriteNumber("input-push-token", 1);

        if (isStart && live.InputPushChannel is { } channel)
            w.WriteString("input-push-channel", channel);

        if (live.Alert && (n.Title is not null || n.Message is not null))
        {
            w.WritePropertyName("alert");
            w.WriteStartObject();
            if (n.Title is not null) w.WriteString("title", n.Title);
            if (n.Message is not null) w.WriteString("body", n.Message);
            if (n.Sound is { } sound) w.WriteString("sound", sound);
            w.WriteEndObject();
        }
    }
}
