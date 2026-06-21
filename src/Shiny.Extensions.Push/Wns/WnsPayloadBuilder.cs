using System.Text;
using System.Text.Json;
using Shiny.Extensions.Push;

namespace Shiny.Extensions.Push.Wns;


/// <summary>
/// Builds the WNS request body from a <see cref="PushNotification"/>. Toasts are emitted as a
/// <c>ToastGeneric</c> XML payload; tile/badge expect a verbatim <see cref="WindowsPushOptions.Payload"/>;
/// raw sends the app data as JSON. AOT-safe (hand-built XML + <see cref="Utf8JsonWriter"/>).
/// </summary>
static class WnsPayloadBuilder
{
    /// <summary>The built body plus the matching <c>X-WNS-Type</c> header value and HTTP content type.</summary>
    public readonly record struct WnsContent(string Body, string WnsType, string ContentType);


    public static WnsContent Build(PushNotification n)
    {
        var type = n.Windows?.Type ?? WnsNotificationType.Toast;

        return type switch
        {
            WnsNotificationType.Raw => new WnsContent(n.Windows?.Payload ?? BuildRawJson(n), "wns/raw", "application/octet-stream"),
            WnsNotificationType.Badge => new WnsContent(n.Windows?.Payload ?? $"<badge value=\"{n.Badge ?? 0}\"/>", "wns/badge", "text/xml"),
            WnsNotificationType.Tile => new WnsContent(n.Windows?.Payload ?? BuildToast(n), "wns/tile", "text/xml"),
            _ => new WnsContent(n.Windows?.Payload ?? BuildToast(n), "wns/toast", "text/xml")
        };
    }


    static string BuildToast(PushNotification n)
    {
        var launch = n.Windows?.Launch ?? n.DeepLink;

        var sb = new StringBuilder("<toast");
        if (!string.IsNullOrEmpty(launch))
            sb.Append(" launch=\"").Append(Escape(launch)).Append('"');
        sb.Append("><visual><binding template=\"ToastGeneric\">");

        if (n.Title is { } title)
            sb.Append("<text>").Append(Escape(title)).Append("</text>");
        if (n.Message is { } message)
            sb.Append("<text>").Append(Escape(message)).Append("</text>");

        sb.Append("</binding></visual>");

        // A "silent"/"none" sound suppresses the system sound; otherwise WNS plays its default.
        if (n.Sound is "silent" or "none" or "")
            sb.Append("<audio silent=\"true\"/>");

        sb.Append("</toast>");
        return sb.ToString();
    }


    static string BuildRawJson(PushNotification n)
    {
        var buffer = new System.Buffers.ArrayBufferWriter<byte>(128);
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            if (n.Title is { } title) w.WriteString("title", title);
            if (n.Message is { } message) w.WriteString("message", message);
            if (n.DeepLink is { } link) w.WriteString("deeplink", link);
            foreach (var kvp in n.Data)
                w.WriteString(kvp.Key, kvp.Value);
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }


    // Minimal XML escaping for the handful of attribute/text values we emit (no System.Xml dependency).
    static string Escape(string value) => value
        .Replace("&", "&amp;")
        .Replace("<", "&lt;")
        .Replace(">", "&gt;")
        .Replace("\"", "&quot;");
}
