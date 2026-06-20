---
name: shiny-extensions-push
description: Generate code using Shiny.Extensions.Push, a server-side push notification dispatch library for .NET with a provider-agnostic core, direct APNs (token .p8 / ES256 over HTTP/2), Shiny.DocumentDb persistence, structured targeting, interceptors, dead-token pruning, multi-app keyed registration, and System.Diagnostics.Metrics — fully AOT/trim-safe.
auto_invoke: true
triggers:
  - AddPushNotifications
  - IPushManager
  - IPushProvider
  - IPushRepository
  - IPushInterceptor
  - PushNotification
  - DeviceRegistration
  - PushFilter
  - PushSendResult
  - PushDeliveryResult
  - AddApns
  - ApnsProvider
  - ApnsOptions
  - UseDocumentDb
  - DocumentDbPushRepository
  - PushMetrics
  - DevicePlatform
  - PushEnvironment
  - SendToUser
  - SendToTags
  - Shiny.Extensions.Push
  - server push notification
  - APNs server
  - send push from server
---

# Shiny.Extensions.Push Skill

You are an expert in **Shiny.Extensions.Push**, a server-side push notification dispatch library for
.NET. It has a provider-agnostic core, a direct **APNs** transport (token-based `.p8` / ES256 over
HTTP/2 — no FCM dependency for Apple), a **Shiny.DocumentDb** persistence option, structured targeting,
an interceptor pipeline, automatic dead-token pruning, multi-app keyed registration, and
`System.Diagnostics.Metrics` telemetry. The whole library is AOT/trim-safe.

## When to Use This Skill

Invoke this skill when the user wants to:
- Send push notifications from a .NET **server/backend** (ASP.NET Core, worker, Aspire) to mobile/desktop devices
- Register/unregister device tokens and store them (in-memory or Shiny.DocumentDb)
- Send to a user across all their devices, to tags/segments, to explicit tokens, or broadcast
- Talk to **APNs directly** with a `.p8` auth key (iOS/macOS)
- Mutate, localize, personalize, or suppress notifications per-device via interceptors
- Automatically prune expired/invalid device tokens and apply rotated tokens
- Serve **multiple apps** from one server (keyed APNs registrations)
- Emit push delivery metrics for OpenTelemetry
- Send silent/background (content-available) pushes

> This is the **server** counterpart. For the on-device client (registering for push, receiving), use
> `Shiny.Push` (the MAUI client library) — a different package.

## Library Overview

- **Core namespace**: `Shiny.Extensions.Push`
- **NuGet packages**:
  - `Shiny.Extensions.Push` — core: models, `IPushManager`/`IPushProvider`/`IPushRepository`/`IPushInterceptor`,
    `PushManager` orchestrator, builder/DI, in-memory repository, debug provider, `PushMetrics`
  - `Shiny.Extensions.Push.Apns` — direct APNs provider (token `.p8` / ES256 over HTTP/2)
  - `Shiny.Extensions.Push.DocumentDb` — `IPushRepository` backed by Shiny.DocumentDb (any provider)
- **Target**: `net10.0`. AOT/trim-safe.

## Setup (Dependency Injection)

```csharp
using Shiny.Extensions.Push;
using Shiny.Extensions.Push.Apns;
using Shiny.Extensions.Push.DocumentDb;
using Shiny.DocumentDb.Sqlite;

services.AddPushNotifications(push =>
{
    // Direct APNs (single app)
    push.AddApns(o =>
    {
        o.TeamId = "ABCDE12345";
        o.KeyId  = "KEY1234567";
        o.BundleId = "com.example.app";
        o.PrivateKeyPath = "AuthKey_KEY1234567.p8";   // or o.PrivateKey = "<PEM contents>"
        // o.ForceEnvironment = PushEnvironment.Sandbox;  // default: honour each registration
    });

    // Persistence (defaults to in-memory if omitted)
    push.UseDocumentDb(o => o.DatabaseProvider = new SqliteDatabaseProvider("Data Source=push.db"));

    // Optional
    // push.AddInterceptor<LocalizationInterceptor>();
    // push.Configure(m => { m.MaxDegreeOfParallelism = 25; m.AutoPruneDeadTokens = true; });
});
```

Defaults when not configured: an in-memory `IPushRepository` and the built-in `PushManager`.
`IMeterFactory`/metrics are wired automatically.

## Registering Devices

Resolve `IPushManager` and register the device when your client app reports its token:

```csharp
await pushManager.RegisterDevice(new DeviceRegistration
{
    DeviceToken    = "<apns-device-token>",
    Platform       = DevicePlatform.iOS,        // iOS, MacOS, Android, Windows, WebBrowser
    DeviceId       = "install-guid",            // stable identity across token rotation (recommended)
    UserIdentifier = "user-42",                 // enables "send to user across all devices"
    Tags           = ["beta", "sports"],
    Locale         = "en-US",
    Environment    = PushEnvironment.Production, // APNs sandbox vs production (tokens are env-specific!)
    AppId          = null                        // set when using multi-app keyed providers
});

await pushManager.UnregisterDevice("<apns-device-token>", DevicePlatform.iOS);
```

## Sending

`IPushManager` returns a `PushSendResult` (`BatchId`, `Sent`, `Failed`, `TokensRemoved`, `Skipped`, `Results`).
One device failing never aborts the batch.

```csharp
var result = await pushManager.SendToUser("user-42", new PushNotification
{
    Title    = "Goal!",
    Message  = "Your team just scored",
    Badge    = 1,
    Sound    = "default",
    DeepLink = "app://match/123",
    Priority = PushPriority.High,
    Data     = new Dictionary<string, string> { ["matchId"] = "123" }
});

await pushManager.SendToTags(["sports"], notification, TagMatch.Any);
await pushManager.SendToTokens(["tokenA", "tokenB"], notification);
await pushManager.Broadcast(notification);

// Full structured targeting (all set clauses are AND-combined):
await pushManager.Send(notification, new PushFilter
{
    Platforms      = [DevicePlatform.iOS],
    Tags           = ["beta"],
    TagMatch       = TagMatch.All,
    Environment    = PushEnvironment.Production
});
```

### Apple-specific options & silent push

```csharp
new PushNotification
{
    Title = "Title",
    Apple = new ApplePushOptions
    {
        Subtitle        = "Subtitle",
        Category        = "MESSAGE_CATEGORY",
        ThreadId        = "thread-1",
        MutableContent  = true,                 // for a Notification Service Extension
        // ContentAvailable = true,             // background push
        // TopicOverride / PushTypeOverride for VoIP/complication topics
    },
    CollapseId  = "score-update",               // apns-collapse-id
    TimeToLive  = TimeSpan.FromHours(1)         // apns-expiration
};

// Silent / background push (no visible alert):
new PushNotification
{
    Apple = new ApplePushOptions { ContentAvailable = true },
    Data  = new Dictionary<string, string> { ["sync"] = "inbox" }
};
```

## Interceptors (localize / personalize / suppress / report)

Implement `IPushInterceptor` and register with `push.AddInterceptor<T>()` (additive; run in order).
Mutate by replacing `context.Notification`; return `InterceptorResult.Skip` to drop a device.

```csharp
public sealed class LocalizationInterceptor : IPushInterceptor
{
    public Task<InterceptorResult> BeforeSend(PushSendContext context, CancellationToken ct = default)
    {
        if (context.Registration.Tags.Contains("opted-out"))
            return Task.FromResult(InterceptorResult.Skip);

        var title = Localize(context.Notification.Title, context.Registration.Locale);
        context.Notification = context.Notification with { Title = title };
        return Task.FromResult(InterceptorResult.Continue);
    }

    public Task OnSent(PushSendContext c, PushDeliveryResult r, CancellationToken ct = default) => Task.CompletedTask;
    public Task OnFailed(PushSendContext c, PushDeliveryResult r, CancellationToken ct = default) => Task.CompletedTask;
}
```

## Dead-token pruning & rotation (automatic)

Providers return a normalized `PushDeliveryStatus`. The manager auto-removes tokens reported
`TokenExpired`/`InvalidToken` (APNs `410 Unregistered` / `BadDeviceToken`) and applies rotated tokens
(`PushDeliveryResult.UpdatedToken`) to the repository. Disable via `PushManagerOptions.AutoPruneDeadTokens = false`.
`RateLimited` is surfaced on the result but **not** retried — backoff is the caller's responsibility.

## Multiple apps (keyed registration)

Register one keyed APNs provider per app; devices carry the matching `AppId`; the manager routes by it.

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

await pushManager.Send(notification, new PushFilter { AppId = "driver", Tags = ["on-shift"] });
```

## Metrics

Telemetry is emitted via `System.Diagnostics.Metrics` under the meter `Shiny.Extensions.Push`
(`PushMetrics.MeterName`): counters `push.notifications.sent` / `.failed` / `.skipped`,
`push.tokens.pruned`, and histogram `push.send.duration` (ms) — tagged by `platform`, `provider`,
`status` (never by `BatchId`, which is high-cardinality).

```csharp
services.AddOpenTelemetry().WithMetrics(m => m.AddMeter(PushMetrics.MeterName));
```

## Persistence (Shiny.DocumentDb)

`Shiny.Extensions.Push.DocumentDb` implements `IPushRepository` over any Shiny.DocumentDb backend
(SQLite, Postgres, SQL Server, Cosmos, Mongo, …). `UseDocumentDb(Action<DocumentStoreOptions>)`
registers the store + repository; `UseDocumentDb()` assumes an `IDocumentStore` is already registered.
Token-keyed operations (save/remove/rotate) are O(1) point lookups; targeted sends currently scan and
filter in-process. To replace persistence entirely, implement `IPushRepository` and call
`push.UseRepository<T>()`.

## Custom providers

Implement `IPushProvider` (`Identifier`, `CanDeliver(registration)`, `Send(...)` returning a
`PushDeliveryResult`) and register additively via `push.AddProvider<T>()`. For local dev/tests, the
built-in `DebugPushProvider` logs instead of sending and claims every platform.

## Key Conventions / Gotchas

- `Title`/`Message` are nullable — silent/background pushes carry only `Data` + `ContentAvailable`.
- APNs tokens are **environment-specific** (sandbox vs production) — set `DeviceRegistration.Environment`.
- Prefer a stable `DeviceId` so re-registration upserts instead of duplicating.
- Custom payload `Data` is `string`-keyed/valued to stay AOT-safe.
- The APNs provider caches its ES256 provider JWT (~50 min) and reuses one pooled HTTP/2 connection.
