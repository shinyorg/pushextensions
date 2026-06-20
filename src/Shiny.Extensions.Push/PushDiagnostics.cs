using System.Diagnostics;

namespace Shiny.Extensions.Push;


/// <summary>
/// Distributed-tracing source for the push pipeline. Subscribe from OpenTelemetry with
/// <c>AddSource(PushDiagnostics.ActivitySourceName)</c>. Emits a <c>push.send</c> span per batch and a
/// <c>push.deliver</c> span per device. Zero-cost when nothing is listening.
/// </summary>
public static class PushDiagnostics
{
    public const string ActivitySourceName = "Shiny.Extensions.Push";

    internal static readonly ActivitySource Source = new(ActivitySourceName);
}
