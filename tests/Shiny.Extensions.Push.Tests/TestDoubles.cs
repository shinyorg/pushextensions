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
