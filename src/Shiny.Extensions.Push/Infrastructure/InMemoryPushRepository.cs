using System.Collections.Concurrent;
using System.Runtime.CompilerServices;

using Shiny.Extensions.Push;

namespace Shiny.Extensions.Push.Infrastructure;


/// <summary>
/// A thread-safe, process-local registration store. Intended for tests, samples and single-node
/// development — replace with a durable <see cref="IPushRepository"/> (e.g. DocumentDB) in production.
/// </summary>
public sealed class InMemoryPushRepository : IPushRepository
{
    readonly ConcurrentDictionary<string, DeviceRegistration> store = new(StringComparer.Ordinal);


    static string Key(string deviceToken, DevicePlatform platform) => $"{platform}::{deviceToken}";

    // Identity key: prefer the stable DeviceId, fall back to token+platform.
    static string IdentityKey(DeviceRegistration r) =>
        r.DeviceId is { Length: > 0 } id ? $"id::{r.Platform}::{id}" : Key(r.DeviceToken, r.Platform);


    public Task Save(DeviceRegistration registration, CancellationToken cancellationToken = default)
    {
        var identity = IdentityKey(registration);

        // If keyed by DeviceId and the token rotated, drop any stale token-keyed row for the same device.
        if (registration.DeviceId is { Length: > 0 })
        {
            foreach (var kvp in this.store)
            {
                if (kvp.Value.DeviceId == registration.DeviceId &&
                    kvp.Value.Platform == registration.Platform &&
                    kvp.Key != identity)
                {
                    this.store.TryRemove(kvp.Key, out _);
                }
            }
        }

        this.store[identity] = registration;
        return Task.CompletedTask;
    }


    public Task<bool> Remove(string deviceToken, DevicePlatform platform, CancellationToken cancellationToken = default)
    {
        // Token may be stored under an id-key, so scan by value.
        var removed = false;
        foreach (var kvp in this.store)
        {
            if (kvp.Value.Platform == platform &&
                string.Equals(kvp.Value.DeviceToken, deviceToken, StringComparison.Ordinal))
            {
                removed |= this.store.TryRemove(kvp.Key, out _);
            }
        }
        return Task.FromResult(removed);
    }


    public Task UpdateToken(string oldToken, DevicePlatform platform, string newToken, CancellationToken cancellationToken = default)
    {
        foreach (var kvp in this.store)
        {
            if (kvp.Value.Platform == platform &&
                string.Equals(kvp.Value.DeviceToken, oldToken, StringComparison.Ordinal))
            {
                var updated = kvp.Value with { DeviceToken = newToken };
                this.store[kvp.Key] = updated;
            }
        }
        return Task.CompletedTask;
    }


    public Task Subscribe(string deviceToken, DevicePlatform platform, string topic, CancellationToken cancellationToken = default)
    {
        foreach (var kvp in this.store)
        {
            var reg = kvp.Value;
            if (reg.Platform == platform &&
                string.Equals(reg.DeviceToken, deviceToken, StringComparison.Ordinal) &&
                !reg.Topics.Contains(topic))
            {
                this.store[kvp.Key] = reg with { Topics = [.. reg.Topics, topic] };
            }
        }
        return Task.CompletedTask;
    }


    public Task Unsubscribe(string deviceToken, DevicePlatform platform, string topic, CancellationToken cancellationToken = default)
    {
        foreach (var kvp in this.store)
        {
            var reg = kvp.Value;
            if (reg.Platform == platform &&
                string.Equals(reg.DeviceToken, deviceToken, StringComparison.Ordinal) &&
                reg.Topics.Contains(topic))
            {
                this.store[kvp.Key] = reg with { Topics = [.. reg.Topics.Where(t => t != topic)] };
            }
        }
        return Task.CompletedTask;
    }


    public Task<IReadOnlyList<DeviceRegistration>> GetRegistrations(PushFilter filter, CancellationToken cancellationToken = default)
    {
        IReadOnlyList<DeviceRegistration> matches = this.store.Values
            .Where(filter.Matches)
            .ToList();
        return Task.FromResult(matches);
    }


    public async IAsyncEnumerable<DeviceRegistration> StreamRegistrations(
        PushFilter filter,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        foreach (var reg in this.store.Values)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (filter.Matches(reg))
                yield return reg;
        }
        await Task.CompletedTask;
    }


    /// <summary>Test/diagnostic helper: current registration count.</summary>
    public int Count => this.store.Count;
}
