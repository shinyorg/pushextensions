using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Shiny.Extensions.Push;

namespace Shiny.Extensions.Push.Tests;


public class TracingTests
{
    [Fact]
    public async Task Send_EmitsBatchAndDeliverActivities()
    {
        // The listener is process-wide and other tests run in parallel, so collect concurrently and
        // scope assertions to this send's own trace.
        var activities = new System.Collections.Concurrent.ConcurrentBag<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == PushDiagnostics.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = activities.Add
        };
        ActivitySource.AddActivityListener(listener);

        var services = new ServiceCollection();
        services.AddLogging();
        var provider = new TestProvider();
        services.AddPushNotifications(b => b.Services.AddSingleton<IPushProvider>(provider));
        var sp = services.BuildServiceProvider();
        var manager = sp.GetRequiredService<IPushManager>();

        await manager.RegisterDevice(new DeviceRegistration { DeviceToken = "a", Platform = DevicePlatform.iOS });
        var result = await manager.Broadcast(new PushNotification { Title = "x" });

        var batch = Assert.Single(activities.Where(a =>
            a.OperationName == "push.send" && a.GetTagItem("push.batch_id")?.ToString() == result.BatchId.ToString()));
        Assert.Equal(1, batch.GetTagItem("push.sent"));

        var deliver = Assert.Single(activities.Where(a =>
            a.OperationName == "push.deliver" && a.TraceId == batch.TraceId));
        Assert.Equal("iOS", deliver.GetTagItem("push.platform"));
        Assert.Equal("test", deliver.GetTagItem("push.provider"));
        Assert.Equal("Success", deliver.GetTagItem("push.status"));
    }
}
