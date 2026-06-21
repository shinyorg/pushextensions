using System.Text.Json;
using Shiny.Extensions.Push;

namespace Shiny.Extensions.Push.Fcm;


/// <summary>
/// Builds the FCM HTTP v1 <c>message</c> payload by hand with <see cref="Utf8JsonWriter"/> (AOT-safe).
/// </summary>
static class FcmPayloadBuilder
{
    public static byte[] Build(PushNotification n, string deviceToken)
    {
        var android = n.Android;
        var hasAlert = n.Title is not null || n.Message is not null;

        var buffer = new System.Buffers.ArrayBufferWriter<byte>(256);
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            w.WritePropertyName("message");
            w.WriteStartObject();

            w.WriteString("token", deviceToken);

            if (hasAlert)
            {
                w.WritePropertyName("notification");
                w.WriteStartObject();
                if (n.Title is not null) w.WriteString("title", n.Title);
                if (n.Message is not null) w.WriteString("body", n.Message);
                w.WriteEndObject();
            }

            // FCM data values must be strings.
            if (n.Data.Count > 0 || n.DeepLink is not null)
            {
                w.WritePropertyName("data");
                w.WriteStartObject();
                foreach (var kvp in n.Data)
                    w.WriteString(kvp.Key, kvp.Value);
                if (n.DeepLink is { } link && !n.Data.ContainsKey("deeplink"))
                    w.WriteString("deeplink", link);
                w.WriteEndObject();
            }

            // android block
            w.WritePropertyName("android");
            w.WriteStartObject();
            w.WriteString("priority", n.Priority == PushPriority.High ? "HIGH" : "NORMAL");
            if (n.TimeToLive is { } ttl)
                w.WriteString("ttl", $"{(long)ttl.TotalSeconds}s");
            if (n.CollapseId is { } collapse)
                w.WriteString("collapse_key", collapse);

            var hasAndroidNotif = hasAlert || android is not null;
            if (hasAndroidNotif)
            {
                w.WritePropertyName("notification");
                w.WriteStartObject();
                if (n.Title is not null) w.WriteString("title", n.Title);
                if (n.Message is not null) w.WriteString("body", n.Message);
                if (n.Sound is { } sound) w.WriteString("sound", sound);
                if (android?.ChannelId is { } ch) w.WriteString("channel_id", ch);
                if (android?.Icon is { } icon) w.WriteString("icon", icon);
                if (android?.Color is { } color) w.WriteString("color", color);
                if (android?.ImageUrl is { } image) w.WriteString("image", image);
                if (n.DeepLink is { } link) w.WriteString("click_action", link);
                w.WriteEndObject();
            }
            w.WriteEndObject(); // android

            w.WriteEndObject(); // message
            w.WriteEndObject();
        }
        return buffer.WrittenSpan.ToArray();
    }
}
