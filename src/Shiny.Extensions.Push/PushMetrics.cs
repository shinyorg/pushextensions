using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Shiny.Extensions.Push;


/// <summary>
/// Emits push delivery telemetry via <see cref="System.Diagnostics.Metrics"/>. Subscribe with
/// OpenTelemetry (or <c>MetricCollector</c> in tests) against the meter named <see cref="MeterName"/>.
/// </summary>
/// <remarks>
/// Tags are intentionally low-cardinality (platform, provider, status). The per-send <c>BatchId</c> is
/// deliberately NOT a tag — it is unbounded and would explode any metrics backend; it lives on
/// <see cref="PushSendResult"/> and in logs for correlation instead.
/// </remarks>
public sealed class PushMetrics
{
    public const string MeterName = "Shiny.Extensions.Push";

    readonly Counter<long> sent;
    readonly Counter<long> failed;
    readonly Counter<long> pruned;
    readonly Counter<long> skipped;
    readonly Histogram<double> duration;


    public PushMetrics(IMeterFactory meterFactory)
    {
        var meter = meterFactory.Create(MeterName);

        this.sent = meter.CreateCounter<long>(
            "push.notifications.sent", unit: "{notification}", description: "Notifications accepted by a provider.");
        this.failed = meter.CreateCounter<long>(
            "push.notifications.failed", unit: "{notification}", description: "Notifications that failed to send.");
        this.pruned = meter.CreateCounter<long>(
            "push.tokens.pruned", unit: "{token}", description: "Device tokens removed after being reported dead.");
        this.skipped = meter.CreateCounter<long>(
            "push.notifications.skipped", unit: "{notification}", description: "Devices skipped by an interceptor.");
        this.duration = meter.CreateHistogram<double>(
            "push.send.duration", unit: "ms", description: "Per-device provider send latency.");
    }


    public void RecordSent(DevicePlatform platform, string provider, double elapsedMs)
    {
        var tags = Tags(platform, provider, "success");
        this.sent.Add(1, tags);
        this.duration.Record(elapsedMs, tags);
    }


    public void RecordPruned(DevicePlatform platform, string provider, PushDeliveryStatus status, double elapsedMs)
    {
        var tags = Tags(platform, provider, StatusName(status));
        this.pruned.Add(1, tags);
        this.duration.Record(elapsedMs, tags);
    }


    public void RecordFailed(DevicePlatform platform, string provider, PushDeliveryStatus status, double elapsedMs)
    {
        var tags = Tags(platform, provider, StatusName(status));
        this.failed.Add(1, tags);
        this.duration.Record(elapsedMs, tags);
    }


    public void RecordNoProvider(DevicePlatform platform)
        => this.failed.Add(1, Tags(platform, "none", StatusName(PushDeliveryStatus.NoProvider)));


    public void RecordSkipped(DevicePlatform platform, string provider)
        => this.skipped.Add(1, Tags(platform, provider, StatusName(PushDeliveryStatus.Skipped)));


    static TagList Tags(DevicePlatform platform, string provider, string status) => new()
    {
        { "platform", PlatformName(platform) },
        { "provider", provider },
        { "status", status }
    };


    // Switch expressions return interned constants — no per-call ToString() allocation.
    static string PlatformName(DevicePlatform p) => p switch
    {
        DevicePlatform.iOS => "iOS",
        DevicePlatform.MacOS => "MacOS",
        DevicePlatform.Android => "Android",
        DevicePlatform.Windows => "Windows",
        DevicePlatform.WebBrowser => "WebBrowser",
        _ => "Unknown"
    };

    static string StatusName(PushDeliveryStatus s) => s switch
    {
        PushDeliveryStatus.Success => "Success",
        PushDeliveryStatus.TokenExpired => "TokenExpired",
        PushDeliveryStatus.InvalidToken => "InvalidToken",
        PushDeliveryStatus.RateLimited => "RateLimited",
        PushDeliveryStatus.Error => "Error",
        PushDeliveryStatus.NoProvider => "NoProvider",
        PushDeliveryStatus.Skipped => "Skipped",
        _ => "Unknown"
    };
}
