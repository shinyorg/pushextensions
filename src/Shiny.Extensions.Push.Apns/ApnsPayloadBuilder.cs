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
}
