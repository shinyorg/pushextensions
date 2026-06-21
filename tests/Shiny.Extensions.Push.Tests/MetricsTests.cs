using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;
using Shiny.Extensions.Push;
using Shiny.Extensions.Push.Infrastructure;

namespace Shiny.Extensions.Push.Tests;


public class MetricsTests
{
    static (IPushManager manager, IServiceProvider sp, TestProvider provider) Build()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        var provider = new TestProvider();
        services.AddPushNotifications(b => b.Services.AddSingleton<IPushProvider>(provider));

        var sp = services.BuildServiceProvider();
        return (sp.GetRequiredService<IPushManager>(), sp, provider);
    }

    static DeviceRegistration Reg(string token) =>
        new() { DeviceToken = token, Platform = DevicePlatform.iOS };


    [Fact]
    public async Task SentCounter_RecordsPerDevice_WithTags()
    {
        var (manager, sp, _) = Build();
        var meterFactory = sp.GetRequiredService<IMeterFactory>();
        using var collector = new MetricCollector<long>(meterFactory, PushMetrics.MeterName, "push.notifications.sent");

        await manager.RegisterDevice(Reg("a"));
        await manager.RegisterDevice(Reg("b"));
        await manager.Broadcast(new PushNotification { Title = "x" });

        var snapshot = collector.GetMeasurementSnapshot();
        Assert.Equal(2, snapshot.Sum(m => m.Value));
        Assert.All(snapshot, m =>
        {
            Assert.Equal("iOS", m.Tags["platform"]);
            Assert.Equal("test", m.Tags["provider"]);
            Assert.Equal("success", m.Tags["status"]);
        });
    }


    [Fact]
    public async Task PrunedCounter_RecordsDeadTokens()
    {
        var (manager, sp, provider) = Build();
        provider.ExpireTokens.Add("dead");
        var meterFactory = sp.GetRequiredService<IMeterFactory>();
        using var pruned = new MetricCollector<long>(meterFactory, PushMetrics.MeterName, "push.tokens.pruned");

        await manager.RegisterDevice(Reg("dead"));
        await manager.Broadcast(new PushNotification { Title = "x" });

        var snapshot = pruned.GetMeasurementSnapshot();
        Assert.Equal(1, snapshot.Sum(m => m.Value));
        Assert.Equal("TokenExpired", snapshot.Single().Tags["status"]);
    }


    [Fact]
    public async Task DurationHistogram_IsRecorded()
    {
        var (manager, sp, _) = Build();
        var meterFactory = sp.GetRequiredService<IMeterFactory>();
        using var duration = new MetricCollector<double>(meterFactory, PushMetrics.MeterName, "push.send.duration");

        await manager.RegisterDevice(Reg("a"));
        await manager.Broadcast(new PushNotification { Title = "x" });

        Assert.NotEmpty(duration.GetMeasurementSnapshot());
    }
}
