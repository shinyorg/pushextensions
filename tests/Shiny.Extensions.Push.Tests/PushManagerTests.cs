using Microsoft.Extensions.DependencyInjection;
using Shiny.Extensions.Push;
using Shiny.Extensions.Push.Infrastructure;

namespace Shiny.Extensions.Push.Tests;


public class PushManagerTests
{
    static (IPushManager manager, InMemoryPushRepository repo, TestProvider provider) Build(
        Action<IPushBuilder>? extra = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();

        TestProvider? captured = null;
        services.AddPushNotifications(b =>
        {
            b.AddProvider<TestProvider>();
            extra?.Invoke(b);
        });
        // Resolve the concrete provider instance the container built.
        var sp = services.BuildServiceProvider();
        captured = sp.GetServices<IPushProvider>().OfType<TestProvider>().Single();

        var manager = sp.GetRequiredService<IPushManager>();
        var repo = (InMemoryPushRepository)sp.GetRequiredService<IPushRepository>();
        return (manager, repo, captured);
    }


    static DeviceRegistration Reg(string token, DevicePlatform platform = DevicePlatform.iOS, string? user = null, params string[] tags) =>
        new() { DeviceToken = token, Platform = platform, UserIdentifier = user, Tags = tags };


    [Fact]
    public async Task SendToUser_TargetsOnlyThatUsersDevices()
    {
        var (manager, repo, provider) = Build();
        await manager.RegisterDevice(Reg("a", user: "u1"));
        await manager.RegisterDevice(Reg("b", user: "u1"));
        await manager.RegisterDevice(Reg("c", user: "u2"));

        var result = await manager.SendToUser("u1", new PushNotification { Title = "hi" });

        Assert.Equal(2, result.Sent);
        Assert.Equal(2, provider.Sent.Count);
        Assert.DoesNotContain("c", provider.Sent);
    }


    [Fact]
    public async Task SendToTags_All_RequiresEveryTag()
    {
        var (manager, _, provider) = Build();
        await manager.RegisterDevice(Reg("a", tags: ["beta", "sports"]));
        await manager.RegisterDevice(Reg("b", tags: ["beta"]));

        var result = await manager.SendToTags(["beta", "sports"], new PushNotification { Title = "x" }, TagMatch.All);

        Assert.Equal(1, result.Sent);
        Assert.Equal(["a"], provider.Sent);
    }


    [Fact]
    public async Task Broadcast_HitsEveryRegistration()
    {
        var (manager, _, provider) = Build();
        await manager.RegisterDevice(Reg("a"));
        await manager.RegisterDevice(Reg("b"));

        var result = await manager.Broadcast(new PushNotification { Title = "x" });

        Assert.Equal(2, result.Total);
        Assert.Equal(2, result.Sent);
    }


    [Fact]
    public async Task DeadToken_IsPrunedAutomatically()
    {
        var (manager, repo, provider) = Build();
        await manager.RegisterDevice(Reg("dead"));
        await manager.RegisterDevice(Reg("good"));
        provider.ExpireTokens.Add("dead");

        var result = await manager.Broadcast(new PushNotification { Title = "x" });

        Assert.Equal(1, result.TokensRemoved);
        Assert.Equal(1, result.Sent);
        Assert.Equal(1, repo.Count); // "dead" removed
    }


    [Fact]
    public async Task UpdatedToken_IsRotatedInRepository()
    {
        var (manager, repo, provider) = Build();
        await manager.RegisterDevice(Reg("old"));
        provider.RotateTokens["old"] = "new";

        await manager.Broadcast(new PushNotification { Title = "x" });

        var regs = await repo.GetRegistrations(PushFilter.Broadcast);
        Assert.Equal("new", regs.Single().DeviceToken);
    }


    [Fact]
    public async Task NoProvider_ForPlatform_ReportsNoProvider()
    {
        // TestProvider only handles iOS.
        var (manager, _, _) = Build();
        await manager.RegisterDevice(Reg("w", DevicePlatform.Windows));

        var result = await manager.Broadcast(new PushNotification { Title = "x" });

        Assert.Equal(1, result.Failed);
        Assert.Equal(PushDeliveryStatus.NoProvider, result.Results.Single().Status);
    }


    [Fact]
    public async Task Interceptor_CanSkipAndMutate()
    {
        var (manager, _, provider) = Build(b => b.AddInterceptor<SkipAndRewriteInterceptor>());
        await manager.RegisterDevice(Reg("skipme"));
        await manager.RegisterDevice(Reg("keep"));

        var result = await manager.Broadcast(new PushNotification { Title = "orig" });

        Assert.Equal(1, result.Skipped);
        Assert.Equal(1, result.Sent);
        Assert.Equal("rewritten", provider.LastTitle);
    }


    [Fact]
    public async Task DeviceId_UpsertDoesNotDuplicateOnTokenRotation()
    {
        var (manager, repo, _) = Build();
        await manager.RegisterDevice(new DeviceRegistration { DeviceToken = "t1", Platform = DevicePlatform.iOS, DeviceId = "install-1" });
        await manager.RegisterDevice(new DeviceRegistration { DeviceToken = "t2", Platform = DevicePlatform.iOS, DeviceId = "install-1" });

        Assert.Equal(1, repo.Count);
        var regs = await repo.GetRegistrations(PushFilter.Broadcast);
        Assert.Equal("t2", regs.Single().DeviceToken);
    }
}
