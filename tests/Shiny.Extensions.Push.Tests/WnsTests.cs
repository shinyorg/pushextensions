using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Shiny.Extensions.Push;
using Shiny.Extensions.Push.Wns;

namespace Shiny.Extensions.Push.Tests;


public class WnsTests
{
    static WnsProvider Build(Func<HttpRequestMessage, HttpResponseMessage> sendResponder, string key = "")
    {
        var stub = new StubHttpHandler(req =>
            req.RequestUri!.Host.Contains("login.microsoftonline.com")
                ? StubHttpHandler.Json(HttpStatusCode.OK, """{"access_token":"fake-token","expires_in":3600}""")
                : sendResponder(req));

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddPushNotifications(b => b.AddWns(key, o =>
        {
            o.TenantId = "tenant";
            o.ClientId = "client";
            o.ClientSecret = "secret";
        }));
        services.AddHttpClient(WnsProvider.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => stub);

        var sp = services.BuildServiceProvider();
        return sp.GetServices<IPushProvider>().OfType<WnsProvider>().Single();
    }

    static DeviceRegistration Reg => new() { DeviceToken = "https://db5.notify.windows.com/?token=abc", Platform = DevicePlatform.Windows };

    static HttpResponseMessage Wns(HttpStatusCode status, string? msgId = null, string? notificationStatus = null)
    {
        var response = new HttpResponseMessage(status);
        if (msgId is not null)
            response.Headers.TryAddWithoutValidation("X-WNS-Msg-ID", msgId);
        if (notificationStatus is not null)
            response.Headers.TryAddWithoutValidation("X-WNS-NotificationStatus", notificationStatus);
        return response;
    }


    [Fact]
    public void Provider_ClaimsWindows()
    {
        var provider = Build(_ => Wns(HttpStatusCode.OK));
        Assert.True(provider.CanDeliver(Reg));
        Assert.False(provider.CanDeliver(new DeviceRegistration { DeviceToken = "t", Platform = DevicePlatform.Android }));
        Assert.Equal("wns", provider.Identifier);
    }


    [Fact]
    public void Payload_BuildsToastGenericXml()
    {
        var c = WnsPayloadBuilder.Build(new PushNotification { Title = "Hello", Message = "World", DeepLink = "app://x" });

        Assert.Equal("wns/toast", c.WnsType);
        Assert.Equal("text/xml", c.ContentType);
        Assert.Contains("template=\"ToastGeneric\"", c.Body);
        Assert.Contains("<text>Hello</text>", c.Body);
        Assert.Contains("<text>World</text>", c.Body);
        Assert.Contains("launch=\"app://x\"", c.Body);
    }


    [Fact]
    public void Payload_EscapesXml()
    {
        var c = WnsPayloadBuilder.Build(new PushNotification { Title = "A & B <c>", Message = "\"q\"" });
        Assert.Contains("<text>A &amp; B &lt;c&gt;</text>", c.Body);
        Assert.Contains("<text>&quot;q&quot;</text>", c.Body);
    }


    [Fact]
    public void Payload_RawSerializesData()
    {
        var c = WnsPayloadBuilder.Build(new PushNotification
        {
            Windows = new WindowsPushOptions { Type = WnsNotificationType.Raw },
            Data = new Dictionary<string, string> { ["k"] = "v" }
        });

        Assert.Equal("wns/raw", c.WnsType);
        Assert.Equal("application/octet-stream", c.ContentType);
        Assert.Contains("\"k\":\"v\"", c.Body);
    }


    [Fact]
    public void Payload_VerbatimOverride_IsUsed()
    {
        const string tile = "<tile><visual><binding template=\"TileMedium\"/></visual></tile>";
        var c = WnsPayloadBuilder.Build(new PushNotification
        {
            Windows = new WindowsPushOptions { Type = WnsNotificationType.Tile, Payload = tile }
        });
        Assert.Equal("wns/tile", c.WnsType);
        Assert.Equal(tile, c.Body);
    }


    [Fact]
    public async Task Send_Success_CapturesMessageId_AndSetsHeaders()
    {
        HttpRequestMessage? captured = null;
        var provider = Build(req => { captured = req; return Wns(HttpStatusCode.OK, msgId: "MSG-1"); });

        var result = await provider.Send(new PushNotification { Title = "x", Priority = PushPriority.High }, Reg);

        Assert.Equal(PushDeliveryStatus.Success, result.Status);
        Assert.Equal("MSG-1", result.ProviderMessageId);
        Assert.Equal(Reg.DeviceToken, captured!.RequestUri!.ToString());
        Assert.Equal("wns/toast", captured.Headers.GetValues("X-WNS-Type").Single());
        Assert.Equal("1", captured.Headers.GetValues("X-WNS-PRIORITY").Single());
        Assert.Equal("Bearer", captured.Headers.Authorization!.Scheme);
    }


    [Fact]
    public async Task Send_Gone_MapsToTokenExpired()
    {
        var provider = Build(_ => Wns(HttpStatusCode.Gone));
        var result = await provider.Send(new PushNotification { Title = "x" }, Reg);
        Assert.Equal(PushDeliveryStatus.TokenExpired, result.Status);
    }


    [Fact]
    public async Task Send_NotFound_MapsToInvalidToken()
    {
        var provider = Build(_ => Wns(HttpStatusCode.NotFound));
        var result = await provider.Send(new PushNotification { Title = "x" }, Reg);
        Assert.Equal(PushDeliveryStatus.InvalidToken, result.Status);
    }


    [Fact]
    public async Task Send_TooManyRequests_MapsToRateLimited()
    {
        var provider = Build(_ => Wns(HttpStatusCode.TooManyRequests));
        var result = await provider.Send(new PushNotification { Title = "x" }, Reg);
        Assert.Equal(PushDeliveryStatus.RateLimited, result.Status);
    }


    [Fact]
    public async Task Send_DroppedStatusOn200_MapsToError()
    {
        var provider = Build(_ => Wns(HttpStatusCode.OK, notificationStatus: "dropped"));
        var result = await provider.Send(new PushNotification { Title = "x" }, Reg);
        Assert.Equal(PushDeliveryStatus.Error, result.Status);
        Assert.Equal("dropped", result.Reason);
    }


    [Fact]
    public async Task KeyedProvider_RoutesByAppId_AndIdentifierShowsKey()
    {
        var provider = Build(_ => Wns(HttpStatusCode.OK), key: "store");
        Assert.Equal("wns:store", provider.Identifier);
        Assert.True(provider.CanDeliver(new DeviceRegistration { DeviceToken = "u", Platform = DevicePlatform.Windows, AppId = "store" }));
        Assert.False(provider.CanDeliver(new DeviceRegistration { DeviceToken = "u", Platform = DevicePlatform.Windows, AppId = "other" }));
    }
}
