using System.Net;
using System.Security.Cryptography;
using Microsoft.Extensions.DependencyInjection;
using Shiny.Extensions.Push;
using Shiny.Extensions.Push.Apns;

namespace Shiny.Extensions.Push.Tests;


public class ApnsHttpTests
{
    static ApnsProvider Build(Func<HttpRequestMessage, HttpResponseMessage> responder)
    {
        var stub = new StubHttpHandler(responder);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddPushNotifications(b => b.AddApns(o =>
        {
            o.TeamId = "TEAM123456";
            o.KeyId = "KEY1234567";
            o.BundleId = "com.example.app";
            using var ec = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            o.PrivateKey = ec.ExportPkcs8PrivateKeyPem();
        }));
        services.AddHttpClient(ApnsProvider.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => stub);

        var sp = services.BuildServiceProvider();
        return sp.GetServices<IPushProvider>().OfType<ApnsProvider>().Single();
    }

    static DeviceRegistration Reg => new() { DeviceToken = "tok", Platform = DevicePlatform.iOS };


    [Fact]
    public async Task Success_CapturesApnsId()
    {
        var provider = Build(_ =>
        {
            var r = new HttpResponseMessage(HttpStatusCode.OK);
            r.Headers.TryAddWithoutValidation("apns-id", "ABC-123");
            return r;
        });

        var result = await provider.Send(new PushNotification { Title = "x" }, Reg);

        Assert.Equal(PushDeliveryStatus.Success, result.Status);
        Assert.Equal("ABC-123", result.ProviderMessageId);
    }


    [Fact]
    public async Task Gone_Unregistered_MapsToTokenExpired()
    {
        var provider = Build(_ => StubHttpHandler.Json(HttpStatusCode.Gone, """{"reason":"Unregistered"}"""));
        var result = await provider.Send(new PushNotification { Title = "x" }, Reg);
        Assert.Equal(PushDeliveryStatus.TokenExpired, result.Status);
    }


    [Fact]
    public async Task BadDeviceToken_MapsToInvalidToken()
    {
        var provider = Build(_ => StubHttpHandler.Json(HttpStatusCode.BadRequest, """{"reason":"BadDeviceToken"}"""));
        var result = await provider.Send(new PushNotification { Title = "x" }, Reg);
        Assert.Equal(PushDeliveryStatus.InvalidToken, result.Status);
    }


    [Fact]
    public async Task TooManyRequests_MapsToRateLimited()
    {
        var provider = Build(_ => StubHttpHandler.Json(HttpStatusCode.TooManyRequests, """{"reason":"TooManyRequests"}"""));
        var result = await provider.Send(new PushNotification { Title = "x" }, Reg);
        Assert.Equal(PushDeliveryStatus.RateLimited, result.Status);
    }
}
