using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Shiny.Extensions.Push;
using Shiny.Extensions.Push.Apns;

namespace Shiny.Extensions.Push.Tests;


public class LiveActivityTests
{
    static readonly Dictionary<string, LiveActivityValue> State = new()
    {
        ["driverName"] = "Sam",
        ["stopsRemaining"] = 3,
        ["progress"] = 0.65,
        ["isDelivered"] = false
    };

    static JsonElement BuildRoot(PushNotification n)
        => JsonDocument.Parse(Encoding.UTF8.GetString(ApnsPayloadBuilder.Build(n))).RootElement;


    #region payload

    [Fact]
    public void Update_WritesEventTimestampAndTypedContentState()
    {
        var aps = BuildRoot(LiveActivityPush.Update(State)).GetProperty("aps");
        var state = aps.GetProperty("content-state");

        Assert.Equal("update", aps.GetProperty("event").GetString());
        Assert.True(aps.GetProperty("timestamp").GetInt64() > 0);

        // The point of LiveActivityValue: Swift's Codable ContentState needs real JSON types.
        Assert.Equal(JsonValueKind.String, state.GetProperty("driverName").ValueKind);
        Assert.Equal(JsonValueKind.Number, state.GetProperty("stopsRemaining").ValueKind);
        Assert.Equal(3, state.GetProperty("stopsRemaining").GetInt32());
        Assert.Equal(0.65, state.GetProperty("progress").GetDouble());
        Assert.Equal(JsonValueKind.False, state.GetProperty("isDelivered").ValueKind);
    }


    [Fact]
    public void Update_HasNoAlertUnlessAsked()
    {
        var aps = BuildRoot(LiveActivityPush.Update(State)).GetProperty("aps");
        Assert.False(aps.TryGetProperty("alert", out _));
    }


    [Fact]
    public void Update_WithAlertText_WritesAlert()
    {
        var n = LiveActivityPush.Update(State, alertTitle: "Almost there", alertBody: "2 stops away") with { Sound = "default" };
        var alert = BuildRoot(n).GetProperty("aps").GetProperty("alert");

        Assert.Equal("Almost there", alert.GetProperty("title").GetString());
        Assert.Equal("2 stops away", alert.GetProperty("body").GetString());
        Assert.Equal("default", alert.GetProperty("sound").GetString());
    }


    [Fact]
    public void Start_WritesAttributesAndType()
    {
        var attributes = new Dictionary<string, LiveActivityValue> { ["orderNumber"] = "A-1234" };
        var aps = BuildRoot(LiveActivityPush.Start("DeliveryAttributes", attributes, State)).GetProperty("aps");

        Assert.Equal("start", aps.GetProperty("event").GetString());
        Assert.Equal("DeliveryAttributes", aps.GetProperty("attributes-type").GetString());
        Assert.Equal("A-1234", aps.GetProperty("attributes").GetProperty("orderNumber").GetString());
        Assert.True(aps.TryGetProperty("content-state", out _));
    }


    [Fact]
    public void End_WritesDismissalDate_AndAllowsNoContentState()
    {
        var dismissal = DateTimeOffset.UtcNow.AddMinutes(5);
        var aps = BuildRoot(LiveActivityPush.End(dismissalDate: dismissal)).GetProperty("aps");

        Assert.Equal("end", aps.GetProperty("event").GetString());
        Assert.Equal(dismissal.ToUnixTimeSeconds(), aps.GetProperty("dismissal-date").GetInt64());
        Assert.False(aps.TryGetProperty("content-state", out _));
    }


    [Fact]
    public void StaleDate_And_RelevanceScore_AreUnixSecondsAndNumber()
    {
        var stale = DateTimeOffset.UtcNow.AddMinutes(30);
        var n = new PushNotification
        {
            Apple = new ApplePushOptions
            {
                LiveActivity = new LiveActivityPushOptions
                {
                    ContentState = State,
                    StaleDate = stale,
                    RelevanceScore = 75
                }
            }
        };
        var aps = BuildRoot(n).GetProperty("aps");

        Assert.Equal(stale.ToUnixTimeSeconds(), aps.GetProperty("stale-date").GetInt64());
        Assert.Equal(75, aps.GetProperty("relevance-score").GetDouble());
    }


    [Fact]
    public void PushToStart_Extras_OnlyApplyToStart()
    {
        var options = new LiveActivityPushOptions
        {
            ContentState = State,
            RequestPushToken = true,
            InputPushChannel = "chan-1"
        };

        var update = BuildRoot(new PushNotification { Apple = new ApplePushOptions { LiveActivity = options } }).GetProperty("aps");
        Assert.False(update.TryGetProperty("input-push-token", out _));
        Assert.False(update.TryGetProperty("input-push-channel", out _));

        var start = BuildRoot(new PushNotification
        {
            Apple = new ApplePushOptions
            {
                LiveActivity = options with
                {
                    Event = LiveActivityEvent.Start,
                    AttributesType = "T",
                    Attributes = new Dictionary<string, LiveActivityValue>()
                }
            }
        }).GetProperty("aps");

        Assert.Equal(1, start.GetProperty("input-push-token").GetInt32());
        Assert.Equal("chan-1", start.GetProperty("input-push-channel").GetString());
    }


    [Fact]
    public void LiveActivity_SuppressesOrdinaryApsFields()
    {
        var n = LiveActivityPush.Update(State) with
        {
            Badge = 4,
            Apple = new ApplePushOptions
            {
                Category = "cat",
                ThreadId = "thread",
                MutableContent = true,
                ContentAvailable = true,
                LiveActivity = new LiveActivityPushOptions { ContentState = State }
            }
        };
        var aps = BuildRoot(n).GetProperty("aps");

        Assert.False(aps.TryGetProperty("badge", out _));
        Assert.False(aps.TryGetProperty("category", out _));
        Assert.False(aps.TryGetProperty("thread-id", out _));
        Assert.False(aps.TryGetProperty("mutable-content", out _));
        Assert.False(aps.TryGetProperty("content-available", out _));
    }


    [Fact]
    public void CustomData_StillRidesAtTopLevel()
    {
        var n = LiveActivityPush.Update(State) with
        {
            Data = new Dictionary<string, string> { ["orderId"] = "42" }
        };
        Assert.Equal("42", BuildRoot(n).GetProperty("orderId").GetString());
    }


    [Fact]
    public void Start_WithoutAttributes_Throws()
    {
        var n = new PushNotification
        {
            Apple = new ApplePushOptions
            {
                LiveActivity = new LiveActivityPushOptions { Event = LiveActivityEvent.Start, ContentState = State }
            }
        };
        Assert.Throws<InvalidOperationException>(() => ApnsPayloadBuilder.Build(n));
    }


    [Fact]
    public void Update_WithoutContentState_Throws()
    {
        var n = new PushNotification
        {
            Apple = new ApplePushOptions { LiveActivity = new LiveActivityPushOptions() }
        };
        Assert.Throws<InvalidOperationException>(() => ApnsPayloadBuilder.Build(n));
    }

    #endregion

    #region values

    [Fact]
    public void Date_DefaultsToSwiftReferenceDate()
    {
        // 2001-01-01T00:00:00Z is Swift's Date zero.
        var value = LiveActivityValue.Date(new DateTimeOffset(2001, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var state = new Dictionary<string, LiveActivityValue> { ["when"] = value };

        var root = BuildRoot(LiveActivityPush.Update(state));
        Assert.Equal(0, root.GetProperty("aps").GetProperty("content-state").GetProperty("when").GetDouble());
    }


    [Fact]
    public void Date_UnixAndIso8601_Encodings()
    {
        var when = new DateTimeOffset(2026, 7, 31, 12, 0, 0, TimeSpan.Zero);
        var state = new Dictionary<string, LiveActivityValue>
        {
            ["unix"] = LiveActivityValue.Date(when, LiveActivityDateEncoding.UnixSeconds),
            ["iso"] = LiveActivityValue.Date(when, LiveActivityDateEncoding.Iso8601)
        };
        var s = BuildRoot(LiveActivityPush.Update(state)).GetProperty("aps").GetProperty("content-state");

        Assert.Equal(when.ToUnixTimeSeconds(), s.GetProperty("unix").GetDouble());
        Assert.StartsWith("2026-07-31T12:00:00", s.GetProperty("iso").GetString());
    }


    [Fact]
    public void Nested_ArraysAndObjects()
    {
        var state = new Dictionary<string, LiveActivityValue>
        {
            ["scores"] = LiveActivityValue.Array(1, 2, 3),
            ["home"] = LiveActivityValue.Object(new Dictionary<string, LiveActivityValue> { ["name"] = "Leafs", ["goals"] = 2 }),
            ["raw"] = LiveActivityValue.Json("""{"a":[true,null]}"""),
            ["nothing"] = LiveActivityValue.Null
        };
        var s = BuildRoot(LiveActivityPush.Update(state)).GetProperty("aps").GetProperty("content-state");

        Assert.Equal(3, s.GetProperty("scores").GetArrayLength());
        Assert.Equal("Leafs", s.GetProperty("home").GetProperty("name").GetString());
        Assert.Equal(2, s.GetProperty("home").GetProperty("goals").GetInt32());
        Assert.Equal(JsonValueKind.True, s.GetProperty("raw").GetProperty("a")[0].ValueKind);
        Assert.Equal(JsonValueKind.Null, s.GetProperty("nothing").ValueKind);
    }

    #endregion

    #region transport

    static (ApnsProvider Provider, List<HttpRequestMessage> Requests) BuildProvider()
    {
        var requests = new List<HttpRequestMessage>();
        var stub = new StubHttpHandler(req =>
        {
            requests.Add(req);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddPushNotifications(b => b.AddApns(o =>
        {
            o.TeamId = "TEAM123456";
            o.KeyId = "KEY1234567";
            o.BundleId = "com.example.app";
            using var ec = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            o.PrivateKey = ec.ExportPkcs8PrivateKeyPem();
        }));
        services.AddHttpClient(ApnsProvider.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => stub);

        var sp = services.BuildServiceProvider();
        return (sp.GetServices<IPushProvider>().OfType<ApnsProvider>().Single(), requests);
    }


    static string? Header(HttpRequestMessage request, string name)
        => request.Headers.TryGetValues(name, out var values) ? values.FirstOrDefault() : null;


    [Fact]
    public async Task Send_UsesLiveActivityPushTypeAndTopic()
    {
        var (provider, requests) = BuildProvider();
        var registration = new DeviceRegistration
        {
            DeviceToken = "activity-token",
            Platform = DevicePlatform.iOS,
            TokenKind = PushTokenKind.LiveActivityUpdate
        };

        await provider.Send(LiveActivityPush.Update(State), registration);

        var request = Assert.Single(requests);
        Assert.Equal("liveactivity", Header(request, "apns-push-type"));
        Assert.Equal("com.example.app.push-type.liveactivity", Header(request, "apns-topic"));
        Assert.Equal("10", Header(request, "apns-priority"));
    }


    [Fact]
    public async Task Send_HonoursExplicitTopicOverride()
    {
        var (provider, requests) = BuildProvider();
        var n = LiveActivityPush.Update(State);
        n = n with { Apple = n.Apple! with { TopicOverride = "com.other.topic" } };

        await provider.Send(n, new DeviceRegistration { DeviceToken = "t", Platform = DevicePlatform.iOS });

        Assert.Equal("com.other.topic", Header(Assert.Single(requests), "apns-topic"));
    }


    [Fact]
    public async Task Send_NormalPriority_IsFive()
    {
        var (provider, requests) = BuildProvider();
        var n = LiveActivityPush.Update(State) with { Priority = PushPriority.Normal };

        await provider.Send(n, new DeviceRegistration { DeviceToken = "t", Platform = DevicePlatform.iOS });

        Assert.Equal("5", Header(Assert.Single(requests), "apns-priority"));
    }


    [Fact]
    public async Task ContentAvailable_DoesNotMakeALiveActivityPushSilent()
    {
        var (provider, requests) = BuildProvider();
        var n = LiveActivityPush.Update(State);
        n = n with { Apple = n.Apple! with { ContentAvailable = true } };

        await provider.Send(n, new DeviceRegistration { DeviceToken = "t", Platform = DevicePlatform.iOS });

        Assert.Equal("liveactivity", Header(Assert.Single(requests), "apns-push-type"));
    }

    #endregion

    #region targeting

    [Fact]
    public void Filter_DefaultsToDeviceTokens_AndExcludesLiveActivityTokens()
    {
        var activity = new DeviceRegistration
        {
            DeviceToken = "a",
            Platform = DevicePlatform.iOS,
            TokenKind = PushTokenKind.LiveActivityUpdate
        };
        var device = new DeviceRegistration { DeviceToken = "d", Platform = DevicePlatform.iOS };

        Assert.False(PushFilter.Broadcast.Matches(activity));
        Assert.True(PushFilter.Broadcast.Matches(device));

        var liveFilter = new PushFilter { TokenKind = PushTokenKind.LiveActivityUpdate };
        Assert.True(liveFilter.Matches(activity));
        Assert.False(liveFilter.Matches(device));
    }


    [Fact]
    public async Task Broadcast_DoesNotReachLiveActivityTokens()
    {
        var (manager, sent) = BuildManager();
        await manager.RegisterDevice(new DeviceRegistration { DeviceToken = "device", Platform = DevicePlatform.iOS });
        await manager.RegisterLiveActivityToken(
            new DeviceRegistration { DeviceToken = "activity", Platform = DevicePlatform.iOS },
            PushTokenKind.LiveActivityUpdate
        );

        await manager.Broadcast(new PushNotification { Title = "hi" });

        Assert.Equal(["device"], sent);
    }


    [Fact]
    public async Task SendLiveActivity_InfersTokenKindFromEvent()
    {
        var (manager, sent) = BuildManager();
        await manager.RegisterDevice(new DeviceRegistration { DeviceToken = "device", Platform = DevicePlatform.iOS });
        await manager.RegisterLiveActivityToken(
            new DeviceRegistration { DeviceToken = "start-token", Platform = DevicePlatform.iOS },
            PushTokenKind.LiveActivityStart
        );
        await manager.RegisterLiveActivityToken(
            new DeviceRegistration { DeviceToken = "update-token", Platform = DevicePlatform.iOS },
            PushTokenKind.LiveActivityUpdate
        );

        await manager.SendLiveActivity(LiveActivityPush.Update(State));
        Assert.Equal(["update-token"], sent);

        sent.Clear();
        await manager.SendLiveActivity(LiveActivityPush.Start("T", new Dictionary<string, LiveActivityValue>(), State));
        Assert.Equal(["start-token"], sent);
    }


    [Fact]
    public async Task DeviceAndLiveActivityTokens_CoexistUnderOneDeviceId()
    {
        var (manager, sent) = BuildManager();
        await manager.RegisterDevice(new DeviceRegistration { DeviceToken = "device", Platform = DevicePlatform.iOS, DeviceId = "install-1" });
        await manager.RegisterLiveActivityToken(
            new DeviceRegistration { DeviceToken = "activity", Platform = DevicePlatform.iOS, DeviceId = "install-1" },
            PushTokenKind.LiveActivityUpdate
        );

        await manager.Broadcast(new PushNotification { Title = "hi" });
        Assert.Equal(["device"], sent);

        sent.Clear();
        await manager.SendLiveActivity(LiveActivityPush.Update(State));
        Assert.Equal(["activity"], sent);
    }


    [Fact]
    public async Task RegisterLiveActivityToken_RejectsDeviceKind()
    {
        var (manager, _) = BuildManager();
        await Assert.ThrowsAsync<ArgumentException>(() => manager.RegisterLiveActivityToken(
            new DeviceRegistration { DeviceToken = "t", Platform = DevicePlatform.iOS },
            PushTokenKind.Device
        ));
    }


    static (IPushManager Manager, List<string> Sent) BuildManager()
    {
        var sent = new List<string>();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddPushNotifications(b => b.AddProvider<RecordingProvider>());
        services.AddSingleton(sent);

        var sp = services.BuildServiceProvider();
        return (sp.GetRequiredService<IPushManager>(), sent);
    }


    class RecordingProvider(List<string> sent) : IPushProvider
    {
        public string Identifier => "recording";
        public bool CanDeliver(DeviceRegistration registration) => true;

        public Task<PushDeliveryResult> Send(PushNotification notification, DeviceRegistration registration, CancellationToken cancellationToken = default)
        {
            lock (sent)
                sent.Add(registration.DeviceToken);

            return Task.FromResult(PushDeliveryResult.Success(registration));
        }
    }

    #endregion
}
