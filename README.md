# Shiny.Extensions.Push

Server-side push notification dispatch for .NET. Provider-agnostic core with transports for **APNs**
(direct, `.p8`/ES256 over HTTP/2), **FCM** (HTTP v1, with multicast batching), **Web Push** (VAPID +
RFC 8291) and **WNS** (Windows, modern Windows App SDK / Entra auth). Structured targeting, topics,
interceptors, lifecycle event receivers, dead-token pruning, multi-app keyed registration, runtime
static/dynamic (multi-tenant) configuration, metrics + tracing. AOT/trim friendly
(verified by a native-AOT smoke test).

See [`samples/Push.Api`](./samples/Push.Api) for a runnable ASP.NET Core API with a Scalar UI.

| Package | Contents |
|---|---|
| `Shiny.Extensions.Push` | core (manager, in-memory repo, debug provider) **plus the built-in APNs, FCM, Web Push and WNS transports** |
| `Shiny.Extensions.Push.DocumentDb` | persistence over any Shiny.DocumentDb backend |

> The four transports ship **in the core package** — there are no separate `*.Apns` / `*.Fcm` /
> `*.WebPush` / `*.Wns` packages. Each still lives in its own namespace (`Shiny.Extensions.Push.Apns`,
> `.Fcm`, `.WebPush`, `.Wns`) and is opt-in via `AddApns` / `AddFcm` / `AddWebPush` / `AddWns`, so you only
> pay for what you register.

## Platform setup

Before the library can send anything you need credentials from each platform, and the client app has to
hand its device token/subscription to your server. The end-to-end setup per platform:

### Apple — APNs (iOS / macOS)

Requires a **paid Apple Developer account**.

1. **Create an APNs auth key (.p8).** [developer.apple.com](https://developer.apple.com/account) →
   *Certificates, Identifiers & Profiles* → **Keys** → **+**. Give it a name, tick **Apple Push
   Notifications service (APNs)**, register, then **Download** the `.p8` (you can only download it once —
   store it safely). Note the **Key ID** (10 chars) shown next to the key.
2. **Get your Team ID** (10 chars) — top-right of the developer portal, or the *Membership* page.
3. **Bundle ID** — under *Identifiers*, your App ID (e.g. `com.example.app`). Make sure that App ID has
   the **Push Notifications** capability enabled.
4. **Client app** — enable the *Push Notifications* capability, call `registerForRemoteNotifications`, and
   POST the returned device token to your server. (Use [`Shiny.Push`](https://shinylib.net/push/) or the
   native APIs.)
5. **Sandbox vs production** — token auth uses the **same `.p8`** for both; only the APNs host differs and
   is chosen per device by `DeviceRegistration.Environment`. Debug builds get sandbox tokens, App
   Store/TestFlight builds get production tokens — the two are **not** interchangeable.

You end up with: **TeamId**, **KeyId**, **BundleId**, and the **`AuthKey_XXXXXXXXXX.p8`** file.

### Android — FCM (HTTP v1)

Requires a **Google / Firebase account**.

1. **Create a Firebase project** at [console.firebase.google.com](https://console.firebase.google.com)
   (or reuse one). Note the **Project ID**.
2. **Add your Android app** (its package name) and download **`google-services.json`** for the *client*
   app.
3. **Create a server service-account key.** Firebase Console → *Project settings* → **Service accounts** →
   **Generate new private key** → downloads a JSON file containing `project_id`, `client_email`, and
   `private_key`. This is the **server** credential — keep it secret.
4. Ensure the **Firebase Cloud Messaging API (V1)** is enabled (Project settings → *Cloud Messaging*, or
   the Google Cloud console → *APIs & Services*).
5. **Client app** — integrate the Firebase SDK, obtain the FCM registration token, and POST it to your
   server.

You end up with: the **service-account JSON** (pass its path or contents to `AddFcm`).

### Web Push (browsers — VAPID)

Requires your site served over **HTTPS** (or `localhost`) with a **service worker**.

1. **Generate VAPID keys once.** Easiest with the [`web-push`](https://www.npmjs.com/package/web-push)
   CLI:
   ```bash
   npm install -g web-push
   web-push generate-vapid-keys      # prints a base64url Public Key and Private Key
   ```
   (Any P-256 key pair works: the public key is the 65-byte uncompressed point as base64url, the private
   key the 32-byte scalar as base64url.) Pick a **Subject** — a `mailto:` or `https:` contact URL.
2. **Subscribe in the browser** (register a service worker, then subscribe with the VAPID **public** key):
   ```js
   const reg = await navigator.serviceWorker.register('/sw.js');
   const sub = await reg.pushManager.subscribe({
     userVisibleOnly: true,
     applicationServerKey: '<VAPID_PUBLIC_KEY>'   // base64url
   });
   // POST to your server: sub.endpoint, sub.toJSON().keys.p256dh, sub.toJSON().keys.auth
   ```
3. **Handle the push in the service worker** (`/sw.js`):
   ```js
   self.addEventListener('push', e => {
     const d = e.data.json();
     e.waitUntil(self.registration.showNotification(d.title, { body: d.body, data: d }));
   });
   ```

You end up with: VAPID **PublicKey**, **PrivateKey**, **Subject**, and per device the **endpoint** +
**p256dh** + **auth** (mapped to `DeviceToken` and `Data` — see [Register a device](#register-a-device)).

### Windows — WNS (Windows App SDK / Entra)

Uses the **modern** WNS auth model (Windows App SDK / WinUI 3 / unpackaged apps) — Microsoft Entra (Azure
AD), **not** the classic Partner Center Package SID + secret.

1. **Register an app in Microsoft Entra** ([entra.microsoft.com](https://entra.microsoft.com) → *App
   registrations* → **New registration**). Note the **Directory (tenant) ID** and **Application (client)
   ID**.
2. **Create a client secret** (App registration → *Certificates & secrets* → **New client secret**) — copy
   the value (shown once).
3. **Associate the app with WNS** in [Partner Center](https://partner.microsoft.com) and grant the Entra
   app the WNS access, per the [Windows App SDK push docs](https://learn.microsoft.com/windows/apps/windows-app-sdk/notifications/push-notifications/).
4. **Client app** — request a WNS/push channel via the Windows App SDK (`PushNotificationManager`) and POST
   the **channel URI** to your server.

You end up with: **TenantId**, **ClientId**, **ClientSecret**, and per device the **channel URI** (stored
as the registration's `DeviceToken`).

## Wiring

```csharp
services.AddPushNotifications(push =>
{
    push.AddApns(o =>
    {
        o.TeamId = "ABCDE12345";
        o.KeyId  = "KEY1234567";
        o.BundleId = "com.example.app";
        o.PrivateKeyPath = "AuthKey_KEY1234567.p8";   // or o.PrivateKey = "<PEM>"
    });

    push.AddFcm(o => o.ServiceAccountJsonPath = "firebase-service-account.json");

    push.AddWebPush(o =>
    {
        o.PublicKey  = "<vapid-public-key>";    // base64url (web-push format)
        o.PrivateKey = "<vapid-private-key>";
        o.Subject    = "mailto:you@example.com";
    });

    push.AddWns(o =>
    {
        o.TenantId     = "<entra-tenant-id>";
        o.ClientId     = "<app-registration-client-id>";
        o.ClientSecret = "<client-secret>";
    });

    // push.UseDocumentDb(o => o.DatabaseProvider = new SqliteDatabaseProvider("Data Source=push.db"));
    // push.AddInterceptor<LocalizationInterceptor>();
    // push.AddEventReceiver<PushTelemetry>();   // observe batch/sent/failed (zero or more)
    // push.Configure(m => m.MaxDegreeOfParallelism = 25);
});
```

A device registers its platform (`iOS`, `MacOS`, `Android`, `WebBrowser`); the manager routes it to the
provider that claims it. Web Push registrations put the subscription endpoint in `DeviceToken` and the
`p256dh`/`auth` keys in `Data`.

## Register a device

```csharp
// APNs (iOS/macOS) and FCM (Android): the platform's device token
await pushManager.RegisterDevice(new DeviceRegistration
{
    DeviceToken    = "<apns-or-fcm-token>",
    Platform       = DevicePlatform.iOS,      // or Android
    DeviceId       = "install-guid",          // stable identity across token rotation
    UserIdentifier = "user-42",
    Tags           = ["beta", "sports"],
    Environment    = PushEnvironment.Production
});

// Web Push: endpoint goes in DeviceToken; p256dh/auth go in Data
await pushManager.RegisterDevice(new DeviceRegistration
{
    DeviceToken    = subscription.Endpoint,
    Platform       = DevicePlatform.WebBrowser,
    UserIdentifier = "user-42",
    Data           = new Dictionary<string, string>
    {
        ["p256dh"] = subscription.Keys.P256dh,
        ["auth"]   = subscription.Keys.Auth
    }
});
```

## Send

```csharp
var result = await pushManager.SendToUser("user-42", new PushNotification
{
    Title   = "Goal!",
    Message = "Your team just scored",
    Badge   = 1,
    DeepLink = "app://match/123"
});

// result.BatchId, result.Sent, result.Failed, result.TokensRemoved, result.Results[...]

await pushManager.SendToTags(["sports"], notification, TagMatch.Any);
await pushManager.SendToTokens(["tokenA", "tokenB"], notification);
await pushManager.Broadcast(notification);
await pushManager.Send(notification, new PushFilter { Platforms = [DevicePlatform.iOS], Tags = ["beta"] });
```

Silent / background push:

```csharp
new PushNotification
{
    Apple = new ApplePushOptions { ContentAvailable = true },
    Data  = new Dictionary<string, string> { ["sync"] = "inbox" }
};
```

Dead tokens (APNs `410 Unregistered` / `BadDeviceToken`) are pruned automatically; rotated tokens are
applied back to the repository.

**Batching (FCM multicast):** when a provider supports it, devices that share the same notification are
delivered in one transport call (FCM packs up to 500 into a multipart `/batch` request) instead of one
request per device — automatic for broadcasts and topic fan-out, no call-site change. Per-device pruning,
rotation, and `OnSent`/`OnFailed` are preserved. Disable with `push.Configure(m => m.EnableBatching = false)`;
make a custom transport batchable by implementing `IPushBatchProvider`.

## Live Activities (iOS 16.1+)

ActivityKit Live Activities are pushed over APNs, but with their own push type, their own topic, and a
completely different `aps` body. Set `Apple.LiveActivity` (or use the `LiveActivityPush` factories) and
the transport handles all three.

**Live Activity tokens are not device tokens.** Apple issues a *push-to-start* token (one per install,
iOS 17.2+) and a per-activity *update* token (born and dead with the activity). Sending an ordinary
alert to one is rejected with `DeviceTokenNotForTopic`, so registrations carry a `TokenKind` and
`PushFilter` targets `PushTokenKind.Device` by default — a `Broadcast` can never accidentally hit (and
prune) a Live Activity token.

```csharp
// Store the tokens your app reports
await pushManager.RegisterLiveActivityToken(
    new DeviceRegistration
    {
        DeviceToken    = "<push-to-start-token>",
        Platform       = DevicePlatform.iOS,
        DeviceId       = "install-guid",
        UserIdentifier = "user-42"
    },
    PushTokenKind.LiveActivityStart
);

// Start an activity without the app running (iOS 17.2+)
await pushManager.SendLiveActivity(
    LiveActivityPush.Start(
        attributesType: "DeliveryAttributes",              // the Swift ActivityAttributes type name
        attributes:     new Dictionary<string, LiveActivityValue> { ["orderNumber"] = "A-1234" },
        contentState:   new Dictionary<string, LiveActivityValue>
        {
            ["driverName"]     = "Sam",
            ["stopsRemaining"] = 3,
            ["progress"]       = 0.65,
            ["eta"]            = DateTimeOffset.UtcNow.AddMinutes(12)
        },
        alertTitle: "Your order is on the way"
    ),
    new PushFilter { UserIdentifier = "user-42" }
);

// Update / end a specific activity
await pushManager.SendLiveActivityToTokens([activityToken], LiveActivityPush.Update(state));
await pushManager.SendLiveActivityToTokens([activityToken], LiveActivityPush.End(finalState, dismissalDate: DateTimeOffset.UtcNow.AddMinutes(5)));
```

`LiveActivityValue` exists because the widget's Swift `ContentState` is a strongly typed `Codable`
struct — a number must arrive as a JSON number or the whole update is silently dropped. Implicit
conversions cover string/int/long/double/bool/`DateTimeOffset`, and `Array`/`Object`/`Json` cover nested
shapes.

> **Dates:** ActivityKit decodes with a stock Swift `JSONDecoder`, whose default strategy reads a `Date`
> as *seconds since 2001-01-01* — not the Unix epoch. `LiveActivityValue.Date(...)` defaults to that
> Apple reference encoding; pass `LiveActivityDateEncoding.UnixSeconds`/`Iso8601` if your widget decodes
> differently.

**Broadcast channels (iOS 18+)** replace one-push-per-activity with one push per *channel* — the shape
sports scores and transit arrivals want. Resolve `ApnsBroadcastClient` from DI:

```csharp
var channel = await broadcast.CreateChannel();               // hand channel.ChannelId to the app
await broadcast.Broadcast(channel.ChannelId!, LiveActivityPush.Update(state));
await broadcast.DeleteChannel(channel.ChannelId!);
```

The client app side (starting activities, reporting tokens, Android's equivalent) is
[Shiny.Mobile.LiveActivities](https://github.com/shinyorg/liveactivities).

## Multiple apps (multi-keyed)

Register one keyed APNs provider per app. Devices carry the matching `AppId`; the manager routes by it.

```csharp
services.AddPushNotifications(push =>
{
    push.AddApns("consumer", o => { o.BundleId = "com.example.consumer"; /* … */ });
    push.AddApns("driver",   o => { o.BundleId = "com.example.driver";   /* … */ });
});

await pushManager.RegisterDevice(new DeviceRegistration
{
    DeviceToken = "<token>", Platform = DevicePlatform.iOS, AppId = "driver"
});

// target one app explicitly
await pushManager.Send(notification, new PushFilter { AppId = "driver", Tags = ["on-shift"] });
```

## Runtime configuration (dynamic / multi-tenant)

The **static** path is the plain registration you've already seen — `AddApns(o => …)` (or the keyed
`AddApns("key", o => …)`) bakes the credentials in at startup.

For a **dynamic** setup — many apps, or tenants and keys that change without a restart — supply credentials
at **send time** through an `IPushConfigurationProvider`, keyed by `DeviceRegistration.AppId`. Register the
provider once with `UsePushConfiguration<T>()` and opt each transport in with its **no-argument** overload
(`AddApns()` / `AddFcm()` / `AddWebPush()` / `AddWns()`):

```csharp
public sealed class MyTenantConfig(MyDbContext db) : IPushConfigurationProvider   // scoped — can inject a DbContext
{
    public async ValueTask<PushConfiguration?> GetConfiguration(string appId, CancellationToken ct = default)
    {
        var t = await db.Tenants.FindAsync([appId], ct);
        return t is null ? null : new PushConfiguration
        {
            AppId = appId,
            Apns  = t.HasApns ? new ApnsOptions { TeamId = t.TeamId, KeyId = t.KeyId, BundleId = t.BundleId, PrivateKey = t.P8 } : null,
            Fcm   = t.HasFcm  ? new FcmOptions  { ServiceAccountJson = t.FcmJson } : null
        };
    }
}

services.AddPushNotifications(push => push
    .UsePushConfiguration<MyTenantConfig>()   // registered Scoped
    .AddApns()                                // config-driven APNs
    .AddFcm());                               // config-driven FCM
```

`UsePushConfiguration<T>()` registers your provider as **scoped**, so it can depend on scoped services like an
EF `DbContext`; the singleton delivery providers open a fresh DI scope per send to resolve it. The library
reuses each app's minted APNs JWT / OAuth bearer on the transport's normal token lifetime and **re-reads your
current config when that token refreshes**, so a rotated key is picked up within one token-lifetime window —
no version bookkeeping. Registrations with an unknown/unconfigured `AppId` return a failed result (`Error`,
reason `"app not configured for …"`) rather than delivering. Use this model **or** the keyed
`AddApns("key", …)` registration for a given transport, not both.

## Persistence (Shiny.DocumentDb)

The default repository is in-memory. For production, use the `Shiny.Extensions.Push.DocumentDb` package —
it runs on any Shiny.DocumentDb backend (SQLite, Postgres, SQL Server, Cosmos, Mongo, …):

```csharp
services.AddPushNotifications(push =>
{
    push.AddApns(o => { /* … */ });

    // registers the document store for you
    push.UseDocumentDb(o => o.DatabaseProvider =
        new SqliteDatabaseProvider("Data Source=push.db"));

    // …or, if you already registered IDocumentStore via services.AddDocumentStore(…):
    // push.UseDocumentDb();
});
```

Token-keyed operations (save, remove, rotation, subscribe) are O(1) point lookups. Targeted sends push
`UserIdentifier`/`AppId` equality into the store query and filter the rest in-process.

## Topics

Topics are server-side subscriptions that work across every provider:

```csharp
await pushManager.SubscribeToTopic("<token>", DevicePlatform.iOS, "sports");
await pushManager.SendToTopic("sports", new PushNotification { Title = "Goal!" });
await pushManager.UnsubscribeFromTopic("<token>", DevicePlatform.iOS, "sports");
```

## Metrics & tracing

Metrics via `System.Diagnostics.Metrics` under the meter **`Shiny.Extensions.Push`**
(`PushMetrics.MeterName`): counters `push.notifications.sent` / `.failed` / `.skipped`,
`push.tokens.pruned`, and histogram `push.send.duration` (ms) — tagged by `platform`, `provider`,
`status`. Distributed tracing via the `ActivitySource` of the same name (`push.send` batch span +
`push.deliver` per-device span, or one `push.deliver.batch` span for a batched send). Wire both into
OpenTelemetry:

```csharp
services.AddOpenTelemetry()
    .WithMetrics(m => m.AddMeter(PushMetrics.MeterName))
    .WithTracing(t => t.AddSource(PushDiagnostics.ActivitySourceName));
```

> Note: this library surfaces `RateLimited` on the result but does **not** retry — backoff/`Retry-After`
> handling is the caller's responsibility by design.
