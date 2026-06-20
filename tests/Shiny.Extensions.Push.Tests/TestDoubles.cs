using System.Collections.Concurrent;
using Shiny.Extensions.Push;

namespace Shiny.Extensions.Push.Tests;


/// <summary>An iOS-only provider that records sends and can simulate expired/rotated tokens.</summary>
public sealed class TestProvider : IPushProvider
{
    public ConcurrentBag<string> Sent { get; } = [];
    public HashSet<string> ExpireTokens { get; } = new(StringComparer.Ordinal);
    public ConcurrentDictionary<string, string> RotateTokens { get; } = new();
    public volatile string? LastTitle;

    public string Identifier => "test";
    public bool CanDeliver(DeviceRegistration registration) => registration.Platform == DevicePlatform.iOS;

    public Task<PushDeliveryResult> Send(PushNotification notification, DeviceRegistration registration, CancellationToken cancellationToken = default)
    {
        this.LastTitle = notification.Title;

        if (this.ExpireTokens.Contains(registration.DeviceToken))
            return Task.FromResult(PushDeliveryResult.Failed(registration, PushDeliveryStatus.TokenExpired, "Unregistered"));

        this.Sent.Add(registration.DeviceToken);

        var updated = this.RotateTokens.TryGetValue(registration.DeviceToken, out var nt) ? nt : null;
        return Task.FromResult(PushDeliveryResult.Success(registration, updated));
    }
}


/// <summary>A provider bound to a single AppId, for multi-key routing tests.</summary>
public sealed class KeyedTestProvider : IPushProvider
{
    readonly string appId;
    public KeyedTestProvider(string appId) => this.appId = appId;

    public ConcurrentBag<string> Sent { get; } = [];

    public string Identifier => $"test:{this.appId}";

    public bool CanDeliver(DeviceRegistration registration)
        => string.Equals(registration.AppId ?? string.Empty, this.appId, StringComparison.Ordinal);

    public Task<PushDeliveryResult> Send(PushNotification notification, DeviceRegistration registration, CancellationToken cancellationToken = default)
    {
        this.Sent.Add(registration.DeviceToken);
        return Task.FromResult(PushDeliveryResult.Success(registration));
    }
}


/// <summary>
/// An Android provider that supports batching. Records the size of each <see cref="SendBatch"/> call and
/// how many single <see cref="Send"/> calls it received, so tests can assert which path the manager took.
/// </summary>
public sealed class FakeBatchProvider : IPushBatchProvider
{
    public int MaxBatchSize { get; set; } = 500;
    public ConcurrentBag<int> BatchSizes { get; } = [];
    public int SingleSends;
    public HashSet<string> ExpireTokens { get; } = new(StringComparer.Ordinal);
    public ConcurrentBag<string?> TitlesSeen { get; } = [];

    public string Identifier => "fakebatch";
    public bool CanDeliver(DeviceRegistration registration) => registration.Platform == DevicePlatform.Android;

    public Task<PushDeliveryResult> Send(PushNotification notification, DeviceRegistration registration, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref this.SingleSends);
        this.TitlesSeen.Add(notification.Title);
        return Task.FromResult(this.Result(registration));
    }

    public Task<IReadOnlyList<PushDeliveryResult>> SendBatch(PushNotification notification, IReadOnlyList<DeviceRegistration> registrations, CancellationToken cancellationToken = default)
    {
        this.BatchSizes.Add(registrations.Count);
        this.TitlesSeen.Add(notification.Title);
        IReadOnlyList<PushDeliveryResult> results = registrations.Select(this.Result).ToList();
        return Task.FromResult(results);
    }

    PushDeliveryResult Result(DeviceRegistration registration)
        => this.ExpireTokens.Contains(registration.DeviceToken)
            ? PushDeliveryResult.Failed(registration, PushDeliveryStatus.TokenExpired, "Unregistered")
            : PushDeliveryResult.Success(registration);
}


/// <summary>Skips the device whose token is "skipme" and rewrites the title of everything else.</summary>
public sealed class SkipAndRewriteInterceptor : IPushInterceptor
{
    public Task<InterceptorResult> BeforeSend(PushSendContext context, CancellationToken cancellationToken = default)
    {
        if (context.Registration.DeviceToken == "skipme")
            return Task.FromResult(InterceptorResult.Skip);

        context.Notification = context.Notification with { Title = "rewritten" };
        return Task.FromResult(InterceptorResult.Continue);
    }

    public Task OnSent(PushSendContext context, PushDeliveryResult result, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task OnFailed(PushSendContext context, PushDeliveryResult result, CancellationToken cancellationToken = default) => Task.CompletedTask;
}
