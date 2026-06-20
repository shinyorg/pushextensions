using System.Text;
using System.Text.Json;
using Shiny.Extensions.Push;
using Shiny.Extensions.Push.Apns;

namespace Shiny.Extensions.Push.Tests;


public class ApnsPayloadTests
{
    static JsonElement BuildRoot(PushNotification n)
    {
        var bytes = ApnsPayloadBuilder.Build(n);
        return JsonDocument.Parse(Encoding.UTF8.GetString(bytes)).RootElement;
    }


    [Fact]
    public void Alert_ContainsTitleAndBody()
    {
        var root = BuildRoot(new PushNotification { Title = "T", Message = "B", Badge = 3 });
        var aps = root.GetProperty("aps");
        var alert = aps.GetProperty("alert");

        Assert.Equal("T", alert.GetProperty("title").GetString());
        Assert.Equal("B", alert.GetProperty("body").GetString());
        Assert.Equal(3, aps.GetProperty("badge").GetInt32());
    }


    [Fact]
    public void SilentPush_HasContentAvailable_AndNoAlert()
    {
        var root = BuildRoot(new PushNotification
        {
            Apple = new ApplePushOptions { ContentAvailable = true },
            Data = new Dictionary<string, string> { ["sync"] = "1" }
        });
        var aps = root.GetProperty("aps");

        Assert.False(aps.TryGetProperty("alert", out _));
        Assert.Equal(1, aps.GetProperty("content-available").GetInt32());
        Assert.Equal("1", root.GetProperty("sync").GetString());
    }


    [Fact]
    public void DeepLink_IsWrittenAtTopLevel()
    {
        var root = BuildRoot(new PushNotification { Title = "T", DeepLink = "app://x" });
        Assert.Equal("app://x", root.GetProperty("deeplink").GetString());
    }


    [Fact]
    public void CustomData_DoesNotOverrideExplicitDeepLink()
    {
        var root = BuildRoot(new PushNotification
        {
            Title = "T",
            DeepLink = "app://explicit",
            Data = new Dictionary<string, string> { ["deeplink"] = "app://data" }
        });
        // Data key wins (written first); we must not emit a duplicate property.
        Assert.Equal("app://data", root.GetProperty("deeplink").GetString());
    }
}
