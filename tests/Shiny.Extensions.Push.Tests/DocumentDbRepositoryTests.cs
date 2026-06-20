using Microsoft.Extensions.DependencyInjection;
using Shiny.DocumentDb;
using Shiny.DocumentDb.Sqlite;
using Shiny.Extensions.Push;
using Shiny.Extensions.Push.DocumentDb;

namespace Shiny.Extensions.Push.Tests;


/// <summary>Integration tests against a real on-disk SQLite Shiny.DocumentDb store.</summary>
public sealed class DocumentDbRepositoryTests : IDisposable
{
    readonly string dbPath = Path.Combine(Path.GetTempPath(), $"push-test-{Guid.NewGuid():N}.db");
    readonly ServiceProvider sp;
    readonly IPushManager manager;
    readonly IPushRepository repo;
    readonly TestProvider provider = new();


    public DocumentDbRepositoryTests()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddPushNotifications(b =>
        {
            b.Services.AddSingleton<IPushProvider>(this.provider);
            b.UseDocumentDb(o => o.DatabaseProvider = new SqliteDatabaseProvider($"Data Source={this.dbPath}"));
        });
        this.sp = services.BuildServiceProvider();
        this.manager = this.sp.GetRequiredService<IPushManager>();
        this.repo = this.sp.GetRequiredService<IPushRepository>();
    }


    public void Dispose()
    {
        this.sp.Dispose();
        try { File.Delete(this.dbPath); } catch { /* best-effort temp cleanup */ }
    }


    static DeviceRegistration Reg(string token, string? user = null, params string[] tags) =>
        new() { DeviceToken = token, Platform = DevicePlatform.iOS, UserIdentifier = user, Tags = tags };


    [Fact]
    public void Repository_IsDocumentDbBacked()
        => Assert.IsType<DocumentDbPushRepository>(this.repo);


    [Fact]
    public async Task Save_And_QueryByUser()
    {
        await this.repo.Save(Reg("a", "u1"));
        await this.repo.Save(Reg("b", "u1"));
        await this.repo.Save(Reg("c", "u2"));

        var u1 = await this.repo.GetRegistrations(new PushFilter { UserIdentifier = "u1" });
        Assert.Equal(2, u1.Count);
        Assert.All(u1, r => Assert.Equal("u1", r.UserIdentifier));
    }


    [Fact]
    public async Task QueryByTags_All()
    {
        await this.repo.Save(Reg("a", tags: ["beta", "sports"]));
        await this.repo.Save(Reg("b", tags: ["beta"]));

        var both = await this.repo.GetRegistrations(new PushFilter { Tags = ["beta", "sports"], TagMatch = TagMatch.All });
        Assert.Equal("a", Assert.Single(both).DeviceToken);
    }


    [Fact]
    public async Task Save_IsUpsert_ReplacesFields()
    {
        await this.repo.Save(Reg("a", "u1", "old"));
        await this.repo.Save(Reg("a", "u2", "new"));   // same token+platform → same doc id

        var all = await this.repo.GetRegistrations(PushFilter.Broadcast);
        var reg = Assert.Single(all);
        Assert.Equal("u2", reg.UserIdentifier);
        Assert.Equal(["new"], reg.Tags);
    }


    [Fact]
    public async Task Remove_DeletesRegistration()
    {
        await this.repo.Save(Reg("a"));
        var removed = await this.repo.Remove("a", DevicePlatform.iOS);

        Assert.True(removed);
        Assert.Empty(await this.repo.GetRegistrations(PushFilter.Broadcast));
    }


    [Fact]
    public async Task Topics_SubscribeUnsubscribe_RoundTripsThroughStore()
    {
        await this.repo.Save(Reg("a"));
        await this.repo.Subscribe("a", DevicePlatform.iOS, "news");

        var subscribed = await this.repo.GetRegistrations(new PushFilter { Topic = "news" });
        Assert.Equal("a", Assert.Single(subscribed).DeviceToken);

        await this.repo.Unsubscribe("a", DevicePlatform.iOS, "news");
        Assert.Empty(await this.repo.GetRegistrations(new PushFilter { Topic = "news" }));
    }


    [Fact]
    public async Task UpdateToken_RotatesAndPreservesData()
    {
        await this.repo.Save(Reg("old", "u1", "vip"));
        await this.repo.UpdateToken("old", DevicePlatform.iOS, "new");

        var all = await this.repo.GetRegistrations(PushFilter.Broadcast);
        var reg = Assert.Single(all);
        Assert.Equal("new", reg.DeviceToken);
        Assert.Equal("u1", reg.UserIdentifier);
        Assert.Equal(["vip"], reg.Tags);
    }


    [Fact]
    public async Task ManagerFlow_PrunesDeadToken_FromStore()
    {
        await this.manager.RegisterDevice(Reg("good"));
        await this.manager.RegisterDevice(Reg("dead"));
        this.provider.ExpireTokens.Add("dead");

        var result = await this.manager.Broadcast(new PushNotification { Title = "x" });

        Assert.Equal(1, result.Sent);
        Assert.Equal(1, result.TokensRemoved);
        var remaining = await this.repo.GetRegistrations(PushFilter.Broadcast);
        Assert.Equal("good", Assert.Single(remaining).DeviceToken);
    }


    [Fact]
    public async Task ManagerFlow_RotatesToken_InStore()
    {
        await this.manager.RegisterDevice(Reg("old"));
        this.provider.RotateTokens["old"] = "rotated";

        await this.manager.Broadcast(new PushNotification { Title = "x" });

        var all = await this.repo.GetRegistrations(PushFilter.Broadcast);
        Assert.Equal("rotated", Assert.Single(all).DeviceToken);
    }
}
