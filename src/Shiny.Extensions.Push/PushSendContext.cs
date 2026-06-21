namespace Shiny.Extensions.Push;


/// <summary>
/// Per-device context passed through the interceptor pipeline. The <see cref="Notification"/> is
/// mutable so interceptors can localize/personalize it (records are swapped with <c>with</c>):
/// <code>context.Notification = context.Notification with { Title = localizedTitle };</code>
/// </summary>
public sealed class PushSendContext(Guid batchId, DeviceRegistration registration, PushNotification notification)
{
    /// <summary>Correlation id for the whole send batch.</summary>
    public Guid BatchId { get; } = batchId;

    /// <summary>The target registration. Immutable within a send.</summary>
    public DeviceRegistration Registration { get; } = registration;

    /// <summary>The notification to deliver. Interceptors may replace this before send.</summary>
    public PushNotification Notification { get; set; } = notification;

    /// <summary>An optional per-device property bag interceptors can use to pass state between hooks.</summary>
    public IDictionary<string, object> Items { get; } = new Dictionary<string, object>();
}


/// <summary>The result of an interceptor's <see cref="IPushInterceptor.BeforeSend"/> hook.</summary>
public readonly record struct InterceptorResult(InterceptorDecision Decision)
{
    public static InterceptorResult Continue { get; } = new(InterceptorDecision.Continue);
    public static InterceptorResult Skip { get; } = new(InterceptorDecision.Skip);
}
