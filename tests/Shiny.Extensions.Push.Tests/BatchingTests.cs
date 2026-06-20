using Microsoft.Extensions.DependencyInjection;
using Shiny.Extensions.Push;

namespace Shiny.Extensions.Push.Tests;


public class BatchingTests
{
    static (IPushManager manager, InMemoryPushRepository repo, FakeBatchProvider provider) Build(
        bool enableBatching = true,
        int maxBatchSize = 500,
        Action<IPushBuilder>? extra = null)
    {
        var provider = new FakeBatchProvider { MaxBatchSize = maxBatchSize };
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddPushNotifications(b =>
        {
            b.Services.AddSingleton<IPushProvider>(provider);
            b.Configure(o => o.EnableBatching = enableBatching);
            extra?.Invoke(b);
        });

        var sp = services.BuildServiceProvider();
        var manager = sp.GetRequiredService<IPushManager>();
        var repo = (InMemoryPushRepository)sp.GetRequiredService<IPushRepository>();
        return (manager, repo, provider);
    }

    static DeviceRegistration Reg(string token) =>
        new() { DeviceToken = token, Platform = DevicePlatform.Android };


    [Fact]
    public async Task Broadcast_SendsOneBatchForAllDevices()
    {
        var (manager, _, provider) = Build();
        for (var i = 0; i < 5; i++)
            await manager.RegisterDevice(Reg($"t{i}"));

        var result = await manager.Broadcast(new PushNotification { Title = "x" });

        Assert.Equal(5, result.Sent);
        Assert.Equal(0, provider.SingleSends);
        Assert.Equal([5], provider.BatchSizes.OrderBy(x => x));
    }


    [Fact]
    public async Task EnableBatchingFalse_FallsBackToPerDevice()
    {
        var (manager, _, provider) = Build(enableBatching: false);
        for (var i = 0; i < 3; i++)
            await manager.RegisterDevice(Reg($"t{i}"));

        var result = await manager.Broadcast(new PushNotification { Title = "x" });

        Assert.Equal(3, result.Sent);
        Assert.Equal(3, provider.SingleSends);
        Assert.Empty(provider.BatchSizes);
    }


    [Fact]
    public async Task DeadToken_InBatch_IsPruned()
    {
        var (manager, repo, provider) = Build();
        await manager.RegisterDevice(Reg("dead"));
        await manager.RegisterDevice(Reg("good"));
        provider.ExpireTokens.Add("dead");

        var result = await manager.Broadcast(new PushNotification { Title = "x" });

        Assert.Equal(1, result.Sent);
        Assert.Equal(1, result.TokensRemoved);
        Assert.Equal(1, repo.Count);
    }


    [Fact]
    public async Task MaxBatchSize_SplitsLargeAudience()
    {
        // MaxBatchSize 2 over 5 devices => batches of 2 + 2, with the lone remainder sent per-device.
        var (manager, _, provider) = Build(maxBatchSize: 2);
        for (var i = 0; i < 5; i++)
            await manager.RegisterDevice(Reg($"t{i}"));

        var result = await manager.Broadcast(new PushNotification { Title = "x" });

        Assert.Equal(5, result.Sent);
        Assert.Equal([2, 2], provider.BatchSizes.OrderBy(x => x));
        Assert.Equal(1, provider.SingleSends);
    }


    [Fact]
    public async Task PerDeviceMutatingInterceptor_FallsBackToSingleSends()
    {
        // SkipAndRewriteInterceptor replaces the notification per device (distinct instances), so no two
        // devices share an instance and batching degrades to per-device sends.
        var (manager, _, provider) = Build(extra: b => b.AddInterceptor<SkipAndRewriteInterceptor>());
        await manager.RegisterDevice(Reg("a"));
        await manager.RegisterDevice(Reg("b"));

        var result = await manager.Broadcast(new PushNotification { Title = "orig" });

        Assert.Equal(2, result.Sent);
        Assert.Equal(2, provider.SingleSends);
        Assert.Empty(provider.BatchSizes);
        Assert.All(provider.TitlesSeen, t => Assert.Equal("rewritten", t));
    }
}
