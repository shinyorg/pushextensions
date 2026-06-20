namespace Shiny.Extensions.Push;


/// <summary>
/// The normalized outcome of delivering one notification to one device. Providers return this; the
/// manager acts on it (pruning, token updates) and rolls it into a <see cref="PushSendResult"/>.
/// </summary>
public record PushDeliveryResult
{
    public required string DeviceToken { get; init; }
    public required DevicePlatform Platform { get; init; }
    public required PushDeliveryStatus Status { get; init; }

    /// <summary>
    /// A replacement token reported by the provider (e.g. FCM canonical id). When present, the manager
    /// upserts it via <see cref="IPushRepository.UpdateToken"/>.
    /// </summary>
    public string? UpdatedToken { get; init; }

    /// <summary>Provider-specific reason code/string (e.g. APNs "BadDeviceToken"). Useful for logging.</summary>
    public string? Reason { get; init; }

    /// <summary>
    /// Provider-assigned message identifier for the accepted push (APNs <c>apns-id</c>, FCM message
    /// <c>name</c>). Useful for correlation/receipts. Null on failure or when the provider returns none.
    /// </summary>
    public string? ProviderMessageId { get; init; }

    /// <summary>The transport exception, when the failure was an exception rather than a status.</summary>
    public Exception? Error { get; init; }

    public bool IsSuccess => this.Status == PushDeliveryStatus.Success;

    public static PushDeliveryResult Success(DeviceRegistration r, string? updatedToken = null, string? providerMessageId = null) =>
        new() { DeviceToken = r.DeviceToken, Platform = r.Platform, Status = PushDeliveryStatus.Success, UpdatedToken = updatedToken, ProviderMessageId = providerMessageId };

    public static PushDeliveryResult Failed(DeviceRegistration r, PushDeliveryStatus status, string? reason = null, Exception? error = null) =>
        new() { DeviceToken = r.DeviceToken, Platform = r.Platform, Status = status, Reason = reason, Error = error };
}


/// <summary>
/// The aggregate result of a send across an audience. The <see cref="BatchId"/> correlates this send
/// across logs, interceptors and (eventually) delivery receipts.
/// </summary>
public record PushSendResult
{
    public required Guid BatchId { get; init; }
    public required IReadOnlyList<PushDeliveryResult> Results { get; init; }

    public int Total => this.Results.Count;
    public int Sent => this.Results.Count(r => r.Status == PushDeliveryStatus.Success);
    public int Failed => this.Results.Count(r => r.Status is PushDeliveryStatus.Error or PushDeliveryStatus.RateLimited or PushDeliveryStatus.NoProvider);
    public int TokensRemoved => this.Results.Count(r => r.Status is PushDeliveryStatus.TokenExpired or PushDeliveryStatus.InvalidToken);
    public int Skipped => this.Results.Count(r => r.Status == PushDeliveryStatus.Skipped);

    public static PushSendResult Empty(Guid batchId) =>
        new() { BatchId = batchId, Results = [] };
}
