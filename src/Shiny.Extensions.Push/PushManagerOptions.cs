namespace Shiny.Extensions.Push;


/// <summary>Tuning knobs for the built-in <see cref="Infrastructure.PushManager"/>.</summary>
public class PushManagerOptions
{
    /// <summary>
    /// Maximum number of devices delivered to concurrently within a single send. APNs multiplexes fine
    /// over HTTP/2, so a modest fan-out is safe. Set to 1 for repositories/providers that are not
    /// thread-safe.
    /// </summary>
    public int MaxDegreeOfParallelism { get; set; } = 10;

    /// <summary>
    /// When true (default), tokens reported expired/invalid by a provider are removed from the
    /// repository automatically. Turn off if you want to manage pruning yourself via an interceptor.
    /// </summary>
    public bool AutoPruneDeadTokens { get; set; } = true;

    /// <summary>
    /// When true (default), the manager hands groups of devices to providers that implement
    /// <see cref="IPushBatchProvider"/> in a single provider-specific batched call instead of one request per
    /// device. Devices are grouped per provider by the identical notification instance, so an interceptor
    /// that replaces the notification per device naturally falls back to per-device sends. Turn off to force
    /// per-device delivery for every provider.
    /// </summary>
    public bool EnableBatching { get; set; } = true;
}
