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
| `src/Shiny.Extensions.Push` | Core: models, interfaces, `PushManager` orchestrator, builder/DI, in-memory repo, debug provider |
| `src/Shiny.Extensions.Push.Apns` | Direct APNs provider (token-based .p8 / ES256 over HTTP/2) |
| `src/Shiny.Extensions.Push.DocumentDb` | `IPushRepository` backed by Shiny.DocumentDb (any provider) |
| `tests/Shiny.Extensions.Push.Tests` | xUnit tests (manager, metrics, multi-key, JWT, APNs payload, SQLite integration) |

Abstractions currently live *in* the core project (not a separate `.Abstractions`). Providers
reference the core. **Decision:** keep it one project until a second provider exists and we feel the
weight; splitting later is mechanical.

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
native-only features. Only `ApplePushOptions` is wired up today.

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
- Pin note: Shiny.DocumentDb 8.0.0 requires `Microsoft.Extensions.*` ≥ 10.0.3 (we pin 10.0.3). `Remove<T>`
  takes `(id, CancellationToken)` — **no** `JsonTypeInfo` (no deserialization); the other CRUD/query calls take
  the optional `JsonTypeInfo<T>`.

## Naming
Namespace/package root is `Shiny.Extensions.Push` (this is the server counterpart to the client-side
`Shiny.Push`). **Assumption, easily renamed** if you want a different brand — it's a mechanical
find/replace across `src`, `tests`, csproj `RootNamespace`, and the `.slnx`.

## Roadmap / known gaps (not yet built)

- **FCM provider** (`Shiny.Extensions.Push.Fcm`) — HTTP v1, OAuth2 service account, 500/multicast batching.
  `AndroidPushOptions` is modelled but unused.
- **WebPush provider** — VAPID; note WebPush registration is endpoint + p256dh/auth keys (carried in
  `DeviceRegistration.Data`), not a single token. `WebPushOptions` modelled but unused.
- **DocumentDb read pushdown** — repository CRUD is done (decision 10), but reads scan + filter in-process.
  Push UserIdentifier/AppId equality into `store.Query<T>().Where(...)` for large tables.
- **Topics/subscriptions** — subscribe/unsubscribe device↔topic (FCM topics / provider pub-sub).
- **Tracing** — metrics are done (decision 8); `Activity`/distributed tracing spans per batch/send are
  not yet added. Optional delivery-receipt webhooks later.
- **`apns-id` correlation** — capture APNs' returned `apns-id` into `PushDeliveryResult` for tracing.
- **Outbox/durable queue** — out of scope for v1; the provider/result seams allow adding it later.

### Explicitly out of scope (decided, not "todo")
- **Retry / backoff** — not building it. `RateLimited` is surfaced on the result for callers to handle;
  the library does not retry or honour `Retry-After` itself. (user decision, 2026-06-20)

### Done since the first cut
- ✅ **Metrics** (`System.Diagnostics.Metrics` / `IMeterFactory`) — decision 8.
- ✅ **Multi-app / multi-tenant** via keyed registration + `AppId` — decision 9.
- ✅ **DocumentDb `IPushRepository`** (any Shiny.DocumentDb provider), SQLite-integration-tested — decision 10.

## CI / GitHub Actions

Two workflows in `.github/workflows/` (brought over from `shinyorg/extensions` and adapted):

- **`build.yml`** — on push to `main`/`dev`/`preview`/`v*` (and manual dispatch). Runs on `ubuntu-latest`
  (pure server libs — no MAUI workload, unlike the extensions repo). Builds `ServerPush.slnx` in Release
  with `PublicRelease=true`, runs the test project, uploads `**/*.nupkg`, and pushes to nuget.org on
  `main`/`v*`. **Requires repo secret `NUGETAPIKEY`.**
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
- `this.`-qualified member access (matches the existing style in this repo).
- Tokens are masked in logs (`PushManager.Mask`).
- New providers: implement `IPushProvider`, ship an `Add<Provider>(this IPushBuilder)` extension that
  registers options + any `HttpClient` + the provider, and keep the project AOT-clean.
