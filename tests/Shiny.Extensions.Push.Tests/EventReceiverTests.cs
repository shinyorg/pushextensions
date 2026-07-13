using Microsoft.Extensions.DependencyInjection;
using Shiny.Extensions.Push;
using Shiny.Extensions.Push.Infrastructure;

namespace Shiny.Extensions.Push.Tests;


public class EventReceiverTests
{
    static (IPushManager manager, TestProvider provider, RecordingEventReceiver receiver) Build(
        Action<IPushBuilder>? extra = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddPushNotifications(b =>
        {
            b.AddProvider<TestProvider>();
            b.AddEventReceiver<RecordingEventReceiver>();
            extra?.Invoke(b);
        });

        var sp = services.BuildServiceProvider();
        var provider = sp.GetServices<IPushProvider>().OfType<TestProvider>().Single();
        var receiver = sp.GetServices<IPushEventReceiver>().OfType<RecordingEventReceiver>().Single();
        var manager = sp.GetRequiredService<IPushManager>();
        return (manager, provider, receiver);
    }


    static DeviceRegistration Reg(string token) =>
        new() { DeviceToken = token, Platform = DevicePlatform.iOS };


    [Fact]
    public async Task BatchLifecycle_FiresOnceEachWithFinalResult()
    {
        var (manager, _, receiver) = Build();
        await manager.RegisterDevice(Reg("a"));
        await manager.RegisterDevice(Reg("b"));

        var result = await manager.Broadcast(new PushNotification { Title = "hi" });

        Assert.Equal(1, receiver.BatchStarted);
        Assert.Equal(1, receiver.BatchFinished);
        Assert.Equal(result.BatchId, receiver.BatchIds.Single());
        Assert.Same(result, receiver.FinishedResult);
        Assert.Equal(2, receiver.FinishedResult!.Sent);
    }


    [Fact]
    public async Task OnSent_FiresPerSuccessfulDevice()
    {
        var (manager, _, receiver) = Build();
        await manager.RegisterDevice(Reg("a"));
        await manager.RegisterDevice(Reg("b"));

        await manager.Broadcast(new PushNotification { Title = "hi" });

        Assert.Equal(2, receiver.SentTokens.Count);
        Assert.Contains("a", receiver.SentTokens);
        Assert.Contains("b", receiver.SentTokens);
        Assert.Empty(receiver.Failed);
    }


    [Fact]
    public async Task OnFailed_FiresForNormalizedFailure_NotJustExceptions()
    {
        var (manager, provider, receiver) = Build();
        await manager.RegisterDevice(Reg("dead"));
        await manager.RegisterDevice(Reg("good"));
        provider.ExpireTokens.Add("dead");

        await manager.Broadcast(new PushNotification { Title = "hi" });

        var failure = Assert.Single(receiver.Failed);
        Assert.Equal("dead", failure.Token);
        Assert.Equal(PushDeliveryStatus.TokenExpired, failure.Status);
        Assert.Equal(["good"], receiver.SentTokens);
    }


    [Fact]
    public async Task ThrowingReceiver_NeverBreaksTheBatch()
    {
        var (manager, _, receiver) = Build(b => b.AddEventReceiver<ThrowingEventReceiver>());
        await manager.RegisterDevice(Reg("a"));

        var result = await manager.Broadcast(new PushNotification { Title = "hi" });

        // Delivery still succeeded and the well-behaved receiver still saw every event.
        Assert.Equal(1, result.Sent);
        Assert.Equal(1, receiver.BatchStarted);
        Assert.Equal(1, receiver.BatchFinished);
        Assert.Equal(["a"], receiver.SentTokens);
    }


    [Fact]
    public async Task NoReceivers_IsFine()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddPushNotifications(b => b.AddProvider<TestProvider>());
        var sp = services.BuildServiceProvider();
        var manager = sp.GetRequiredService<IPushManager>();

        await manager.RegisterDevice(Reg("a"));
        var result = await manager.Broadcast(new PushNotification { Title = "hi" });

        Assert.Equal(1, result.Sent);
    }
}
