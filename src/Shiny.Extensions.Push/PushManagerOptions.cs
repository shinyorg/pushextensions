namespace Shiny.Extensions.Push;


/// <summary>Tuning knobs for the built-in <see cref="PushManager"/>.</summary>
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
}
