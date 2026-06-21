using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;

namespace Shiny.Extensions.Push;


/// <summary>
/// A transport that delivers to one device. One provider per transport (APNs, FCM, WebPush, WNS).
/// The manager groups registrations by the provider that claims them via <see cref="CanDeliver"/>.
/// Implementations must be thread-safe — the manager may invoke <see cref="Send"/> concurrently.
/// </summary>
public interface IPushProvider
{
    /// <summary>Stable identifier, e.g. "apns", "fcm", "webpush".</summary>
    string Identifier { get; }

    /// <summary>Whether this provider can deliver to the given registration (typically a platform check).</summary>
    bool CanDeliver(DeviceRegistration registration);

    /// <summary>Deliver a single notification to a single device, returning a normalized outcome.</summary>
    Task<PushDeliveryResult> Send(PushNotification notification, DeviceRegistration registration, CancellationToken cancellationToken = default);
}


/// <summary>
/// An optional capability for transports that can deliver one notification to many devices in a single
/// operation (e.g. FCM's multipart <c>/batch</c> endpoint). When batching is enabled
/// (<see cref="PushManagerOptions.EnableBatching"/>) the manager groups devices that share the same
/// (post-interceptor) notification and hands each group to <see cref="SendBatch"/> instead of calling
/// <see cref="IPushProvider.Send"/> per device. Implementations must be thread-safe.
/// </summary>
public interface IPushBatchProvider : IPushProvider
{
    /// <summary>
    /// The maximum number of registrations accepted per <see cref="SendBatch"/> call. The manager splits
    /// larger groups into batches of this size. Must be ≥ 1 (FCM HTTP v1 caps a batch at 500).
    /// </summary>
    int MaxBatchSize { get; }

    /// <summary>
    /// Deliver one notification to a batch of devices. Returns exactly one result per registration, in the
    /// same order as <paramref name="registrations"/>. A whole-batch transport failure should be surfaced
    /// as one failed result per registration rather than thrown — the manager treats a thrown exception or
    /// a count mismatch as a failure of the entire batch.
    /// </summary>
    Task<IReadOnlyList<PushDeliveryResult>> SendBatch(PushNotification notification, IReadOnlyList<DeviceRegistration> registrations, CancellationToken cancellationToken = default);
}


/// <summary>
/// Persistence for device registrations. Implementations must be safe for concurrent reads/writes
/// (the manager prunes/updates tokens while iterating). The default in-memory implementation and a
/// DocumentDB implementation both satisfy this.
/// </summary>
public interface IPushRepository
{
    /// <summary>
    /// Upsert a registration. Keyed on <see cref="DeviceRegistration.DeviceId"/> when present, else on
    /// (<see cref="DeviceRegistration.DeviceToken"/>, <see cref="DeviceRegistration.Platform"/>).
    /// </summary>
    Task Save(DeviceRegistration registration, CancellationToken cancellationToken = default);

    /// <summary>Remove a registration by token + platform. Returns whether anything was removed.</summary>
    Task<bool> Remove(string deviceToken, DevicePlatform platform, CancellationToken cancellationToken = default);

    /// <summary>Replace a rotated token in place, preserving the rest of the registration.</summary>
    Task UpdateToken(string oldToken, DevicePlatform platform, string newToken, CancellationToken cancellationToken = default);

    /// <summary>Add a topic subscription to a registration (idempotent). No-op if the device is unknown.</summary>
    Task Subscribe(string deviceToken, DevicePlatform platform, string topic, CancellationToken cancellationToken = default);

    /// <summary>Remove a topic subscription from a registration (idempotent).</summary>
    Task Unsubscribe(string deviceToken, DevicePlatform platform, string topic, CancellationToken cancellationToken = default);

    /// <summary>Materialize all matching registrations. Prefer <see cref="StreamRegistrations"/> for large audiences.</summary>
    Task<IReadOnlyList<DeviceRegistration>> GetRegistrations(PushFilter filter, CancellationToken cancellationToken = default);

    /// <summary>Stream matching registrations so broadcasts never have to buffer the whole table.</summary>
    IAsyncEnumerable<DeviceRegistration> StreamRegistrations(PushFilter filter, CancellationToken cancellationToken = default);
}


/// <summary>
/// A hook into the send pipeline for localization, personalization, suppression, and reporting.
/// Multiple interceptors run in registration order. Any one returning <see cref="InterceptorResult.Skip"/>
/// from <see cref="BeforeSend"/> short-circuits delivery for that device.
/// </summary>
public interface IPushInterceptor
{
    /// <summary>
    /// Inspect/mutate the notification before delivery, or skip this device. Mutate by replacing
    /// <see cref="PushSendContext.Notification"/>.
    /// </summary>
    Task<InterceptorResult> BeforeSend(PushSendContext context, CancellationToken cancellationToken = default);

    /// <summary>Called after a successful delivery (analytics, receipts).</summary>
    Task OnSent(PushSendContext context, PushDeliveryResult result, CancellationToken cancellationToken = default);

    /// <summary>
    /// Called when delivery failed (including token-expired/invalid). A single failure never stops the
    /// batch — log and move on. <paramref name="result"/> carries the reason and any exception.
    /// </summary>
    Task OnFailed(PushSendContext context, PushDeliveryResult result, CancellationToken cancellationToken = default);
}


/// <summary>The primary entry point applications use to register devices and send notifications.</summary>
public interface IPushManager
{
    Task RegisterDevice(DeviceRegistration registration, CancellationToken cancellationToken = default);
    Task UnregisterDevice(string deviceToken, DevicePlatform platform, CancellationToken cancellationToken = default);

    /// <summary>Subscribe a device to a topic for later <see cref="SendToTopic"/> fan-out.</summary>
    Task SubscribeToTopic(string deviceToken, DevicePlatform platform, string topic, CancellationToken cancellationToken = default);

    /// <summary>Unsubscribe a device from a topic.</summary>
    Task UnsubscribeFromTopic(string deviceToken, DevicePlatform platform, string topic, CancellationToken cancellationToken = default);

    /// <summary>Send to everyone matching <paramref name="filter"/>.</summary>
    Task<PushSendResult> Send(PushNotification notification, PushFilter filter, CancellationToken cancellationToken = default);

    Task<PushSendResult> SendToUser(string userIdentifier, PushNotification notification, CancellationToken cancellationToken = default);
    Task<PushSendResult> SendToTags(IEnumerable<string> tags, PushNotification notification, TagMatch match = TagMatch.Any, CancellationToken cancellationToken = default);
    Task<PushSendResult> SendToTokens(IEnumerable<string> deviceTokens, PushNotification notification, CancellationToken cancellationToken = default);
    Task<PushSendResult> SendToTopic(string topic, PushNotification notification, CancellationToken cancellationToken = default);
    Task<PushSendResult> Broadcast(PushNotification notification, CancellationToken cancellationToken = default);
}


/// <summary>Fluent configuration surface returned by <c>AddPushNotifications</c>.</summary>
public interface IPushBuilder
{
    IServiceCollection Services { get; }

    /// <summary>Replace the registration store. Defaults to an in-memory store if not called.</summary>
    IPushBuilder UseRepository<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] T>() where T : class, IPushRepository;

    /// <summary>Add a transport provider. Additive — register one per platform you support.</summary>
    IPushBuilder AddProvider<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] T>() where T : class, IPushProvider;

    /// <summary>Add an interceptor. Additive — they run in registration order.</summary>
    IPushBuilder AddInterceptor<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] T>() where T : class, IPushInterceptor;

    /// <summary>Replace the manager implementation. Defaults to the built-in <see cref="Infrastructure.PushManager"/>.</summary>
    IPushBuilder UseManager<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] T>() where T : class, IPushManager;

    /// <summary>Tune dispatch behaviour (concurrency, etc.).</summary>
    IPushBuilder Configure(Action<PushManagerOptions> configure);
}
