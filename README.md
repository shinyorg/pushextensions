# Shiny.Extensions.Push

Server-side push notification dispatch for .NET. Provider-agnostic core with a direct **APNs** transport
(token-based `.p8` / ES256 over HTTP/2). AOT/trim friendly.

See [CLAUDE.md](./CLAUDE.md) for the architecture and decision log.

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

    // push.UseRepository<DocumentDbPushRepository>();  // defaults to in-memory
    // push.AddInterceptor<LocalizationInterceptor>();
    // push.Configure(m => m.MaxDegreeOfParallelism = 25);
});
```

## Register a device

```csharp
await pushManager.RegisterDevice(new DeviceRegistration
{
    DeviceToken    = "<apns-token>",
    Platform       = DevicePlatform.iOS,
    DeviceId       = "install-guid",          // stable identity across token rotation
    UserIdentifier = "user-42",
    Tags           = ["beta", "sports"],
    Environment    = PushEnvironment.Production
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

Token-keyed operations (save, remove, rotation) are O(1) point lookups. Targeted sends currently scan +
filter in-process (see CLAUDE.md decision 10).

## Metrics

Telemetry is emitted via `System.Diagnostics.Metrics` under the meter **`Shiny.Extensions.Push`**
(`PushMetrics.MeterName`): counters `push.notifications.sent` / `.failed` / `.skipped`,
`push.tokens.pruned`, and histogram `push.send.duration` (ms) — tagged by `platform`, `provider`,
`status`. Wire it into OpenTelemetry:

```csharp
services.AddOpenTelemetry().WithMetrics(m => m.AddMeter(PushMetrics.MeterName));
```

> Note: this library surfaces `RateLimited` on the result but does **not** retry — backoff/`Retry-After`
> handling is the caller's responsibility by design.
