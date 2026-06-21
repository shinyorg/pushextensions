using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Shiny.Extensions.Push;

namespace Shiny.Extensions.Push.Infrastructure;


/// <summary>
/// The default orchestrator. It resolves the audience from the repository, runs each device through
/// the interceptor pipeline, hands delivery to the provider that claims the platform, and reacts to
/// the normalized result (pruning dead tokens, applying rotated tokens) — all with bounded concurrency.
/// One device failing never aborts the batch.
/// </summary>
public class PushManager(
    IPushRepository repository,
    IEnumerable<IPushProvider> providers,
    IEnumerable<IPushInterceptor> interceptors,
    IOptions<PushManagerOptions> options,
    PushMetrics metrics,
    ILogger<PushManager> logger
) : IPushManager
{
    readonly IReadOnlyList<IPushProvider> providers = providers.ToList();
    readonly IReadOnlyList<IPushInterceptor> interceptors = interceptors.ToList();
    readonly PushManagerOptions options = options.Value;


    public Task RegisterDevice(DeviceRegistration registration, CancellationToken cancellationToken = default)
        => repository.Save(registration, cancellationToken);

    public Task UnregisterDevice(string deviceToken, DevicePlatform platform, CancellationToken cancellationToken = default)
        => repository.Remove(deviceToken, platform, cancellationToken);

    public Task SubscribeToTopic(string deviceToken, DevicePlatform platform, string topic, CancellationToken cancellationToken = default)
        => repository.Subscribe(deviceToken, platform, topic, cancellationToken);

    public Task UnsubscribeFromTopic(string deviceToken, DevicePlatform platform, string topic, CancellationToken cancellationToken = default)
        => repository.Unsubscribe(deviceToken, platform, topic, cancellationToken);


    public Task<PushSendResult> SendToUser(string userIdentifier, PushNotification notification, CancellationToken cancellationToken = default)
        => Send(notification, new PushFilter { UserIdentifier = userIdentifier }, cancellationToken);

    public Task<PushSendResult> SendToTags(IEnumerable<string> tags, PushNotification notification, TagMatch match = TagMatch.Any, CancellationToken cancellationToken = default)
        => Send(notification, new PushFilter { Tags = tags.ToList(), TagMatch = match }, cancellationToken);

    public Task<PushSendResult> SendToTokens(IEnumerable<string> deviceTokens, PushNotification notification, CancellationToken cancellationToken = default)
        => Send(notification, new PushFilter { DeviceTokens = deviceTokens.ToList() }, cancellationToken);

    public Task<PushSendResult> SendToTopic(string topic, PushNotification notification, CancellationToken cancellationToken = default)
        => Send(notification, new PushFilter { Topic = topic }, cancellationToken);

    public Task<PushSendResult> Broadcast(PushNotification notification, CancellationToken cancellationToken = default)
        => Send(notification, PushFilter.Broadcast, cancellationToken);


    public async Task<PushSendResult> Send(PushNotification notification, PushFilter filter, CancellationToken cancellationToken = default)
    {
        var batchId = Guid.NewGuid();
        var results = new ConcurrentBag<PushDeliveryResult>();

        using var activity = PushDiagnostics.Source.StartActivity("push.send");
        activity?.SetTag("push.batch_id", batchId);

        logger.LogInformation("Push batch {BatchId} starting", batchId);

        var parallelOptions = new ParallelOptions
        {
            MaxDegreeOfParallelism = Math.Max(1, options.MaxDegreeOfParallelism),
            CancellationToken = cancellationToken
        };

        var canBatch = options.EnableBatching && providers.Any(p => p is IPushBatchProvider);
        if (canBatch)
        {
            await SendChunked(batchId, notification, filter, results, parallelOptions, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await Parallel.ForEachAsync(
                repository.StreamRegistrations(filter, cancellationToken),
                parallelOptions,
                async (registration, ct) =>
                {
                    var result = await DeliverOne(batchId, notification, registration, ct).ConfigureAwait(false);
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

        logger.LogInformation(
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

        var provider = SelectProvider(registration);
        if (provider == null)
        {
            RecordNoProvider(registration, activity);
            return PushDeliveryResult.Failed(registration, PushDeliveryStatus.NoProvider, "no provider for platform");
        }
        activity?.SetTag("push.provider", provider.Identifier);

        var context = new PushSendContext(batchId, registration, notification);
        if (await RunBeforeSend(context, provider, ct).ConfigureAwait(false))
        {
            activity?.SetTag("push.status", nameof(PushDeliveryStatus.Skipped));
            return PushDeliveryResult.Failed(registration, PushDeliveryStatus.Skipped);
        }

        var (result, elapsedMs) = await InvokeSend(provider, context, ct).ConfigureAwait(false);

        activity?.SetTag("push.status", result.Status.ToString());
        if (!result.IsSuccess)
            activity?.SetStatus(ActivityStatusCode.Error, result.Reason);

        await HandleResult(context, result, provider.Identifier, elapsedMs, ct).ConfigureAwait(false);
        return result;
    }


    // Batching path: buffer the stream into chunks (bounded by the largest provider batch size), prepare
    // each chunk through provider-selection + interceptors, then deliver batchable groups in one call and
    // anything else per device. Buffering is bounded to one chunk, so broadcasts still don't load the table.
    async Task SendChunked(Guid batchId, PushNotification notification, PushFilter filter, ConcurrentBag<PushDeliveryResult> results, ParallelOptions parallelOptions, CancellationToken ct)
    {
        var chunkSize = Math.Max(
            parallelOptions.MaxDegreeOfParallelism,
            providers.OfType<IPushBatchProvider>().Max(p => Math.Max(1, p.MaxBatchSize))
        );

        var buffer = new List<DeviceRegistration>(chunkSize);
        await foreach (var registration in repository.StreamRegistrations(filter, ct).WithCancellation(ct).ConfigureAwait(false))
        {
            buffer.Add(registration);
            if (buffer.Count >= chunkSize)
            {
                await ProcessChunk(batchId, notification, buffer, results, parallelOptions, ct).ConfigureAwait(false);
                buffer.Clear();
            }
        }
        if (buffer.Count > 0)
            await ProcessChunk(batchId, notification, buffer, results, parallelOptions, ct).ConfigureAwait(false);
    }


    async Task ProcessChunk(Guid batchId, PushNotification notification, IReadOnlyList<DeviceRegistration> chunk, ConcurrentBag<PushDeliveryResult> results, ParallelOptions parallelOptions, CancellationToken ct)
    {
        // 1. Select provider + run interceptors for each device concurrently. Skips/no-provider resolve here.
        var prepared = new ConcurrentBag<Prepared>();
        await Parallel.ForEachAsync(chunk, parallelOptions, async (registration, c) =>
        {
            var (item, early) = await Prepare(batchId, notification, registration, c).ConfigureAwait(false);
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
            results.Add(await DeliverPrepared(p, c).ConfigureAwait(false))).ConfigureAwait(false);

        await Parallel.ForEachAsync(slices, parallelOptions, async (slice, c) =>
            await DeliverBatch(slice.Provider, slice.Items, results, c).ConfigureAwait(false)).ConfigureAwait(false);
    }


    async Task<(Prepared? Prepared, PushDeliveryResult? Early)> Prepare(Guid batchId, PushNotification notification, DeviceRegistration registration, CancellationToken ct)
    {
        var provider = SelectProvider(registration);
        if (provider == null)
        {
            RecordNoProvider(registration, null);
            return (null, PushDeliveryResult.Failed(registration, PushDeliveryStatus.NoProvider, "no provider for platform"));
        }

        var context = new PushSendContext(batchId, registration, notification);
        if (await RunBeforeSend(context, provider, ct).ConfigureAwait(false))
            return (null, PushDeliveryResult.Failed(registration, PushDeliveryStatus.Skipped));

        return (new Prepared(provider, context), null);
    }


    // A single (non-batched) send for an already-prepared device — used for the leftovers of a batching chunk.
    async Task<PushDeliveryResult> DeliverPrepared(Prepared prepared, CancellationToken ct)
    {
        using var activity = PushDiagnostics.Source.StartActivity("push.deliver");
        activity?.SetTag("push.platform", prepared.Context.Registration.Platform.ToString());
        activity?.SetTag("push.provider", prepared.Provider.Identifier);

        var (result, elapsedMs) = await InvokeSend(prepared.Provider, prepared.Context, ct).ConfigureAwait(false);

        activity?.SetTag("push.status", result.Status.ToString());
        if (!result.IsSuccess)
            activity?.SetStatus(ActivityStatusCode.Error, result.Reason);

        await HandleResult(prepared.Context, result, prepared.Provider.Identifier, elapsedMs, ct).ConfigureAwait(false);
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
            logger.LogError(
                "Batch provider {Provider} returned {Got} results for {Expected} registrations; failing the batch",
                provider.Identifier, batchResults.Count, items.Count
            );
            activity?.SetStatus(ActivityStatusCode.Error, "batch result count mismatch");
            foreach (var item in items)
            {
                var mismatch = PushDeliveryResult.Failed(item.Context.Registration, PushDeliveryStatus.Error, "batch result count mismatch");
                await HandleResult(item.Context, mismatch, provider.Identifier, elapsedMs, ct).ConfigureAwait(false);
                results.Add(mismatch);
            }
            return;
        }

        // Results are order-correlated with the input registrations (per the IPushBatchProvider contract).
        for (var i = 0; i < items.Count; i++)
        {
            var result = batchResults[i];
            await HandleResult(items[i].Context, result, provider.Identifier, elapsedMs, ct).ConfigureAwait(false);
            results.Add(result);
        }
    }


    IPushProvider? SelectProvider(DeviceRegistration registration)
        => providers.FirstOrDefault(p => p.CanDeliver(registration));


    void RecordNoProvider(DeviceRegistration registration, Activity? activity)
    {
        logger.LogWarning("No provider can deliver to platform {Platform} (token {Token})", registration.Platform, Mask(registration.DeviceToken));
        metrics.RecordNoProvider(registration.Platform);
        activity?.SetStatus(ActivityStatusCode.Error, "no provider");
    }


    // Runs the interceptor pipeline. Returns true if a Skip short-circuited delivery (and records the metric).
    async Task<bool> RunBeforeSend(PushSendContext context, IPushProvider provider, CancellationToken ct)
    {
        foreach (var interceptor in interceptors)
        {
            var decision = await interceptor.BeforeSend(context, ct).ConfigureAwait(false);
            if (decision.Decision == InterceptorDecision.Skip)
            {
                metrics.RecordSkipped(context.Registration.Platform, provider.Identifier);
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
                metrics.RecordSent(registration.Platform, providerId, elapsedMs);
                if (result.UpdatedToken is { Length: > 0 } && result.UpdatedToken != registration.DeviceToken)
                    await SafeRepo(() => repository.UpdateToken(registration.DeviceToken, registration.Platform, result.UpdatedToken, ct)).ConfigureAwait(false);

                await FanOut(i => i.OnSent(context, result, ct)).ConfigureAwait(false);
                break;

            case PushDeliveryStatus.TokenExpired:
            case PushDeliveryStatus.InvalidToken:
                metrics.RecordPruned(registration.Platform, providerId, result.Status, elapsedMs);
                if (options.AutoPruneDeadTokens)
                {
                    logger.LogInformation("Pruning dead token {Token} ({Reason})", Mask(registration.DeviceToken), result.Reason);
                    await SafeRepo(() => repository.Remove(registration.DeviceToken, registration.Platform, ct)).ConfigureAwait(false);
                }
                await FanOut(i => i.OnFailed(context, result, ct)).ConfigureAwait(false);
                break;

            default:
                metrics.RecordFailed(registration.Platform, providerId, result.Status, elapsedMs);
                logger.LogWarning("Delivery failed for {Token}: {Status} {Reason}", Mask(registration.DeviceToken), result.Status, result.Reason);
                await FanOut(i => i.OnFailed(context, result, ct)).ConfigureAwait(false);
                break;
        }
    }


    async Task FanOut(Func<IPushInterceptor, Task> action)
    {
        foreach (var interceptor in interceptors)
        {
            try
            {
                await action(interceptor).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Interceptor {Interceptor} threw", interceptor.GetType().Name);
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
            logger.LogError(ex, "Repository operation failed");
        }
    }


    static string Mask(string token)
        => token.Length <= 8 ? "***" : string.Concat(token.AsSpan(0, 4), "…", token.AsSpan(token.Length - 4));
}
