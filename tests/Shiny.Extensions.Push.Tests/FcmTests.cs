using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Shiny.Extensions.Push;
using Shiny.Extensions.Push.Fcm;

namespace Shiny.Extensions.Push.Tests;


public class FcmTests
{
    static string ServiceAccountJson()
    {
        using var rsa = RSA.Create(2048);
        return JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["type"] = "service_account",
            ["project_id"] = "proj",
            ["client_email"] = "x@proj.iam.gserviceaccount.com",
            ["private_key"] = rsa.ExportPkcs8PrivateKeyPem(),
            ["token_uri"] = "https://oauth2.googleapis.com/token"
        });
    }

    static FcmProvider Build(Func<HttpRequestMessage, HttpResponseMessage> sendResponder)
    {
        var stub = new StubHttpHandler(req =>
            req.RequestUri!.Host.Contains("oauth2")
                ? StubHttpHandler.Json(HttpStatusCode.OK, """{"access_token":"fake-token","expires_in":3600}""")
                : sendResponder(req));

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddPushNotifications(b => b.AddFcm(o => o.ServiceAccountJson = ServiceAccountJson()));
        services.AddHttpClient(FcmProvider.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => stub);

        var sp = services.BuildServiceProvider();
        return sp.GetServices<IPushProvider>().OfType<FcmProvider>().Single();
    }

    static DeviceRegistration Reg => new() { DeviceToken = "tok", Platform = DevicePlatform.Android };


    [Fact]
    public void Payload_HasTokenNotificationAndAndroidBlock()
    {
        var bytes = FcmPayloadBuilder.Build(
            new PushNotification { Title = "T", Message = "B", Priority = PushPriority.High, Android = new AndroidPushOptions { ChannelId = "chat" } },
            "device-1");
        var root = JsonDocument.Parse(Encoding.UTF8.GetString(bytes)).RootElement.GetProperty("message");

        Assert.Equal("device-1", root.GetProperty("token").GetString());
        Assert.Equal("T", root.GetProperty("notification").GetProperty("title").GetString());
        Assert.Equal("HIGH", root.GetProperty("android").GetProperty("priority").GetString());
        Assert.Equal("chat", root.GetProperty("android").GetProperty("notification").GetProperty("channel_id").GetString());
    }


    [Fact]
    public void ParseErrorCode_ReadsFcmErrorDetails()
    {
        const string body = """{"error":{"status":"NOT_FOUND","details":[{"@type":"type.googleapis.com/google.firebase.fcm.v1.FcmError","errorCode":"UNREGISTERED"}]}}""";
        Assert.Equal("UNREGISTERED", FcmProvider.ParseErrorCode(body));
    }


    [Fact]
    public async Task Send_Success_CapturesMessageName()
    {
        var provider = Build(_ => StubHttpHandler.Json(HttpStatusCode.OK, """{"name":"projects/proj/messages/0:123"}"""));
        var result = await provider.Send(new PushNotification { Title = "x" }, Reg);

        Assert.Equal(PushDeliveryStatus.Success, result.Status);
        Assert.Equal("projects/proj/messages/0:123", result.ProviderMessageId);
    }


    [Fact]
    public async Task Send_Unregistered_MapsToTokenExpired()
    {
        var provider = Build(_ => StubHttpHandler.Json(
            HttpStatusCode.NotFound,
            """{"error":{"status":"NOT_FOUND","details":[{"errorCode":"UNREGISTERED"}]}}"""));
        var result = await provider.Send(new PushNotification { Title = "x" }, Reg);
        Assert.Equal(PushDeliveryStatus.TokenExpired, result.Status);
    }
}
