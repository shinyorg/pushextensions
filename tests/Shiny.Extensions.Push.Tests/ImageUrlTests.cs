using System.Text;
using System.Text.Json;
using Shiny.Extensions.Push;
using Shiny.Extensions.Push.Apns;
using Shiny.Extensions.Push.Fcm;
using Shiny.Extensions.Push.WebPush;
using Shiny.Extensions.Push.Wns;

namespace Shiny.Extensions.Push.Tests;


public class ImageUrlTests
{
    const string Url = "https://cdn.example.com/a.jpg";

    static JsonElement Parse(byte[] bytes) => JsonDocument.Parse(Encoding.UTF8.GetString(bytes)).RootElement;


    [Fact]
    public void Apns_ImageUrl_SetsMutableContentAndImageKey()
    {
        var root = Parse(ApnsPayloadBuilder.Build(new PushNotification { Title = "T", ImageUrl = Url }));

        Assert.Equal(1, root.GetProperty("aps").GetProperty("mutable-content").GetInt32());
        Assert.Equal(Url, root.GetProperty("image").GetString());
    }


    [Fact]
    public void Apns_ImageUrl_DoesNotOverwriteDataImage()
    {
        var root = Parse(ApnsPayloadBuilder.Build(new PushNotification
        {
            Title = "T",
            ImageUrl = Url,
            Data = new Dictionary<string, string> { ["image"] = "https://other.example.com/b.png" }
        }));
        Assert.Equal("https://other.example.com/b.png", root.GetProperty("image").GetString());
    }


    [Fact]
    public void Apns_SilentPush_IgnoresImageUrl()
    {
        var root = Parse(ApnsPayloadBuilder.Build(new PushNotification
        {
            ImageUrl = Url,
            Apple = new ApplePushOptions { ContentAvailable = true }
        }));
        Assert.False(root.GetProperty("aps").TryGetProperty("mutable-content", out _));
        Assert.False(root.TryGetProperty("image", out _));
    }


    [Fact]
    public void Apns_NoImage_NoMutableContent()
    {
        var root = Parse(ApnsPayloadBuilder.Build(new PushNotification { Title = "T" }));
        Assert.False(root.GetProperty("aps").TryGetProperty("mutable-content", out _));
    }


    [Fact]
    public void Fcm_ImageUrl_InNotificationAndAndroidBlocks()
    {
        var root = Parse(FcmPayloadBuilder.Build(new PushNotification { Title = "T", ImageUrl = Url }, "d")).GetProperty("message");

        Assert.Equal(Url, root.GetProperty("notification").GetProperty("image").GetString());
        Assert.Equal(Url, root.GetProperty("android").GetProperty("notification").GetProperty("image").GetString());
    }


    [Fact]
    public void Fcm_AndroidImageUrl_Overrides()
    {
        var root = Parse(FcmPayloadBuilder.Build(new PushNotification
        {
            Title = "T",
            ImageUrl = Url,
            Android = new AndroidPushOptions { ImageUrl = "https://cdn.example.com/android.jpg" }
        }, "d")).GetProperty("message");

        Assert.Equal("https://cdn.example.com/android.jpg", root.GetProperty("android").GetProperty("notification").GetProperty("image").GetString());
    }


    [Fact]
    public void WebPush_ImageUrl_IsWritten()
    {
        var root = Parse(WebPushPayloadBuilder.Build(new PushNotification { Title = "T", ImageUrl = Url }));
        Assert.Equal(Url, root.GetProperty("image").GetString());
    }


    [Fact]
    public void Wns_ImageUrl_IsHeroImage()
    {
        var c = WnsPayloadBuilder.Build(new PushNotification { Title = "T", ImageUrl = "https://cdn.example.com/a.jpg?x=1&y=2" });
        Assert.Contains("<image placement=\"hero\" src=\"https://cdn.example.com/a.jpg?x=1&amp;y=2\"/>", c.Body);
    }
}
