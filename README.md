# Shiny.Extensions.Push

Server-side push notification dispatch for .NET. Provider-agnostic core with transports for **APNs**
(direct, `.p8`/ES256 over HTTP/2), **FCM** (HTTP v1, with multicast batching), **Web Push** (VAPID +
RFC 8291) and **WNS** (Windows, modern Windows App SDK / Entra auth). Structured targeting, topics,
interceptors, dead-token pruning, multi-app keyed registration, metrics + tracing. AOT/trim friendly
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
