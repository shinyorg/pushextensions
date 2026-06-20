using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Shiny.Extensions.Push;


/// <summary>
/// The default orchestrator. It resolves the audience from the repository, runs each device through
/// the interceptor pipeline, hands delivery to the provider that claims the platform, and reacts to
/// the normalized result (pruning dead tokens, applying rotated tokens) — all with bounded concurrency.
/// One device failing never aborts the batch.
/// </summary>
public class PushManager : IPushManager
{
    readonly IPushRepository repository;
    readonly IReadOnlyList<IPushProvider> providers;
    readonly IReadOnlyList<IPushInterceptor> interceptors;
    readonly PushManagerOptions options;
    readonly PushMetrics metrics;
    readonly ILogger<PushManager> logger;


    public PushManager(
        IPushRepository repository,
        IEnumerable<IPushProvider> providers,
        IEnumerable<IPushInterceptor> interceptors,
        IOptions<PushManagerOptions> options,
        PushMetrics metrics,
        ILogger<PushManager> logger
    )
    {
        this.repository = repository;
        this.providers = providers.ToList();
        this.interceptors = interceptors.ToList();
        this.options = options.Value;
        this.metrics = metrics;
        this.logger = logger;
    }


    public Task RegisterDevice(DeviceRegistration registration, CancellationToken cancellationToken = default)
        => this.repository.Save(registration, cancellationToken);

    public Task UnregisterDevice(string deviceToken, DevicePlatform platform, CancellationToken cancellationToken = default)
        => this.repository.Remove(deviceToken, platform, cancellationToken);

    public Task SubscribeToTopic(string deviceToken, DevicePlatform platform, string topic, CancellationToken cancellationToken = default)
        => this.repository.Subscribe(deviceToken, platform, topic, cancellationToken);

    public Task UnsubscribeFromTopic(string deviceToken, DevicePlatform platform, string topic, CancellationToken cancellationToken = default)
        => this.repository.Unsubscribe(deviceToken, platform, topic, cancellationToken);


    public Task<PushSendResult> SendToUser(string userIdentifier, PushNotification notification, CancellationToken cancellationToken = default)
        => this.Send(notification, new PushFilter { UserIdentifier = userIdentifier }, cancellationToken);

    public Task<PushSendResult> SendToTags(IEnumerable<string> tags, PushNotification notification, TagMatch match = TagMatch.Any, CancellationToken cancellationToken = default)
        => this.Send(notification, new PushFilter { Tags = tags.ToList(), TagMatch = match }, cancellationToken);

    public Task<PushSendResult> SendToTokens(IEnumerable<string> deviceTokens, PushNotification notification, CancellationToken cancellationToken = default)
        => this.Send(notification, new PushFilter { DeviceTokens = deviceTokens.ToList() }, cancellationToken);

    public Task<PushSendResult> SendToTopic(string topic, PushNotification notification, CancellationToken cancellationToken = default)
        => this.Send(notification, new PushFilter { Topic = topic }, cancellationToken);

    public Task<PushSendResult> Broadcast(PushNotification notification, CancellationToken cancellationToken = default)
        => this.Send(notification, PushFilter.Broadcast, cancellationToken);


    public async Task<PushSendResult> Send(PushNotification notification, PushFilter filter, CancellationToken cancellationToken = default)
    {
        var batchId = Guid.NewGuid();
        var results = new ConcurrentBag<PushDeliveryResult>();

        using var activity = PushDiagnostics.Source.StartActivity("push.send");
        activity?.SetTag("push.batch_id", batchId);

        this.logger.LogInformation("Push batch {BatchId} starting", batchId);

        var parallelOptions = new ParallelOptions
        {
            MaxDegreeOfParallelism = Math.Max(1, this.options.MaxDegreeOfParallelism),
            CancellationToken = cancellationToken
        };

        var canBatch = this.options.EnableBatching && this.providers.Any(p => p is IPushBatchProvider);
        if (canBatch)
        {
            await this.SendChunked(batchId, notification, filter, results, parallelOptions, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await Parallel.ForEachAsync(
                this.repository.StreamRegistrations(filter, cancellationToken),
                parallelOptions,
                async (registration, ct) =>
                {
                    var result = await this.DeliverOne(batchId, notification, registration, ct).ConfigureAwait(false);
                    results.Add(result);
                }
            ).ConfigureAwait(false);
        }

        var sendResult = new PushSendResult { BatchId = batchId, Results = results.ToList() };

        activity?.SetTag("push.total", sendResult.Total);
        activity?.SetTag("push.sent", sendResult.Sent);
        activity?.SetTag("push.failed", sendResult.Failed);
        activity?.SetTag("push.tokens_removed", sendResult.TokensRemoved);
        activity?.SetTag("push.skipped", sendResult.Skipped);

        this.logger.LogInformation(
            "Push batch {BatchId} complete: {Sent} sent, {Failed} failed, {Removed} pruned, {Skipped} skipped",
            batchId, sendResult.Sent, sendResult.Failed, sendResult.TokensRemoved, sendResult.Skipped
        );
        return sendResult;
    }


    // Per-device fast path (used when no provider supports batching). Behaviour is unchanged: one
    // push.deliver span covering provider selection, interceptors, send, and result handling.
    async Task<PushDeliveryResult> DeliverOne(Guid batchId, PushNotification notification, DeviceRegistration registration, CancellationToken ct)
    {
        using var activity = PushDiagnostics.Source.StartActivity("push.deliver");
        activity?.SetTag("push.platform", registration.Platform.ToString());

        var provider = this.SelectProvider(registration);
        if (provider == null)
        {
            this.RecordNoProvider(registration, activity);
            return PushDeliveryResult.Failed(registration, PushDeliveryStatus.NoProvider, "no provider for platform");
        }
        activity?.SetTag("push.provider", provider.Identifier);

        var context = new PushSendContext(batchId, registration, notification);
        if (await this.RunBeforeSend(context, provider, ct).ConfigureAwait(false))
        {
            activity?.SetTag("push.status", nameof(PushDeliveryStatus.Skipped));
            return PushDeliveryResult.Failed(registration, PushDeliveryStatus.Skipped);
        }

        var (result, elapsedMs) = await this.InvokeSend(provider, context, ct).ConfigureAwait(false);

        activity?.SetTag("push.status", result.Status.ToString());
        if (!result.IsSuccess)
            activity?.SetStatus(ActivityStatusCode.Error, result.Reason);

        await this.HandleResult(context, result, provider.Identifier, elapsedMs, ct).ConfigureAwait(false);
        return result;
    }


    // Batching path: buffer the stream into chunks (bounded by the largest provider batch size), prepare
    // each chunk through provider-selection + interceptors, then deliver batchable groups in one call and
    // anything else per device. Buffering is bounded to one chunk, so broadcasts still don't load the table.
    async Task SendChunked(Guid batchId, PushNotification notification, PushFilter filter, ConcurrentBag<PushDeliveryResult> results, ParallelOptions parallelOptions, CancellationToken ct)
    {
        var chunkSize = Math.Max(
            parallelOptions.MaxDegreeOfParallelism,
            this.providers.OfType<IPushBatchProvider>().Max(p => Math.Max(1, p.MaxBatchSize))
        );

        var buffer = new List<DeviceRegistration>(chunkSize);
        await foreach (var registration in this.repository.StreamRegistrations(filter, ct).WithCancellation(ct).ConfigureAwait(false))
        {
            buffer.Add(registration);
            if (buffer.Count >= chunkSize)
            {
                await this.ProcessChunk(batchId, notification, buffer, results, parallelOptions, ct).ConfigureAwait(false);
                buffer.Clear();
            }
        }
        if (buffer.Count > 0)
            await this.ProcessChunk(batchId, notification, buffer, results, parallelOptions, ct).ConfigureAwait(false);
    }


    async Task ProcessChunk(Guid batchId, PushNotification notification, IReadOnlyList<DeviceRegistration> chunk, ConcurrentBag<PushDeliveryResult> results, ParallelOptions parallelOptions, CancellationToken ct)
    {
        // 1. Select provider + run interceptors for each device concurrently. Skips/no-provider resolve here.
        var prepared = new ConcurrentBag<Prepared>();
        await Parallel.ForEachAsync(chunk, parallelOptions, async (registration, c) =>
        {
            var (item, early) = await this.Prepare(batchId, notification, registration, c).ConfigureAwait(false);
            if (early is not null)
                results.Add(early);
            else if (item is { } value)
                prepared.Add(value);
        }).ConfigureAwait(false);

        // 2. Partition into per-device singles and per-batch-provider groups.
        var singles = new List<Prepared>();
        var byBatchProvider = new Dictionary<IPushBatchProvider, List<Prepared>>();
        foreach (var p in prepared)
        {
            if (p.Provider is IPushBatchProvider bp)
            {
                if (!byBatchProvider.TryGetValue(bp, out var list))
                    byBatchProvider[bp] = list = new List<Prepared>();
                list.Add(p);
            }
            else
            {
                singles.Add(p);
            }
        }

        // 3. Cluster each provider's devices by the identical notification instance, then slice by batch size.
        //    A solitary device (e.g. a per-device localized notification) degrades to a normal per-device send.
        var slices = new List<(IPushBatchProvider Provider, List<Prepared> Items)>();
        foreach (var (provider, items) in byBatchProvider)
        {
            var clusters = new Dictionary<object, List<Prepared>>(ReferenceEqualityComparer.Instance);
            foreach (var item in items)
            {
                if (!clusters.TryGetValue(item.Context.Notification, out var list))
                    clusters[item.Context.Notification] = list = new List<Prepared>();
                list.Add(item);
            }

            var max = Math.Max(1, provider.MaxBatchSize);
            foreach (var cluster in clusters.Values)
            {
                for (var i = 0; i < cluster.Count; i += max)
                {
                    var count = Math.Min(max, cluster.Count - i);
                    if (count == 1)
                        singles.Add(cluster[i]);
                    else
                        slices.Add((provider, cluster.GetRange(i, count)));
                }
            }
        }

        // 4. Dispatch.
        await Parallel.ForEachAsync(singles, parallelOptions, async (p, c) =>
            results.Add(await this.DeliverPrepared(p, c).ConfigureAwait(false))).ConfigureAwait(false);

        await Parallel.ForEachAsync(slices, parallelOptions, async (slice, c) =>
            await this.DeliverBatch(slice.Provider, slice.Items, results, c).ConfigureAwait(false)).ConfigureAwait(false);
    }


    async Task<(Prepared? Prepared, PushDeliveryResult? Early)> Prepare(Guid batchId, PushNotification notification, DeviceRegistration registration, CancellationToken ct)
    {
        var provider = this.SelectProvider(registration);
        if (provider == null)
        {
            this.RecordNoProvider(registration, null);
            return (null, PushDeliveryResult.Failed(registration, PushDeliveryStatus.NoProvider, "no provider for platform"));
        }

        var context = new PushSendContext(batchId, registration, notification);
        if (await this.RunBeforeSend(context, provider, ct).ConfigureAwait(false))
            return (null, PushDeliveryResult.Failed(registration, PushDeliveryStatus.Skipped));

        return (new Prepared(provider, context), null);
    }


    // A single (non-batched) send for an already-prepared device — used for the leftovers of a batching chunk.
    async Task<PushDeliveryResult> DeliverPrepared(Prepared prepared, CancellationToken ct)
    {
        using var activity = PushDiagnostics.Source.StartActivity("push.deliver");
        activity?.SetTag("push.platform", prepared.Context.Registration.Platform.ToString());
        activity?.SetTag("push.provider", prepared.Provider.Identifier);

        var (result, elapsedMs) = await this.InvokeSend(prepared.Provider, prepared.Context, ct).ConfigureAwait(false);

        activity?.SetTag("push.status", result.Status.ToString());
        if (!result.IsSuccess)
            activity?.SetStatus(ActivityStatusCode.Error, result.Reason);

        await this.HandleResult(prepared.Context, result, prepared.Provider.Identifier, elapsedMs, ct).ConfigureAwait(false);
        return result;
    }


    async Task DeliverBatch(IPushBatchProvider provider, IReadOnlyList<Prepared> items, ConcurrentBag<PushDeliveryResult> results, CancellationToken ct)
    {
        var notification = items[0].Context.Notification;
        var registrations = new List<DeviceRegistration>(items.Count);
        foreach (var item in items)
            registrations.Add(item.Context.Registration);

        using var activity = PushDiagnostics.Source.StartActivity("push.deliver.batch");
        activity?.SetTag("push.provider", provider.Identifier);
        activity?.SetTag("push.batch_size", registrations.Count);

        var startedAt = Stopwatch.GetTimestamp();
        IReadOnlyList<PushDeliveryResult> batchResults;
        try
        {
            batchResults = await provider.SendBatch(notification, registrations, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            var failed = new List<PushDeliveryResult>(registrations.Count);
            foreach (var registration in registrations)
                failed.Add(PushDeliveryResult.Failed(registration, PushDeliveryStatus.Error, ex.Message, ex));
            batchResults = failed;
        }
        var elapsedMs = Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds;

        if (batchResults.Count != items.Count)
        {
            this.logger.LogError(
                "Batch provider {Provider} returned {Got} results for {Expected} registrations; failing the batch",
                provider.Identifier, batchResults.Count, items.Count
            );
            activity?.SetStatus(ActivityStatusCode.Error, "batch result count mismatch");
            foreach (var item in items)
            {
                var mismatch = PushDeliveryResult.Failed(item.Context.Registration, PushDeliveryStatus.Error, "batch result count mismatch");
                await this.HandleResult(item.Context, mismatch, provider.Identifier, elapsedMs, ct).ConfigureAwait(false);
                results.Add(mismatch);
            }
            return;
        }

        // Results are order-correlated with the input registrations (per the IPushBatchProvider contract).
        for (var i = 0; i < items.Count; i++)
        {
            var result = batchResults[i];
            await this.HandleResult(items[i].Context, result, provider.Identifier, elapsedMs, ct).ConfigureAwait(false);
            results.Add(result);
        }
    }


    IPushProvider? SelectProvider(DeviceRegistration registration)
        => this.providers.FirstOrDefault(p => p.CanDeliver(registration));


    void RecordNoProvider(DeviceRegistration registration, Activity? activity)
    {
        this.logger.LogWarning("No provider can deliver to platform {Platform} (token {Token})", registration.Platform, Mask(registration.DeviceToken));
        this.metrics.RecordNoProvider(registration.Platform);
        activity?.SetStatus(ActivityStatusCode.Error, "no provider");
    }


    // Runs the interceptor pipeline. Returns true if a Skip short-circuited delivery (and records the metric).
    async Task<bool> RunBeforeSend(PushSendContext context, IPushProvider provider, CancellationToken ct)
    {
        foreach (var interceptor in this.interceptors)
        {
            var decision = await interceptor.BeforeSend(context, ct).ConfigureAwait(false);
            if (decision.Decision == InterceptorDecision.Skip)
            {
                this.metrics.RecordSkipped(context.Registration.Platform, provider.Identifier);
                return true;
            }
        }
        return false;
    }


    async Task<(PushDeliveryResult Result, double ElapsedMs)> InvokeSend(IPushProvider provider, PushSendContext context, CancellationToken ct)
    {
        var startedAt = Stopwatch.GetTimestamp();
        PushDeliveryResult result;
        try
        {
            result = await provider.Send(context.Notification, context.Registration, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            result = PushDeliveryResult.Failed(context.Registration, PushDeliveryStatus.Error, ex.Message, ex);
        }
        var elapsedMs = Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds;
        return (result, elapsedMs);
    }


    readonly record struct Prepared(IPushProvider Provider, PushSendContext Context);


    async Task HandleResult(PushSendContext context, PushDeliveryResult result, string providerId, double elapsedMs, CancellationToken ct)
    {
        var registration = context.Registration;

        switch (result.Status)
        {
            case PushDeliveryStatus.Success:
                this.metrics.RecordSent(registration.Platform, providerId, elapsedMs);
                if (result.UpdatedToken is { Length: > 0 } && result.UpdatedToken != registration.DeviceToken)
                    await this.SafeRepo(() => this.repository.UpdateToken(registration.DeviceToken, registration.Platform, result.UpdatedToken, ct)).ConfigureAwait(false);

                await this.FanOut(i => i.OnSent(context, result, ct)).ConfigureAwait(false);
                break;

            case PushDeliveryStatus.TokenExpired:
            case PushDeliveryStatus.InvalidToken:
                this.metrics.RecordPruned(registration.Platform, providerId, result.Status, elapsedMs);
                if (this.options.AutoPruneDeadTokens)
                {
                    this.logger.LogInformation("Pruning dead token {Token} ({Reason})", Mask(registration.DeviceToken), result.Reason);
                    await this.SafeRepo(() => this.repository.Remove(registration.DeviceToken, registration.Platform, ct)).ConfigureAwait(false);
                }
                await this.FanOut(i => i.OnFailed(context, result, ct)).ConfigureAwait(false);
                break;

            default:
                this.metrics.RecordFailed(registration.Platform, providerId, result.Status, elapsedMs);
                this.logger.LogWarning("Delivery failed for {Token}: {Status} {Reason}", Mask(registration.DeviceToken), result.Status, result.Reason);
                await this.FanOut(i => i.OnFailed(context, result, ct)).ConfigureAwait(false);
                break;
        }
    }


    async Task FanOut(Func<IPushInterceptor, Task> action)
    {
        foreach (var interceptor in this.interceptors)
        {
            try
            {
                await action(interceptor).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                this.logger.LogError(ex, "Interceptor {Interceptor} threw", interceptor.GetType().Name);
            }
        }
    }


    async Task SafeRepo(Func<Task> action)
    {
        try
        {
            await action().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            this.logger.LogError(ex, "Repository operation failed");
        }
    }


    static string Mask(string token)
        => token.Length <= 8 ? "***" : string.Concat(token.AsSpan(0, 4), "…", token.AsSpan(token.Length - 4));
}
