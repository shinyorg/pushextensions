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

        await Parallel.ForEachAsync(
            this.repository.StreamRegistrations(filter, cancellationToken),
            parallelOptions,
            async (registration, ct) =>
            {
                var result = await this.DeliverOne(batchId, notification, registration, ct).ConfigureAwait(false);
                results.Add(result);
            }
        ).ConfigureAwait(false);

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


    async Task<PushDeliveryResult> DeliverOne(Guid batchId, PushNotification notification, DeviceRegistration registration, CancellationToken ct)
    {
        using var activity = PushDiagnostics.Source.StartActivity("push.deliver");
        activity?.SetTag("push.platform", registration.Platform.ToString());

        var provider = this.providers.FirstOrDefault(p => p.CanDeliver(registration));
        if (provider == null)
        {
            this.logger.LogWarning("No provider can deliver to platform {Platform} (token {Token})", registration.Platform, Mask(registration.DeviceToken));
            this.metrics.RecordNoProvider(registration.Platform);
            activity?.SetStatus(ActivityStatusCode.Error, "no provider");
            return PushDeliveryResult.Failed(registration, PushDeliveryStatus.NoProvider, "no provider for platform");
        }
        activity?.SetTag("push.provider", provider.Identifier);

        var context = new PushSendContext(batchId, registration, notification);

        // Interceptor pipeline: any Skip short-circuits.
        foreach (var interceptor in this.interceptors)
        {
            var decision = await interceptor.BeforeSend(context, ct).ConfigureAwait(false);
            if (decision.Decision == InterceptorDecision.Skip)
            {
                this.metrics.RecordSkipped(registration.Platform, provider.Identifier);
                activity?.SetTag("push.status", nameof(PushDeliveryStatus.Skipped));
                return PushDeliveryResult.Failed(registration, PushDeliveryStatus.Skipped);
            }
        }

        var startedAt = Stopwatch.GetTimestamp();
        PushDeliveryResult result;
        try
        {
            result = await provider.Send(context.Notification, registration, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            result = PushDeliveryResult.Failed(registration, PushDeliveryStatus.Error, ex.Message, ex);
        }
        var elapsedMs = Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds;

        activity?.SetTag("push.status", result.Status.ToString());
        if (!result.IsSuccess)
            activity?.SetStatus(ActivityStatusCode.Error, result.Reason);

        await this.HandleResult(context, result, provider.Identifier, elapsedMs, ct).ConfigureAwait(false);
        return result;
    }


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
