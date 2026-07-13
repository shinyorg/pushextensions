using System.Collections.Concurrent;

namespace Shiny.Extensions.Push.Infrastructure;


/// <summary>
/// Reuses a minted credential (an APNs JWT, an FCM/WNS OAuth bearer, a Web Push VAPID header) per cache key
/// so the hot path doesn't re-sign / re-exchange on every send — APNs in particular rate-limits token
/// generation. Entries refresh on the transport's own token lifetime; the mint delegate re-reads whatever
/// the tenant store currently returns, so a rotated key is picked up at the next refresh.
/// </summary>
/// <remarks>
/// Deliberately <b>not</b> a managed cache: there is no eviction, size bound, or versioning. The key set is
/// the server's tenant set (bounded by the business) and each entry is a small string + timestamp, so it is
/// left to grow. Thread-safe: a per-key gate serializes minting so a burst of concurrent sends for a cold
/// tenant mints once.
/// </remarks>
sealed class CredentialTokenCache
{
    sealed class Entry
    {
        public readonly SemaphoreSlim Gate = new(1, 1);
        public string? Token;
        public DateTimeOffset ExpiresAt;
    }

    readonly ConcurrentDictionary<string, Entry> entries = new(StringComparer.Ordinal);


    /// <summary>
    /// Return the cached credential for <paramref name="key"/>, minting a fresh one via <paramref name="mint"/>
    /// when it is missing or within <paramref name="skew"/> of expiry. <paramref name="mint"/> returns the
    /// credential and its lifetime in seconds.
    /// </summary>
    public async ValueTask<string> Get(
        string key,
        TimeSpan skew,
        Func<CancellationToken, ValueTask<(string Token, int LifetimeSeconds)>> mint,
        CancellationToken cancellationToken = default
    )
    {
        var entry = entries.GetOrAdd(key, static _ => new Entry());

        if (entry.Token is { } fast && DateTimeOffset.UtcNow < entry.ExpiresAt - skew)
            return fast;

        await entry.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (entry.Token is { } current && DateTimeOffset.UtcNow < entry.ExpiresAt - skew)
                return current;

            var (token, lifetime) = await mint(cancellationToken).ConfigureAwait(false);
            entry.Token = token;
            entry.ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(lifetime);
            return token;
        }
        finally
        {
            entry.Gate.Release();
        }
    }


    /// <summary>Drop the cached credential for <paramref name="key"/> so the next <see cref="Get"/> re-mints.</summary>
    public void Invalidate(string key)
    {
        if (entries.TryGetValue(key, out var entry))
            entry.Token = null;
    }
}
