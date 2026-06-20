using System.Net;
using System.Security.Cryptography;
using Microsoft.Extensions.DependencyInjection;
using Shiny.Extensions.Push;
using Shiny.Extensions.Push.WebPush;

namespace Shiny.Extensions.Push.Tests;


public class WebPushProviderTests
{
    static WebPushProvider Build(Func<HttpRequestMessage, HttpResponseMessage> responder)
    {
        var stub = new StubHttpHandler(responder);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddPushNotifications(b => b.AddWebPush(o =>
        {
            var (pub, priv) = NewVapidKeys();
            o.PublicKey = pub;
            o.PrivateKey = priv;
            o.Subject = "mailto:test@example.com";
        }));
        services.AddHttpClient(WebPushProvider.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => stub);

        var sp = services.BuildServiceProvider();
        return sp.GetServices<IPushProvider>().OfType<WebPushProvider>().Single();
    }

    // A real (RFC 8291) subscription public key + auth secret so encryption succeeds.
    static DeviceRegistration Subscription => new()
    {
        DeviceToken = "https://push.example.com/sub/abc",
        Platform = DevicePlatform.WebBrowser,
        Data = new Dictionary<string, string>
        {
            ["p256dh"] = "BCVxsr7N_eNgVRqvHtD0zTZsEc6-VV-JvLexhqUzORcxaOzi6-AYWXvTBHm4bjyPjs7Vd8pZGH6SRpkNtoIAiw4",
            ["auth"] = "BTBZMqHH6r4Tts7J_aSIgg"
        }
    };


    [Fact]
    public async Task MissingKeys_MapsToInvalidToken()
    {
        var provider = Build(_ => new HttpResponseMessage(HttpStatusCode.Created));
        var reg = new DeviceRegistration { DeviceToken = "https://push.example.com/x", Platform = DevicePlatform.WebBrowser };

        var result = await provider.Send(new PushNotification { Title = "x" }, reg);
        Assert.Equal(PushDeliveryStatus.InvalidToken, result.Status);
    }


    [Fact]
    public async Task Success_EncryptsAndPostsAes128gcm()
    {
        HttpRequestMessage? sent = null;
        var provider = Build(req =>
        {
            sent = req;
            var r = new HttpResponseMessage(HttpStatusCode.Created);
            r.Headers.Location = new Uri("https://push.example.com/receipt/9");
            return r;
        });

        var result = await provider.Send(new PushNotification { Title = "Hi", Message = "there" }, Subscription);

        Assert.Equal(PushDeliveryStatus.Success, result.Status);
        Assert.Equal("https://push.example.com/receipt/9", result.ProviderMessageId);
        Assert.Equal("aes128gcm", sent!.Content!.Headers.ContentEncoding.Single());
        Assert.Contains("vapid t=", sent.Headers.GetValues("Authorization").Single());
    }


    [Fact]
    public async Task Gone_MapsToTokenExpired()
    {
        var provider = Build(_ => new HttpResponseMessage(HttpStatusCode.Gone));
        var result = await provider.Send(new PushNotification { Title = "x" }, Subscription);
        Assert.Equal(PushDeliveryStatus.TokenExpired, result.Status);
    }


    static (string pub, string priv) NewVapidKeys()
    {
        using var ec = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var p = ec.ExportParameters(true);
        var pub = new byte[65];
        pub[0] = 0x04;
        p.Q.X!.CopyTo(pub, 1);
        p.Q.Y!.CopyTo(pub, 33);
        return (System.Buffers.Text.Base64Url.EncodeToString(pub), System.Buffers.Text.Base64Url.EncodeToString(p.D!));
    }
}
