using System.Text.Json;
using Shiny.Extensions.Push;

namespace Shiny.Extensions.Push.WebPush;


/// <summary>
/// Builds the plaintext JSON payload delivered to the browser service worker (before encryption).
/// AOT-safe via <see cref="Utf8JsonWriter"/>.
/// </summary>
static class WebPushPayloadBuilder
{
    public static byte[] Build(PushNotification n)
    {
        var buffer = new System.Buffers.ArrayBufferWriter<byte>(128);
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            if (n.Title is not null) w.WriteString("title", n.Title);
            if (n.Message is not null) w.WriteString("body", n.Message);
            if (n.DeepLink is not null) w.WriteString("deeplink", n.DeepLink);
            if (n.WebPush?.Icon is { } icon) w.WriteString("icon", icon);

            if (n.Data.Count > 0)
            {
                w.WritePropertyName("data");
                w.WriteStartObject();
                foreach (var kvp in n.Data)
                    w.WriteString(kvp.Key, kvp.Value);
                w.WriteEndObject();
            }
            w.WriteEndObject();
        }
        return buffer.WrittenSpan.ToArray();
    }
}
