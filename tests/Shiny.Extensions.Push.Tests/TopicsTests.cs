using Microsoft.Extensions.DependencyInjection;
using Shiny.Extensions.Push;

namespace Shiny.Extensions.Push.Tests;


public class TopicsTests
{
    static (IPushManager manager, TestProvider provider) Build()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        var provider = new TestProvider();
        services.AddPushNotifications(b => b.Services.AddSingleton<IPushProvider>(provider));
        var sp = services.BuildServiceProvider();
        return (sp.GetRequiredService<IPushManager>(), provider);
    }

    static DeviceRegistration Reg(string token) => new() { DeviceToken = token, Platform = DevicePlatform.iOS };


    [Fact]
    public async Task SendToTopic_OnlyHitsSubscribers()
    {
        var (manager, provider) = Build();
        await manager.RegisterDevice(Reg("a"));
        await manager.RegisterDevice(Reg("b"));
        await manager.SubscribeToTopic("a", DevicePlatform.iOS, "news");

        var result = await manager.SendToTopic("news", new PushNotification { Title = "x" });

        Assert.Equal(1, result.Sent);
        Assert.Equal(["a"], provider.Sent);
    }


    [Fact]
    public async Task Unsubscribe_RemovesFromTopic()
    {
        var (manager, provider) = Build();
        await manager.RegisterDevice(Reg("a"));
        await manager.SubscribeToTopic("a", DevicePlatform.iOS, "news");
        await manager.UnsubscribeFromTopic("a", DevicePlatform.iOS, "news");

        var result = await manager.SendToTopic("news", new PushNotification { Title = "x" });

        Assert.Equal(0, result.Total);
        Assert.Empty(provider.Sent);
    }


    [Fact]
    public async Task Subscribe_IsIdempotent()
    {
        var (manager, provider) = Build();
        await manager.RegisterDevice(Reg("a"));
        await manager.SubscribeToTopic("a", DevicePlatform.iOS, "news");
        await manager.SubscribeToTopic("a", DevicePlatform.iOS, "news");

        var result = await manager.SendToTopic("news", new PushNotification { Title = "x" });
        Assert.Equal(1, result.Total);   // not duplicated
    }
}
