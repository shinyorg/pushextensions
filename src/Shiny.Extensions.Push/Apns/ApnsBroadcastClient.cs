using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Shiny.Extensions.Push;

namespace Shiny.Extensions.Push.Apns;


/// <summary>Whether APNs stores the most recent broadcast for devices that subscribe later.</summary>
public enum ApnsChannelStoragePolicy
{
    /// <summary>Do not store the last broadcast.</summary>
    NoStorage = 0,

    /// <summary>Store the most recent broadcast so a device joining the channel receives it.</summary>
    Store = 1
}


/// <summary>The outcome of a broadcast channel operation.</summary>
/// <param name="Success">Whether APNs accepted the request.</param>
/// <param name="StatusCode">The HTTP status APNs returned.</param>
/// <param name="ChannelId">The channel id, for create/read operations.</param>
/// <param name="ApnsId">The <c>apns-id</c> of an accepted broadcast.</param>
/// <param name="Reason">Apple's failure reason, when the request was rejected.</param>
public record ApnsBroadcastResult(
    bool Success,
    HttpStatusCode StatusCode,
    string? ChannelId = null,
    string? ApnsId = null,
    string? Reason = null
);


/// <summary>
/// Manages APNs broadcast channels and sends channel-wide Live Activity pushes (iOS 18+).
/// </summary>
/// <remarks>
/// Broadcast solves the one-to-many Live Activity: rather than tracking a token per activity and sending
/// N pushes, devices subscribe to a channel (the app passes the channel id to ActivityKit) and one push
/// updates every subscriber. This is the shape sports scores and transit arrivals want.
/// <para>
/// Resolve it from DI keyed by the same app key as the APNs provider (or unkeyed for the default
/// registration). Channel management runs against Apple's separate management host on port 2196; the
/// broadcast itself goes to the ordinary APNs host.
/// </para>
/// </remarks>
public sealed class ApnsBroadcastClient(
    string appKey,
    IHttpClientFactory httpClientFactory,
    ApnsJwtProvider jwt,
    IOptionsMonitor<ApnsOptions> optionsMonitor
)
{
    const string ManageProductionHost = "https://api-manage-broadcast.push.apple.com:2196";
    const string ManageSandboxHost = "https://api-manage-broadcast.sandbox.push.apple.com:2196";
    const string ProductionHost = "https://api.push.apple.com";
    const string SandboxHost = "https://api.sandbox.push.apple.com";

    readonly ApnsOptions options = optionsMonitor.Get(appKey ?? string.Empty);


    /// <summary>
    /// Creates a broadcast channel. The returned <see cref="ApnsBroadcastResult.ChannelId"/> is what the
    /// app hands to ActivityKit when starting an activity that should follow this channel.
    /// </summary>
    /// <param name="storagePolicy">Whether APNs retains the last broadcast for late subscribers.</param>
    /// <param name="environment">Sandbox or production. Channels are environment-specific.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    public async Task<ApnsBroadcastResult> CreateChannel(
        ApnsChannelStoragePolicy storagePolicy = ApnsChannelStoragePolicy.Store,
        PushEnvironment environment = PushEnvironment.Production,
        CancellationToken cancellationToken = default
    )
    {
        var body = $$"""{"push-type":"LiveActivity","message-storage-policy":{{(int)storagePolicy}}}""";

        using var request = this.Build(HttpMethod.Post, $"{ManageHost(environment)}/1/apps/{this.options.BundleId}");
        request.Content = new StringContent(body);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

        return await this.Execute(request, cancellationToken).ConfigureAwait(false);
    }


    /// <summary>Reads a single channel, to confirm it still exists.</summary>
    /// <param name="channelId">The channel id returned by <see cref="CreateChannel"/>.</param>
    /// <param name="environment">Sandbox or production.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    public async Task<ApnsBroadcastResult> ReadChannel(
        string channelId,
        PushEnvironment environment = PushEnvironment.Production,
        CancellationToken cancellationToken = default
    )
    {
        using var request = this.Build(HttpMethod.Get, $"{ManageHost(environment)}/1/apps/{this.options.BundleId}");
        request.Headers.TryAddWithoutValidation("apns-channel-id", channelId);

        return await this.Execute(request, cancellationToken).ConfigureAwait(false);
    }


    /// <summary>Lists every channel currently registered for this app.</summary>
    /// <param name="environment">Sandbox or production.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    public async Task<IReadOnlyList<string>> GetAllChannels(
        PushEnvironment environment = PushEnvironment.Production,
        CancellationToken cancellationToken = default
    )
    {
        using var request = this.Build(HttpMethod.Get, $"{ManageHost(environment)}/1/apps/{this.options.BundleId}/all-channels");
        using var response = await this.Send(request, cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
            return [];

        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return ParseChannels(body);
    }


    /// <summary>Deletes a channel. Activities already following it stop receiving broadcasts.</summary>
    /// <param name="channelId">The channel to delete.</param>
    /// <param name="environment">Sandbox or production.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    public async Task<ApnsBroadcastResult> DeleteChannel(
        string channelId,
        PushEnvironment environment = PushEnvironment.Production,
        CancellationToken cancellationToken = default
    )
    {
        using var request = this.Build(HttpMethod.Delete, $"{ManageHost(environment)}/1/apps/{this.options.BundleId}");
        request.Headers.TryAddWithoutValidation("apns-channel-id", channelId);

        return await this.Execute(request, cancellationToken).ConfigureAwait(false);
    }


    /// <summary>
    /// Broadcasts one Live Activity push to every device subscribed to <paramref name="channelId"/>.
    /// </summary>
    /// <param name="channelId">The target channel.</param>
    /// <param name="notification">A notification carrying <see cref="ApplePushOptions.LiveActivity"/>.</param>
    /// <param name="environment">Sandbox or production.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    public async Task<ApnsBroadcastResult> Broadcast(
        string channelId,
        PushNotification notification,
        PushEnvironment environment = PushEnvironment.Production,
        CancellationToken cancellationToken = default
    )
    {
        if (notification.Apple?.LiveActivity is null)
            throw new ArgumentException("Broadcast requires a Live Activity notification — use LiveActivityPush.Update/End", nameof(notification));

        var host = environment == PushEnvironment.Sandbox ? SandboxHost : ProductionHost;

        using var request = this.Build(HttpMethod.Post, $"{host}/4/broadcasts/apps/{this.options.BundleId}");
        request.Headers.TryAddWithoutValidation("apns-channel-id", channelId);
        request.Headers.TryAddWithoutValidation("apns-push-type", "liveactivity");
        request.Headers.TryAddWithoutValidation("apns-topic", $"{this.options.BundleId}.push-type.liveactivity");
        request.Headers.TryAddWithoutValidation("apns-priority", notification.Priority == PushPriority.Normal ? "5" : "10");
        request.Content = new ByteArrayContent(ApnsPayloadBuilder.Build(notification));
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

        return await this.Execute(request, cancellationToken).ConfigureAwait(false);
    }


    static string ManageHost(PushEnvironment environment)
        => environment == PushEnvironment.Sandbox ? ManageSandboxHost : ManageProductionHost;


    HttpRequestMessage Build(HttpMethod method, string uri)
    {
        var request = new HttpRequestMessage(method, uri)
        {
            Version = HttpVersion.Version20,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact
        };
        request.Headers.TryAddWithoutValidation("authorization", $"bearer {jwt.GetToken()}");
        return request;
    }


    Task<HttpResponseMessage> Send(HttpRequestMessage request, CancellationToken cancellationToken)
        => httpClientFactory.CreateClient(ApnsProvider.HttpClientName).SendAsync(request, cancellationToken);


    async Task<ApnsBroadcastResult> Execute(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        using var response = await this.Send(request, cancellationToken).ConfigureAwait(false);
        var channelId = response.Headers.TryGetValues("apns-channel-id", out var channels) ? channels.FirstOrDefault() : null;
        var apnsId = response.Headers.TryGetValues("apns-id", out var ids) ? ids.FirstOrDefault() : null;

        if (response.IsSuccessStatusCode)
            return new ApnsBroadcastResult(true, response.StatusCode, channelId, apnsId);

        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var reason = ParseReason(body);

        if (reason is "ExpiredProviderToken" or "InvalidProviderToken" or "MissingProviderToken")
            jwt.Invalidate();

        return new ApnsBroadcastResult(false, response.StatusCode, channelId, apnsId, reason ?? response.StatusCode.ToString());
    }


    static string? ParseReason(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.TryGetProperty("reason", out var r) ? r.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }


    static IReadOnlyList<string> ParseChannels(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return [];
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("channels", out var channels) || channels.ValueKind != JsonValueKind.Array)
                return [];

            var results = new List<string>(channels.GetArrayLength());
            foreach (var item in channels.EnumerateArray())
            {
                if (item.GetString() is { } id)
                    results.Add(id);
            }
            return results;
        }
        catch (JsonException)
        {
            return [];
        }
    }
}
