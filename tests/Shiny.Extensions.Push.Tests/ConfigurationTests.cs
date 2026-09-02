using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Shiny.Extensions.Push;
using Shiny.Extensions.Push.Apns;
using Shiny.Extensions.Push.Fcm;
using Shiny.Extensions.Push.WebPush;
using Shiny.Extensions.Push.Wns;

namespace Shiny.Extensions.Push.Tests;


public class ConfigurationTests
{
    // A mutable, call-counting in-memory configuration provider standing in for a DB/API-backed one.
    sealed class FakeConfigProvider : IPushConfigurationProvider
    {
        public Dictionary<string, PushConfiguration> Apps { get; } = new(StringComparer.Ordinal);
        public int Calls;

        public ValueTask<PushConfiguration?> GetConfiguration(string appId, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref this.Calls);
            return new ValueTask<PushConfiguration?>(this.Apps.GetValueOrDefault(appId));
        }
    }

    static string NewP256Pem()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return key.ExportPkcs8PrivateKeyPem();
    }

    static ApnsOptions Apns(string bundleId) =>
        new() { TeamId = "TEAMAAAAAA", KeyId = "KEYAAAAAAA", BundleId = bundleId, PrivateKey = NewP256Pem() };

    static string ServiceAccountJson(string projectId)
    {
        using var rsa = RSA.Create(2048);
        return JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["type"] = "service_account",
            ["project_id"] = projectId,
            ["client_email"] = $"x@{projectId}.iam.gserviceaccount.com",
            ["private_key"] = rsa.ExportPkcs8PrivateKeyPem(),
            ["token_uri"] = "https://oauth2.googleapis.com/token"
        });
    }

    static (string pub, string priv) NewVapidKeys()
    {
        using var ec = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var p = ec.ExportParameters(true);
        var pub = new byte[65];
        pub[0] = 0x04;
        p.Q.X!.CopyTo(pub, 1);
        p.Q.Y!.CopyTo(pub, 33);
        return (System.Buffers.Text.Base64Url.EncodeToString(pub), System.Buffers.Text.Base64Url.EncodeToString(p.D!));
    }

    // Builds a service provider that registers `configProvider` + one delivery provider type, routing the
    // given named HttpClient(s) to the stub.
    static ServiceProvider Build<TProvider>(IPushConfigurationProvider configProvider, StubHttpHandler stub, params string[] httpClientNames)
        where TProvider : class, IPushProvider
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(configProvider);
        services.AddPushNotifications(b => b.Services.AddSingleton<IPushProvider, TProvider>());
        foreach (var name in httpClientNames)
            services.AddHttpClient(name).ConfigurePrimaryHttpMessageHandler(() => stub);
        return services.BuildServiceProvider();
    }


    // ---- APNs ----

    [Fact]
    public async Task Apns_RoutesConfig_ByAppId()
    {
        var provider = new FakeConfigProvider();
        provider.Apps["a"] = new PushConfiguration { AppId = "a", Apns = Apns("com.a") };
        provider.Apps["b"] = new PushConfiguration { AppId = "b", Apns = Apns("com.b") };

        var stub = new StubHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var sp = Build<ApnsTenantProvider>(provider, stub, ApnsProvider.HttpClientName);
        var apns = sp.GetServices<IPushProvider>().OfType<ApnsTenantProvider>().Single();

        await apns.Send(new PushNotification { Title = "x" }, new DeviceRegistration { DeviceToken = "ta", Platform = DevicePlatform.iOS, AppId = "a" });
        await apns.Send(new PushNotification { Title = "x" }, new DeviceRegistration { DeviceToken = "tb", Platform = DevicePlatform.iOS, AppId = "b" });

        Assert.Equal("com.a", stub.Requests[0].Headers.GetValues("apns-topic").Single());
        Assert.Equal("com.b", stub.Requests[1].Headers.GetValues("apns-topic").Single());
        Assert.Equal("apns", apns.Identifier);
        Assert.True(apns.CanDeliver(new DeviceRegistration { DeviceToken = "t", Platform = DevicePlatform.MacOS, AppId = "anything" }));
    }


    [Fact]
    public async Task Apns_UnknownApp_MapsToError()
    {
        var stub = new StubHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var sp = Build<ApnsTenantProvider>(new FakeConfigProvider(), stub, ApnsProvider.HttpClientName);
        var apns = sp.GetServices<IPushProvider>().OfType<ApnsTenantProvider>().Single();

        var result = await apns.Send(new PushNotification { Title = "x" }, new DeviceRegistration { DeviceToken = "t", Platform = DevicePlatform.iOS, AppId = "ghost" });

        Assert.Equal(PushDeliveryStatus.Error, result.Status);
        Assert.Equal("app not configured for APNs", result.Reason);
        Assert.Empty(stub.Requests); // no network call for an unknown app
    }


    [Fact]
    public async Task Apns_ConfigChange_IsPickedUpPerSend()
    {
        var provider = new FakeConfigProvider();
        provider.Apps["a"] = new PushConfiguration { AppId = "a", Apns = Apns("com.first") };

        var stub = new StubHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var sp = Build<ApnsTenantProvider>(provider, stub, ApnsProvider.HttpClientName);
        var apns = sp.GetServices<IPushProvider>().OfType<ApnsTenantProvider>().Single();
        var reg = new DeviceRegistration { DeviceToken = "t", Platform = DevicePlatform.iOS, AppId = "a" };

        await apns.Send(new PushNotification { Title = "x" }, reg);
        // Rotate the app's bundle id in the provider — no restart, no invalidation call.
        provider.Apps["a"] = provider.Apps["a"] with { Apns = Apns("com.second") };
        await apns.Send(new PushNotification { Title = "x" }, reg);

        Assert.Equal("com.first", stub.Requests[0].Headers.GetValues("apns-topic").Single());
        Assert.Equal("com.second", stub.Requests[1].Headers.GetValues("apns-topic").Single());
    }


    // ---- FCM ----

    [Fact]
    public async Task Fcm_RoutesConfig_ByAppId()
    {
        var provider = new FakeConfigProvider();
        provider.Apps["a"] = new PushConfiguration { AppId = "a", Fcm = new FcmOptions { ServiceAccountJson = ServiceAccountJson("proj-a") } };
        provider.Apps["b"] = new PushConfiguration { AppId = "b", Fcm = new FcmOptions { ServiceAccountJson = ServiceAccountJson("proj-b") } };

        var sends = new List<(string Path, string Body)>();
        var stub = new StubHttpHandler(req =>
        {
            if (req.RequestUri!.Host.Contains("oauth2"))
                return StubHttpHandler.Json(HttpStatusCode.OK, """{"access_token":"fake-token","expires_in":3600}""");

            sends.Add((req.RequestUri.AbsolutePath, req.Content!.ReadAsStringAsync().Result));
            return StubHttpHandler.Json(HttpStatusCode.OK, """{"name":"projects/x/messages/1"}""");
        });

        var sp = Build<FcmTenantProvider>(provider, stub, FcmProvider.HttpClientName);
        var fcm = sp.GetServices<IPushProvider>().OfType<FcmTenantProvider>().Single();

        var resultA = await fcm.Send(new PushNotification { Title = "x" }, new DeviceRegistration { DeviceToken = "a1", Platform = DevicePlatform.Android, AppId = "a" });
        var resultB = await fcm.Send(new PushNotification { Title = "x" }, new DeviceRegistration { DeviceToken = "b1", Platform = DevicePlatform.Android, AppId = "b" });

        Assert.Equal(PushDeliveryStatus.Success, resultA.Status);
        Assert.Equal(PushDeliveryStatus.Success, resultB.Status);
        Assert.Collection(
            sends,
            x =>
            {
                Assert.Equal("/v1/projects/proj-a/messages:send", x.Path);
                Assert.Contains("\"token\":\"a1\"", x.Body);
            },
            x =>
            {
                Assert.Equal("/v1/projects/proj-b/messages:send", x.Path);
                Assert.Contains("\"token\":\"b1\"", x.Body);
            }
        );
    }


    [Fact]
    public async Task Fcm_UnknownApp_MapsToError()
    {
        var stub = new StubHttpHandler(_ => StubHttpHandler.Json(HttpStatusCode.OK, "{}"));
        var sp = Build<FcmTenantProvider>(new FakeConfigProvider(), stub, FcmProvider.HttpClientName);
        var fcm = sp.GetServices<IPushProvider>().OfType<FcmTenantProvider>().Single();

        var result = await fcm.Send(new PushNotification { Title = "x" }, new DeviceRegistration { DeviceToken = "t", Platform = DevicePlatform.Android, AppId = "ghost" });

        Assert.Equal(PushDeliveryStatus.Error, result.Status);
        Assert.Equal("app not configured for FCM", result.Reason);
    }


    // ---- WebPush ----

    [Fact]
    public async Task WebPush_ResolvesAppVapid_AndSends()
    {
        var (pub, priv) = NewVapidKeys();
        var provider = new FakeConfigProvider();
        provider.Apps["a"] = new PushConfiguration { AppId = "a", WebPush = new Shiny.Extensions.Push.WebPush.WebPushOptions { PublicKey = pub, PrivateKey = priv, Subject = "mailto:t@example.com" } };

        HttpRequestMessage? sent = null;
        var stub = new StubHttpHandler(req => { sent = req; var r = new HttpResponseMessage(HttpStatusCode.Created); r.Headers.Location = new Uri("https://push.example.com/r/1"); return r; });
        var sp = Build<WebPushTenantProvider>(provider, stub, WebPushProvider.HttpClientName);
        var webpush = sp.GetServices<IPushProvider>().OfType<WebPushTenantProvider>().Single();

        var reg = new DeviceRegistration
        {
            DeviceToken = "https://push.example.com/sub/abc",
            Platform = DevicePlatform.WebBrowser,
            AppId = "a",
            Data = new Dictionary<string, string>
            {
                ["p256dh"] = "BCVxsr7N_eNgVRqvHtD0zTZsEc6-VV-JvLexhqUzORcxaOzi6-AYWXvTBHm4bjyPjs7Vd8pZGH6SRpkNtoIAiw4",
                ["auth"] = "BTBZMqHH6r4Tts7J_aSIgg"
            }
        };
        var result = await webpush.Send(new PushNotification { Title = "Hi" }, reg);

        Assert.Equal(PushDeliveryStatus.Success, result.Status);
        Assert.Contains("vapid t=", sent!.Headers.GetValues("Authorization").Single());
    }


    // ---- WNS ----

    [Fact]
    public async Task Wns_ResolvesAppEntra_AndSends()
    {
        var provider = new FakeConfigProvider();
        provider.Apps["a"] = new PushConfiguration { AppId = "a", Wns = new WnsOptions { TenantId = "entra", ClientId = "client", ClientSecret = "secret" } };

        HttpRequestMessage? sent = null;
        var stub = new StubHttpHandler(req =>
        {
            if (req.RequestUri!.Host.Contains("login.microsoftonline.com"))
                return StubHttpHandler.Json(HttpStatusCode.OK, """{"access_token":"fake-token","expires_in":3600}""");
            sent = req;
            var r = new HttpResponseMessage(HttpStatusCode.OK);
            r.Headers.TryAddWithoutValidation("X-WNS-Msg-ID", "MSG-1");
            return r;
        });
        var sp = Build<WnsTenantProvider>(provider, stub, WnsProvider.HttpClientName);
        var wns = sp.GetServices<IPushProvider>().OfType<WnsTenantProvider>().Single();

        var reg = new DeviceRegistration { DeviceToken = "https://db5.notify.windows.com/?token=abc", Platform = DevicePlatform.Windows, AppId = "a" };
        var result = await wns.Send(new PushNotification { Title = "x" }, reg);

        Assert.Equal(PushDeliveryStatus.Success, result.Status);
        Assert.Equal("MSG-1", result.ProviderMessageId);
        Assert.Equal("Bearer", sent!.Headers.Authorization!.Scheme);
    }


    // ---- wiring + manager integration ----

    [Fact]
    public void UsePushConfiguration_RegistersProviderScoped()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddPushNotifications(b => b.UsePushConfiguration<FakeConfigProvider>());

        var descriptor = services.Single(d => d.ServiceType == typeof(IPushConfigurationProvider));
        Assert.Equal(ServiceLifetime.Scoped, descriptor.Lifetime);
    }


    [Fact]
    public void ConfigDrivenOverloads_RegisterTheFourProviders()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddPushNotifications(b => b
            .UsePushConfiguration<FakeConfigProvider>()
            .AddApns().AddFcm().AddWebPush().AddWns());
        var sp = services.BuildServiceProvider();

        var providers = sp.GetServices<IPushProvider>().ToList();
        Assert.Equal(4, providers.Count);
        Assert.Contains(providers, p => p is ApnsTenantProvider);
        Assert.Contains(providers, p => p is FcmTenantProvider);
        Assert.Contains(providers, p => p is WebPushTenantProvider);
        Assert.Contains(providers, p => p is WnsTenantProvider);
    }


    [Fact]
    public async Task ConfigProvider_RoutesThroughManager()
    {
        var stub = new StubHttpHandler(_ => { var r = new HttpResponseMessage(HttpStatusCode.OK); r.Headers.TryAddWithoutValidation("apns-id", "OK-1"); return r; });

        var config = new FakeConfigProvider();
        config.Apps["a"] = new PushConfiguration { AppId = "a", Apns = Apns("com.a") };

        var services = new ServiceCollection();
        services.AddLogging();
        // A scoped registration that hands back our seeded instance (a real provider would load per-scope).
        services.AddScoped<IPushConfigurationProvider>(_ => config);
        services.AddPushNotifications(b => b.AddApns());
        services.AddHttpClient(ApnsProvider.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => stub);
        var sp = services.BuildServiceProvider();
        var manager = sp.GetRequiredService<IPushManager>();

        await manager.RegisterDevice(new DeviceRegistration { DeviceToken = "t", Platform = DevicePlatform.iOS, AppId = "a" });
        var result = await manager.Broadcast(new PushNotification { Title = "x" });

        Assert.Equal(1, result.Sent);
        Assert.Equal("com.a", stub.Requests.Single().Headers.GetValues("apns-topic").Single());
    }


}
