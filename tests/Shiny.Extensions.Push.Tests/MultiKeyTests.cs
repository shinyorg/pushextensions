using System.Security.Cryptography;
using Microsoft.Extensions.DependencyInjection;
using Shiny.Extensions.Push;
using Shiny.Extensions.Push.Apns;

namespace Shiny.Extensions.Push.Tests;


public class MultiKeyTests
{
    [Fact]
    public async Task Manager_RoutesByAppId()
    {
        var appA = new KeyedTestProvider("a");
        var appB = new KeyedTestProvider("b");

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddPushNotifications(b =>
        {
            b.Services.AddSingleton<IPushProvider>(appA);
            b.Services.AddSingleton<IPushProvider>(appB);
        });
        var sp = services.BuildServiceProvider();
        var manager = sp.GetRequiredService<IPushManager>();

        await manager.RegisterDevice(new DeviceRegistration { DeviceToken = "ta", Platform = DevicePlatform.iOS, AppId = "a" });
        await manager.RegisterDevice(new DeviceRegistration { DeviceToken = "tb", Platform = DevicePlatform.iOS, AppId = "b" });

        await manager.Broadcast(new PushNotification { Title = "x" });

        Assert.Equal(["ta"], appA.Sent);
        Assert.Equal(["tb"], appB.Sent);
    }


    [Fact]
    public void Apns_KeyedRegistration_BuildsDistinctProviders()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddPushNotifications(b =>
        {
            b.AddApns("appA", o =>
            {
                o.TeamId = "TEAMAAAAAA"; o.KeyId = "KEYAAAAAAA"; o.BundleId = "com.a";
                o.PrivateKey = NewP256Pem();
            });
            b.AddApns("appB", o =>
            {
                o.TeamId = "TEAMBBBBBB"; o.KeyId = "KEYBBBBBBB"; o.BundleId = "com.b";
                o.PrivateKey = NewP256Pem();
            });
        });
        var sp = services.BuildServiceProvider();

        var apns = sp.GetServices<IPushProvider>().OfType<ApnsProvider>().ToList();
        Assert.Equal(2, apns.Count);

        var a = apns.Single(p => p.Identifier == "apns:appA");
        var b = apns.Single(p => p.Identifier == "apns:appB");

        Assert.True(a.CanDeliver(new DeviceRegistration { DeviceToken = "t", Platform = DevicePlatform.iOS, AppId = "appA" }));
        Assert.False(a.CanDeliver(new DeviceRegistration { DeviceToken = "t", Platform = DevicePlatform.iOS, AppId = "appB" }));
        Assert.True(b.CanDeliver(new DeviceRegistration { DeviceToken = "t", Platform = DevicePlatform.iOS, AppId = "appB" }));
    }


    [Fact]
    public void Apns_DefaultRegistration_HandlesNullAppId()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddPushNotifications(b => b.AddApns(o =>
        {
            o.TeamId = "TEAM000000"; o.KeyId = "KEY0000000"; o.BundleId = "com.default";
            o.PrivateKey = NewP256Pem();
        }));
        var sp = services.BuildServiceProvider();

        var apns = sp.GetServices<IPushProvider>().OfType<ApnsProvider>().Single();
        Assert.Equal("apns", apns.Identifier);
        Assert.True(apns.CanDeliver(new DeviceRegistration { DeviceToken = "t", Platform = DevicePlatform.iOS }));
        Assert.False(apns.CanDeliver(new DeviceRegistration { DeviceToken = "t", Platform = DevicePlatform.iOS, AppId = "other" }));
    }


    static string NewP256Pem()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return key.ExportPkcs8PrivateKeyPem();
    }
}
