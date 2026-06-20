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


    static HttpResponseMessage Multipart(string boundary, params string[] parts)
    {
        var sb = new StringBuilder();
        foreach (var part in parts)
        {
            sb.Append("--").Append(boundary).Append("\r\n");
            sb.Append("Content-Type: application/http\r\n\r\n");
            sb.Append(part).Append("\r\n");
        }
        sb.Append("--").Append(boundary).Append("--\r\n");

        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(sb.ToString())
        };
        response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("multipart/mixed")
        {
            Parameters = { new System.Net.Http.Headers.NameValueHeaderValue("boundary", boundary) }
        };
        return response;
    }


    [Fact]
    public async Task SendBatch_ParsesPerDeviceOutcomes()
    {
        const string ok = "HTTP/1.1 200 OK\r\nContent-Type: application/json\r\n\r\n{\"name\":\"projects/proj/messages/0:1\"}";
        const string dead = "HTTP/1.1 404 Not Found\r\nContent-Type: application/json\r\n\r\n{\"error\":{\"status\":\"NOT_FOUND\",\"details\":[{\"errorCode\":\"UNREGISTERED\"}]}}";

        var provider = Build(req =>
        {
            Assert.EndsWith("/batch", req.RequestUri!.AbsolutePath);
            return Multipart("resp_bnd", ok, dead);
        });

        var regs = new[]
        {
            new DeviceRegistration { DeviceToken = "good", Platform = DevicePlatform.Android },
            new DeviceRegistration { DeviceToken = "dead", Platform = DevicePlatform.Android }
        };
        var results = await provider.SendBatch(new PushNotification { Title = "x" }, regs);

        Assert.Equal(2, results.Count);
        Assert.Equal(PushDeliveryStatus.Success, results[0].Status);
        Assert.Equal("projects/proj/messages/0:1", results[0].ProviderMessageId);
        Assert.Equal(PushDeliveryStatus.TokenExpired, results[1].Status);
    }


    [Fact]
    public async Task SendBatch_SingleDevice_UsesNormalEndpoint()
    {
        var hitBatch = false;
        var provider = Build(req =>
        {
            if (req.RequestUri!.AbsolutePath.EndsWith("/batch", StringComparison.Ordinal))
                hitBatch = true;
            return StubHttpHandler.Json(HttpStatusCode.OK, """{"name":"projects/proj/messages/0:9"}""");
        });

        var results = await provider.SendBatch(new PushNotification { Title = "x" }, [Reg]);

        Assert.False(hitBatch);
        Assert.Single(results);
        Assert.Equal(PushDeliveryStatus.Success, results[0].Status);
    }


    [Fact]
    public async Task SendBatch_FewerPartsThanDevices_FailsTheRemainder()
    {
        const string ok = "HTTP/1.1 200 OK\r\nContent-Type: application/json\r\n\r\n{\"name\":\"projects/proj/messages/0:1\"}";
        var provider = Build(_ => Multipart("resp_bnd", ok)); // only one part for two devices

        var regs = new[]
        {
            new DeviceRegistration { DeviceToken = "a", Platform = DevicePlatform.Android },
            new DeviceRegistration { DeviceToken = "b", Platform = DevicePlatform.Android }
        };
        var results = await provider.SendBatch(new PushNotification { Title = "x" }, regs);

        Assert.Equal(2, results.Count);
        Assert.Equal(PushDeliveryStatus.Success, results[0].Status);
        Assert.Equal(PushDeliveryStatus.Error, results[1].Status);
    }
}
