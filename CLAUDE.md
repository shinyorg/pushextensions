# Shiny.Extensions.Push — Project Guide & Decision Log

Server-side push notification dispatch for .NET. Provider-agnostic core + pluggable transports.
This file is the running record of *why* the design is the way it is. Update it as decisions change.

## Build / test

```bash
dotnet build            # full solution; src projects build with AOT + trim analyzers on
dotnet test             # xUnit suite (tests/Shiny.Extensions.Push.Tests)
```

- A repo-local `NuGet.config` pins restore to nuget.org only (the machine has a private
  ClearD/Azure DevOps feed that fails auth + breaks CPM source-mapping). Don't remove it.
- Central Package Management: all versions live in `Directory.Packages.props`.
- `src/Directory.Build.props` turns on `IsAotCompatible=true` + `TreatWarningsAsErrors=true`,
  so any AOT/trim-hostile code fails the build. Keep src warning-clean.

## Projects

| Project | Purpose |
|---|---|
| `src/Shiny.Extensions.Push` | **Everything except persistence**: models, interfaces, orchestrator, builder/DI, in-memory repo, debug provider, metrics, tracing — **and the APNs, FCM, Web Push and WNS transports** (each in its own `Apns/` `Fcm/` `WebPush/` `Wns/` folder + namespace). Implementations sit in the `Infrastructure/` folder / `Shiny.Extensions.Push.Infrastructure` namespace. |
| `src/Shiny.Extensions.Push.DocumentDb` | `IPushRepository` backed by Shiny.DocumentDb (any provider) — separate because it pulls a third-party dependency |
| `samples/Push.Api` | ASP.NET Core minimal API + Scalar UI (debug provider, in-memory) |
| `samples/AotSmokeTest` | Native-AOT console exercising core + all 4 providers (CI hardening) |
| `tests/Shiny.Extensions.Push.Tests` | xUnit tests (manager, metrics, multi-key, topics, tracing, APNs/FCM/WebPush/WNS HTTP + crypto, SQLite integration) |

### Why the transports live in the core project (user decision, 2026-06-21)
The APNs/FCM/Web Push/WNS providers are BCL-only (no third-party deps) and their per-platform payload option
objects (`ApplePushOptions`/`AndroidPushOptions`/`WebPushOptions`/`WindowsPushOptions`) already live in the
core. Extra NuGet packages bought nothing but friction, so they were **folded into `Shiny.Extensions.Push`**. Each
transport keeps its own namespace (`Shiny.Extensions.Push.{Apns,Fcm,WebPush}`) and stays opt-in via its
`Add*` extension, so consumers still only pay for what they register. `Microsoft.Extensions.Http` moved
to the core package; `InternalsVisibleTo("…Tests")` lives on the core csproj now.
- **DocumentDb stays separate** — it's the one transport/persistence piece with an external dependency
  (`Shiny.DocumentDb`), so folding it in would force that dep on everyone.
- **No `.Abstractions` split either** — interfaces live in the core root namespace. Splitting later (if a
  third-party wants to implement `IPushProvider` without the transports) is still mechanical.

### Namespace hygiene: user-facing root, implementations in `.Infrastructure` (user decision, 2026-06-21)
The root `Shiny.Extensions.Push` namespace is the **public surface** — interfaces (`IPushManager` & co.),
models/records, enums, options, `PushDiagnostics`, and the `Add*`/`UsePushNotifications` DI entry points.
Concrete implementations users shouldn't reference directly — `PushManager`, `InMemoryPushRepository`,
`DebugPushProvider`, `PushBuilder`, `PushMetrics` — moved to `Shiny.Extensions.Push.Infrastructure`.
- They stay **`public`** (not `internal`): DI, `UseManager<T>()`/`AddProvider<T>()`, and tests still name
  them; relocating the namespace is the signal, not access level.
- Consumers inject the **interface**. The only reason to import `.Infrastructure` is to name a concrete
  type (e.g. `AddProvider<DebugPushProvider>()`); the sample API and impl-touching tests do exactly that.

## Core architecture

```
IPushManager  ──> resolves audience via IPushRepository (PushFilter)
              ──> for each device, runs IPushInterceptor pipeline (BeforeSend → mutate/skip)
              ──> picks the IPushProvider that CanDeliver(registration)
              ──> provider.Send() returns a normalized PushDeliveryResult
              ──> manager reacts: prune dead tokens / rotate updated tokens / fan out OnSent/OnFailed
              ──> aggregates into PushSendResult (BatchId + per-device results)
```

Delivery runs with **bounded concurrency** (`Parallel.ForEachAsync`,
`PushManagerOptions.MaxDegreeOfParallelism`, default 10). One device failing never aborts the batch.

## Key decisions

### 1. Platform ≠ transport. `IPushProvider` is the transport seam.
`DevicePlatform` describes the device; a provider claims platforms via `CanDeliver`. The APNs provider
claims `iOS` + `MacOS`. Providers are **additive** (`AddProvider<T>()`), the manager is a singular
replace (`UseManager<T>()`).

### 2. Direct APNs, not FCM-wrapped iOS. (user decision, 2026-06-20)
We talk to APNs directly: HTTP/2, token auth (.p8 → ES256 JWT). No Google/FCM dependency for Apple.
- JWT is hand-built + ES256-signed with `ECDsa` (P1363 signature format — *not* DER). `System.IdentityModel`
  is intentionally avoided (heavy + reflection).
- The provider JWT is cached and only refreshed every 50 min (`ApnsJwtProvider`). Apple rejects tokens
  regenerated too frequently — do not mint per request.
- HTTP/2 connection is reused via a pooled `SocketsHttpHandler` (`AddApns` registration).
- Sandbox vs production is **per registration** (`DeviceRegistration.Environment`); `ApnsOptions.ForceEnvironment`
  can override. APNs tokens are environment-specific — this is a classic prod outage source.

### 3. AOT/trimming is a first-class goal. (user decision, 2026-06-20)
`IsAotCompatible=true` on all src projects; build is warning-clean today.
- **Targeting uses a structured `PushFilter`, not `Expression<Func<DeviceRegistration,bool>>`.**
  Expression trees fall back to the interpreter under AOT and are trim-hostile; a structured filter is
  fully AOT-safe and translates cleanly to a native query in any backend (DocumentDB, EF, Dapper).
  `PushFilter.Matches()` provides in-process evaluation for backends that can't translate every clause.
- APNs JSON is written by hand with `Utf8JsonWriter`; responses parsed with `JsonDocument`. No
  reflection-based (de)serialization anywhere.
- Custom payload data is `IReadOnlyDictionary<string,string>` (string-typed) to stay AOT-safe.
- DI generic registration methods are annotated with `[DynamicallyAccessedMembers(PublicConstructors)]`.
- Options validation is a manual lambda, not `ValidateDataAnnotations` (which reflects).
- **Not yet proven:** a full `PublishAot` of a consuming app. The analyzers are the guarantee today;
  add a native-AOT publish smoke test to CI when convenient.

### 4. Dead-token lifecycle is built in.
Providers return a normalized `PushDeliveryStatus` (`TokenExpired`/`InvalidToken`/`RateLimited`/...).
The manager auto-prunes expired/invalid tokens (`AutoPruneDeadTokens`, default on) and applies rotated
tokens (`PushDeliveryResult.UpdatedToken`) via the repository.

### 5. Registration identity = `DeviceId`, falling back to token+platform.
Tokens rotate. `IPushRepository.Save` upserts on the stable `DeviceId` when present so re-registration
updates instead of duplicating. The in-memory repo implements this; document DB repo must too.

### 6. Interceptors are a pipeline (additive), with skip + mutate.
`BeforeSend` returns `Continue`/`Skip` and may replace `context.Notification` (records via `with`) for
localization/personalization. `OnSent`/`OnFailed` are for analytics/receipts. Interceptor exceptions are
caught and logged — they never break a batch.

### 7. Rich payloads: neutral core + per-platform escape hatches.
`PushNotification` carries cross-cutting fields (title/body nullable for silent pushes, badge, sound,
collapse id, TTL, priority, data, deep link) plus `Apple`/`Android`/`WebPush` option objects for
native-only features. **All three are wired up:** `ApplePushOptions`→APNs (category, thread-id, subtitle,
mutable/content-available, topic & push-type overrides), `AndroidPushOptions`→FCM `android.notification`
(channel id, icon, color, big-picture image), `WebPushOptions`→WebPush (icon in payload, urgency header).

### 8. Metrics via `System.Diagnostics.Metrics` (`IMeterFactory`). (user decision, 2026-06-20)
`PushMetrics` (meter name `Shiny.Extensions.Push`) emits counters `push.notifications.sent` /
`.failed` / `.skipped`, `push.tokens.pruned`, and a histogram `push.send.duration` (ms). Recorded
centrally in `PushManager` (it has the full result + provider). `AddPushNotifications` calls
`services.AddMetrics()` so `IMeterFactory` is always present; subscribe via OpenTelemetry.
- **Tags are low-cardinality only: `platform`, `provider`, `status`.** `BatchId` is deliberately NOT a
  tag (unbounded → would explode the metrics backend); it stays on `PushSendResult` + logs.
- Platform/status tag values come from switch expressions (interned constants, no per-call `ToString`
  allocation on the hot path).

### 9. Multi-keyed (multi-app) registration via keyed DI. (user decision, 2026-06-20)
A server can push for several apps. `AddApns("app-key", …)` registers, per key: named `ApnsOptions`, a
keyed `ApnsJwtProvider` (own .p8 + JWT cache, DI-owned for disposal), and an `ApnsProvider` instance
exposed as `IPushProvider`. `DeviceRegistration.AppId` (and `PushFilter.AppId`) carry the key; the
manager routes via `CanDeliver` (`platform match && AppId == providerKey`). The keyless `AddApns(…)`
overload uses `Options.DefaultName` ("") and matches registrations with null/empty `AppId`. The shared
HTTP/2 client is registered once (same APNs hosts; per-request auth/URI differ). `ApnsProvider.Identifier`
is `apns` (default) or `apns:<key>` so metrics distinguish apps.

### 10. DocumentDb repository: O(1) writes, in-process filter on reads. (user decision — "out of the gate")
`Shiny.Extensions.Push.DocumentDb` implements `IPushRepository` over `Shiny.DocumentDb` (`IDocumentStore`),
so it runs on any DocumentDb backend the host registers (SQLite, Postgres, SQL Server, Cosmos, Mongo, …).
- **Persistence shape is `PushRegistrationDocument`** (separate from the domain `DeviceRegistration` record
  because DocumentDb requires a mutable `Id`). Document id = `{Platform}|{DeviceToken}`.
- **Hot paths are O(1) point ops**: `Save`=`Upsert`, `Remove`=`Remove<T>(id)`, `UpdateToken`=`Get`+`Remove`+`Upsert`
  — all by id. These are the calls the manager makes constantly while pruning/rotating, so they must not scan.
- **Reads stream the type and apply `PushFilter.Matches` in-process.** Deliberate: it avoids JSON-path
  predicate translation, so there's no coupling to the store's serializer naming policy and it stays fully
  AOT-safe and provider-agnostic. **Trade-off:** a send scans the type's table. Pushing equality clauses
  (UserIdentifier/AppId) down into `store.Query<T>().Where(...)` is the obvious next optimization — left out
  of v1 to avoid the naming/translation pitfalls. If you add it, verify the store's `JsonSerializerOptions`
  naming matches the document's serialized property names.
- **AOT:** the project ships its own source-gen `PushDocumentJsonContext` and passes `JsonTypeInfo<T>` on every
  store call, so it's AOT-safe no matter how the host configured the store's serializer.
- **DeviceId dedup divergence from the in-memory repo:** id is keyed by token (not `DeviceId`), so a device
  re-registering with a new token but same `DeviceId` can leave a transient stale row — it self-prunes on the
  next send when the provider reports the old token dead. Documented; acceptable for v1.
- **Store registration is the host's job.** `UseDocumentDb()` assumes an `IDocumentStore` is registered;
  `UseDocumentDb(Action<DocumentStoreOptions>)` registers one for you (set `DatabaseProvider`).
- Pin note: Shiny.DocumentDb `11.0.0` (v11 targets net10.0) requires `Microsoft.Extensions.*` ≥ 10.0.7
  (we pin 10.0.7). As of v11 the DI surface (`AddDocumentStore`, `AddMultiTenantDocumentStore`, `DocumentStoreOptions`)
  is **folded into the core `Shiny.DocumentDb` package** — the separate `Shiny.DocumentDb.Extensions.DependencyInjection`
  package is gone, so we no longer reference it. `Remove<T>` takes `(id, CancellationToken)` — **no** `JsonTypeInfo`
  (no deserialization); the other CRUD/query calls take the optional `JsonTypeInfo<T>`.

## Naming
Namespace/package root is `Shiny.Extensions.Push` (this is the server counterpart to the client-side
`Shiny.Push`). **Assumption, easily renamed** if you want a different brand — it's a mechanical
find/replace across `src`, `tests`, csproj `RootNamespace`, and the `.slnx`.

## More decisions

### 11. FCM provider (HTTP v1, OAuth2 service account).
`Shiny.Extensions.Push.Fcm` claims `Android`. `FcmAccessTokenProvider` builds an RS256 JWT from the
service-account JSON (parsed with `JsonDocument`, RSA via `ImportFromPem`), exchanges it for a bearer
token, and caches it (~55 min, `SemaphoreSlim`-guarded). Payload built with `Utf8JsonWriter`
(`AndroidPushOptions` → `android.notification`). Error mapping: `UNREGISTERED`→TokenExpired,
`INVALID_ARGUMENT`/`SENDER_ID_MISMATCH`→InvalidToken, `QUOTA_EXCEEDED`/`UNAVAILABLE`→RateLimited.
Multi-app keyed like APNs. **Native-token-only — FCM-native topic pub/sub is not used** (we fan out via
the repository for cross-provider consistency).
- **No FCM provider-side batching (correction to decision 15, 2026-07-20).** Google's multipart
  `fcm.googleapis.com/batch` endpoint has been discontinued. Both FCM providers therefore implement only
  `IPushProvider`; the manager fans out supported `messages:send` requests with bounded concurrency. Only an
  explicit FCM `UNREGISTERED` error prunes a token — a generic HTTP 404 maps to `Error`, preventing an endpoint
  or project-level failure from deleting valid registrations.

### 12. WebPush provider (VAPID + RFC 8291, BCL crypto only).
`Shiny.Extensions.Push.WebPush` claims `WebBrowser`. Encryption (`WebPushCrypto`) is RFC 8291 message
encryption over the RFC 8188 `aes128gcm` content encoding using only the BCL (`ECDiffieHellman`,
`HKDF`, `AesGcm`) — **no third-party dependency** — and is verified against the RFC 8291 §5 test vector.
VAPID (`WebPushVapid`) is an ES256 JWT with keys imported from raw base64url (the standard web-push key
format). The subscription endpoint is the registration's `DeviceToken`; `p256dh`/`auth` live in `Data`.

### 13. Topics = server-side subscription membership.
`DeviceRegistration.Topics` + `PushFilter.Topic`. `IPushManager.SubscribeToTopic`/`UnsubscribeFromTopic`/
`SendToTopic` map to `IPushRepository.Subscribe`/`Unsubscribe` (O(1) point ops) + a topic filter. Modeled
at the repository layer so it works identically across every provider (not tied to FCM-native topics).

### 14. Tracing via `ActivitySource` + `apns-id` correlation.
`PushDiagnostics.ActivitySource` ("Shiny.Extensions.Push") emits `push.send` (batch) and `push.deliver`
(per device) spans; subscribe with `AddSource(PushDiagnostics.ActivitySourceName)`. Providers report a
`ProviderMessageId` on success (APNs `apns-id` header, FCM message `name`, WebPush `Location`).
Batched sends emit one `push.deliver.batch` span (with a `push.batch_size` tag) instead of per-device spans.

### 15. Provider-side batching via `IPushBatchProvider`.
An **optional** capability a provider implements when it can deliver one notification to many devices in a
single transport op (`MaxBatchSize` + `SendBatch`). When `PushManagerOptions.EnableBatching` (default on)
and at least one registered provider implements it, the manager switches from per-device streaming to a
**chunked** path: it buffers the stream one chunk at a time (chunk size = max provider `MaxBatchSize`, so
broadcasts still never load the whole table), runs provider-selection + interceptors per device, then
**groups batch-capable devices by the identical (post-interceptor) notification instance** and hands each
group (sliced to `MaxBatchSize`) to `SendBatch`. Anything else (non-batch providers, no-provider/skip, and
solitary devices) falls back to the per-device path.
- **Grouping is by reference identity of the notification object** — the common broadcast/topic case shares
  one instance and batches optimally; an interceptor that replaces the notification per device (localization)
  yields distinct instances and naturally degrades to per-device sends. Predictable + AOT-safe (no value
  equality over the `Data` dictionary).
- Per-device dead-token pruning, token rotation, metrics, and `OnSent`/`OnFailed` fan-out are unchanged —
  `SendBatch` returns one result per registration (same order) and the manager handles each individually.
- A thrown `SendBatch` or a result-count mismatch fails the whole batch (one `Error` per device).
- No built-in provider currently implements this capability. It remains available for custom transports with
  a supported bulk endpoint; in particular, FCM must not use it because Google's multipart endpoint is gone.

### 16. WNS provider (Windows, modern Entra auth). (user decision, 2026-06-21 — "Modern only")
`Shiny.Extensions.Push.Wns` claims `DevicePlatform.Windows`. It uses the **Windows App SDK / Microsoft Entra
(Azure AD)** auth model — **not** classic Partner Center (Package SID + `login.live.com`). `WnsOptions` takes
`TenantId` + `ClientId` + `ClientSecret`; `WnsAccessTokenProvider` does an OAuth2 **client-credentials**
exchange against `https://login.microsoftonline.com/{tenant}/oauth2/v2.0/token` (scope
`https://wns.windows.com/.default`), no JWT signing, and caches the bearer token (~expiry − 5 min,
`SemaphoreSlim`-guarded). `WnsOptions.TokenEndpoint` overrides the endpoint for sovereign clouds.
- **Send is a POST to the channel URI** (the registration's `DeviceToken`) with `X-WNS-Type`,
  `X-WNS-RequestForStatus`, `X-WNS-PRIORITY` (High→1, Normal→3) and optional `X-WNS-TTL`. Payload built by
  `WnsPayloadBuilder`: a hand-written `ToastGeneric` XML toast by default (title/body `<text>`, `launch`
  from `DeepLink`/`WindowsPushOptions.Launch`, `<audio silent>` when `Sound` is silent), or raw JSON, or a
  verbatim `WindowsPushOptions.Payload` for tile/badge. XML is manually escaped (no `System.Xml`).
- **Error mapping:** `410 Gone`→TokenExpired (prune), `404`→InvalidToken (prune), `406`/`429`→RateLimited,
  `401`→Error + token-cache invalidate, others→Error. WNS can still 200 while dropping/throttling — the
  `X-WNS-NotificationStatus` header (`dropped`/`channelthrottled`) is checked and mapped accordingly.
  Success captures `X-WNS-Msg-ID` as `ProviderMessageId`.
- Multi-app keyed like APNs/FCM. **No batching** (WNS has no multicast endpoint) — fanned out per device.
- `WindowsPushOptions` (core) + `WnsNotificationType` enum (`Toast`/`Tile`/`Badge`/`Raw`).

### 17. Runtime configuration provider — static or dynamic (multi-tenant). (user decision, 2026-07-13)
Beyond the static keyed model (decision 9, all apps known at startup, config baked in), credentials can be
supplied at **send time** via `IPushConfigurationProvider.GetConfiguration(appId)` → `PushConfiguration?`
(one record bundling the four optional per-transport option objects, keyed by `DeviceRegistration.AppId`).
This lets a server onboard/offboard apps or tenants and rotate keys **without a restart**.
- **One delivery provider per transport, config-driven.** `Apns/Fcm/WebPush/WnsTenantProvider` claim their
  platform for *any* app (`CanDeliver` = platform only) and resolve `ApnsOptions`/etc. from the provider per
  send. Unknown app / missing transport config → `PushDeliveryStatus.Error` ("app not configured for …"),
  **not** a token-pruning status; a thrown provider is caught → `Error` per device, never a crashed batch.
  Their `Identifier` collapses to `apns`/`fcm`/… (no `:key`) — per-app would be unbounded metrics cardinality
  (consistent with decision 8: app/tenant is never a metrics tag).
- **Registration mirrors the per-transport keyed style (user decision, 2026-07-13 — "keep the original + a
  different version").** The static path is unchanged — `AddApns(o => …)` / `AddApns("key", o => …)` still
  register the keyed `ApnsProvider` with baked-in config. The dynamic path adds a **no-arg** sibling overload
  per transport (`AddApns()` / `AddFcm()` / `AddWebPush()` / `AddWns()`) that registers the config-driven
  `*TenantProvider`, plus a one-time `UsePushConfiguration<T>()` that registers the host's
  `IPushConfigurationProvider`. **Exclusive per transport** — a keyed `ApnsProvider` and the config-driven
  `ApnsTenantProvider` both claim iOS; pick one per platform. (There is no `StaticPushConfigurationProvider` —
  the original `AddApns(config)` *is* the static case.)
- **The config provider is registered `Scoped` (user decision).** `UsePushConfiguration<T>()` does
  `AddScoped<IPushConfigurationProvider, T>()` so a host implementation can depend on scoped services (an EF
  `DbContext`, a per-request tenant context). The delivery providers are singletons (they hold the app-wide
  `CredentialTokenCache` + `IHttpClientFactory`) and bridge to the scoped provider via `IServiceScopeFactory`:
  `ScopedConfiguration.Resolve` opens a fresh scope per send, resolves `IPushConfigurationProvider`, fetches the
  config, disposes the scope — the rest of the send (mint/HTTP) runs outside it. **Cost:** one DI scope per
  device; acceptable for the scoped-DbContext use case.
- **No cache management (user decision).** The library keeps only a minimal per-app credential reuse map
  (`CredentialTokenCache`, a plain unbounded `ConcurrentDictionary`, no eviction/size/version bookkeeping) —
  required because Apple rate-limits JWT *generation* and re-exchanging an FCM/WNS bearer per send is a round
  trip each push. It refreshes on the transport's normal token lifetime (APNs ~50 min, FCM/WNS ~expiry−5 min,
  WebPush VAPID ~12 h), re-reading the provider's *current* config on refresh — so a **rotated key is picked
  up within one token-lifetime window** with zero version tracking. Per-send request-shaping fields
  (APNs `BundleId`/topic/`ForceEnvironment`) come straight from the per-send resolve, so they're always live.
- **Shared send-cores.** The transport HTTP + error mapping was extracted to internal statics
  (`ApnsSender`/`ApnsJwt`, `FcmSender`/`FcmToken`, `WebPushSender` + static `WebPushVapid`, `WnsSender`/`WnsToken`)
  so the keyed and config-driven providers share one code path (keyed provider behaviour is unchanged).
- **FCM resolves per device.** `FcmTenantProvider.Send` resolves the registration's `AppId`, reuses that app's
  cached bearer, and sends through the app's current Firebase project via `messages:send`.
- Naming: the config type is `IPushConfigurationProvider`/`PushConfiguration` (app-centric, not "tenant") per
  user rename; the delivery-provider classes keep the `*TenantProvider` name as the multi-app/tenant-capable
  providers. Files live in `src/Shiny.Extensions.Push/Configuration/`.

### 18. Event receivers — read-only lifecycle observers, distinct from interceptors. (user decision, 2026-07-13)
`IPushEventReceiver` is a **pure observer** of the send lifecycle (telemetry, receipts, dead-letter capture,
dashboards). It is deliberately **separate from `IPushInterceptor`**, which is a per-device *pipeline*
participant that can `Skip`/mutate the notification and has no batch-level hooks. Receivers cannot alter
delivery. Registered additively via `IPushBuilder.AddEventReceiver<T>()` (as `IEnumerable<IPushEventReceiver>`),
**zero or more** — the manager fans out to all of them.
- **Hooks:** `OnBatchStarted(batchId, filter, notification)` once before any device is contacted;
  `OnSent`/`OnFailed(batchId, registration, notification, PushDeliveryResult)` per device; and
  `OnBatchFinished(batchId, notification, PushSendResult)` once with the aggregate counts.
- **Failure signal is the normalized `PushDeliveryResult`, not a raw `Exception`** (user's original sketch
  passed `Exception`). Most failures in this library never throw — `TokenExpired`/`InvalidToken`/`RateLimited`
  are statuses, with the transport `Exception` (when there is one) hanging off `PushDeliveryResult.Error`.
  Passing the result captures every failure mode; the exception alone would miss most.
- **Fired from the same choke points as the interceptor fan-out** (`PushManager.HandleResult` for per-device
  `OnSent`/`OnFailed`; start/end of `Send` for the batch hooks) via a `FanOutReceivers` helper that mirrors
  `FanOut` — a thrown receiver is logged and swallowed, never breaking a batch or the other receivers.
  **Consequence (documented, matches interceptor semantics):** interceptor-`Skip` and `NoProvider` outcomes
  return early *before* `HandleResult`, so they don't raise per-device `OnFailed`; they're visible only in the
  `OnBatchFinished` `PushSendResult` (`Skipped` / `Failed` counts). Per-device hooks run under the send's
  bounded concurrency, so implementations must be thread-safe.

## Roadmap / known gaps (not yet built)

- **DocumentDb read pushdown beyond UserIdentifier/AppId** — tags/topics/platform still filter in-process.
- **Built-in provider batching** — none of the current transports exposes a supported multicast endpoint;
  they send per device while the manager fans out with bounded concurrency.
- **Outbox/durable queue** — out of scope for v1; the provider/result seams allow adding it later.

### Explicitly out of scope (decided, not "todo")
- **Retry / backoff** — not building it. `RateLimited` is surfaced on the result for callers to handle;
  the library does not retry or honour `Retry-After` itself. (user decision, 2026-06-20)

### Done since the first cut
- ✅ **Metrics** (decision 8) · **Multi-app keyed** (decision 9) · **DocumentDb repo** (decision 10).
- ✅ **FCM** (11) · **WebPush** (12) · **Topics** (13) · **Tracing + apns-id** (14) · **DocumentDb read pushdown** (decision 10).
- ✅ **Provider batching infrastructure** (decision 15, `IPushBatchProvider`; available to custom transports).
- ✅ **Sample API + Scalar** (`samples/Push.Api`) and **native-AOT smoke test** (`samples/AotSmokeTest`, CI job `aot-smoke`) — all 4 providers verified AOT-publishable + runnable.
- ✅ **WNS / Windows provider** (decision 16).
- ✅ **Runtime configuration provider — static + dynamic multi-tenancy** (decision 17, `IPushConfigurationProvider`).
- ✅ **Event receivers — read-only lifecycle observers** (decision 18, `IPushEventReceiver`).

## CI / GitHub Actions

Two workflows in `.github/workflows/` (brought over from `shinyorg/extensions` and adapted):

- **`build.yml`** — on push to `main`/`dev`/`preview`/`v*` (and manual dispatch). Runs on `ubuntu-latest`
  (pure server libs — no MAUI workload, unlike the extensions repo). Builds `ServerPush.slnx` in Release
  with `PublicRelease=true`, runs the test project, uploads `**/*.nupkg`, and pushes to nuget.org on
  `main`/`v*`. **Requires repo secret `NUGETAPIKEY`.** A second job **`aot-smoke`** native-AOT-publishes
  `samples/AotSmokeTest` (installs clang) and runs the binary — `TreatWarningsAsErrors` there makes any
  trim/AOT (ILxxxx) regression fail CI.
- **`sync-skills.yml`** — on push to `main` touching `skills/**` (and manual dispatch). Mirrors this repo's
  `skills/*` into `shinyorg/skills` and opens a PR there. Repo references use `${{ github.repository }}`
  (no hard-coded name). **Requires repo secret `SKILLS_REPO_TOKEN`** (a PAT with write access to
  `shinyorg/skills`).

> The repo has no git remote yet. Set the GitHub remote before these run; the GitHub repo name is
> inferred at runtime, so nothing in the workflows hard-codes it.

## Versioning & packaging

- **Nerdbank.GitVersioning** (`version.json`, currently `0.1`) drives package versions. The
  `Nerdbank.GitVersioning` package is referenced for all projects via root `Directory.Build.targets`;
  `build.yml` checks out with `fetch-depth: 0` (NBGV needs full history). It degrades gracefully with no
  commits, so local builds work before the first commit.
- `src/Directory.Build.props` sets `GeneratePackageOnBuild` in **Release**, so `dotnet build -c Release`
  emits versioned `.nupkg`s (e.g. `Shiny.Extensions.Push.0.1.0-g<hash>.nupkg`). The repo-root `README.md`
  is packed as the package readme (`PackageReadmeFile`).
- Public-release refs (stable versions) are `main`, `preview`, and `v*` tags — see `version.json`.

## The local skill (`skills/shiny-extensions-push/SKILL.md`)

This is the agent-facing "how to generate correct code" doc, and the source of the published
`shiny-extensions-push` Claude Code skill. `sync-skills.yml` pushes it to `shinyorg/skills`.
- One folder per skill under `skills/`; each holds a `SKILL.md` with YAML frontmatter
  (`name`, `description`, `auto_invoke: true`, `triggers:` keyword list) followed by the guidance body.
- Keep it aligned with the code. **Update the `triggers:` list whenever a new public type / provider /
  API is introduced**, and update the default guidance when a recommended pattern changes.

## Releases & documentation

Docs and code are kept in sync. A change is not "done" until these are aligned (do them in the same change
unless there's a reason not to):

1. **Code + tests** (`src/`, `tests/`) — run `dotnet test tests/Shiny.Extensions.Push.Tests/Shiny.Extensions.Push.Tests.csproj`.
2. **Skill** (`skills/shiny-extensions-push/SKILL.md`) — keep aligned; update `triggers:` for new public API.
3. **README.md** (repo root) — packed into every NuGet package; update the feature list when behavior changes.
4. **Documentation site** — a **separate** repo at `~/Desktop/dev/documentation` (rendered to https://shinylib.net).
   - The existing `src/content/docs/push/` is the **client** `Shiny.Push` (MAUI) library. This server
     library needs its **own** section (e.g. `src/content/docs/extensions-push/`) — confirm the slug with
     the maintainer before creating it.
   - Pages are `.mdx`. Add a **release note** per the rules below.

### Release notes
Live in the documentation repo, under this library's docs section (e.g.
`~/Desktop/dev/documentation/src/content/docs/extensions-push/release-notes.mdx`).
- **Version** for a note = the `version` field in `version.json`, raw portion only (strip any
  prerelease/build suffix, e.g. `0.2.0-beta` → `0.2.0`).
- **Heading style**: feature/minor releases headed by `major.minor` (`## 0.2 - Jul 1, 2026`); patch
  releases use full `major.minor.patch`. Newest section stays at the top.
- **Not yet released** (prerelease / next version): add under an existing `## <version> TBD` heading, or
  create one at the top. Edit an unshipped entry in place rather than duplicating.
- **Final release**: the section is dated; add under the matching dated section (or promote the `TBD`).
- Each note is a single `<RN>` line — `import RN from '/src/components/ReleaseNote.astro'` — with
  `type="feature|enhancement|fix|breaking"` (`breaking` is its own type, not a flag).

### Blog posts (only when explicitly requested)
Do **not** write blog posts as part of a fix/feature — only when the user asks. When asked, produce **two**:
1. **Docs site** — `~/Desktop/dev/documentation/src/content/docs/blog/YYYY/MM/<slug>.mdx`; frontmatter
   `title`/`description`/`date`/`authors: [allanritchie]`/`tags`; product/release-note voice; reuse
   components like `<NugetBadge name="Shiny.Extensions.Push" />`; **no hero image** on this site.
2. **Personal blog** — `~/Desktop/dev/blog/src/content/blog/YYYY/MM/<slug>.mdx` (different schema:
   `pubDate: 'Mon DD YYYY'`, `heroImage`, `tags`); first-person narrative; **hero image required**
   (`src/assets/<slug>-hero.svg`, `viewBox="0 0 1200 630"`, house style — crib an existing one).

## Conventions
- Records for immutable models; `required` for mandatory fields.
- **Primary constructors** for service/provider classes. Capture pass-through dependencies as parameters
  and use them directly (no backing field); keep a `readonly` field only when the ctor transforms the
  argument (e.g. `appKey ?? string.Empty`, `options.Get(key)`, `.ToList()`, `IOptions<T>.Value`). When a
  two-step init can't be a field initializer (e.g. `ECDsa.Create()` + `ImportFromPem`), use a `static`
  helper so the field can still initialize from a primary-ctor parameter.
- **No `this.`-qualified access in primary-constructor classes** — reference captured parameters and
  members directly. (Classes with a conventional constructor keep the older `this.`-qualified style.)
- Tokens are masked in logs (`PushManager.Mask`).
- New providers: implement `IPushProvider`, ship an `Add<Provider>(this IPushBuilder)` extension that
  registers options + any `HttpClient` + the provider, and keep the project AOT-clean.
