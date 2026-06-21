using System.Runtime.CompilerServices;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.Options;
using Shiny.DocumentDb;
using Shiny.Extensions.Push;

namespace Shiny.Extensions.Push.DocumentDb;


/// <summary>
/// An <see cref="IPushRepository"/> backed by Shiny.DocumentDb. Works over any DocumentDb provider
/// (SQLite, Postgres, SQL Server, Cosmos, Mongo, …) the host registers as <see cref="IDocumentStore"/>.
/// </summary>
/// <remarks>
/// Write/lookup hot paths (save, remove, token rotation, subscribe) are O(1) point operations keyed on
/// the document id <c>{Platform}|{DeviceToken}</c>. Reads optionally push equality on
/// <c>UserIdentifier</c>/<c>AppId</c> into the store query (see <see cref="DocumentDbOptions"/>), then
/// evaluate the remaining <see cref="PushFilter"/> clauses in-process — keeping the repository AOT-safe
/// and provider-agnostic. See CLAUDE.md decision 10.
/// </remarks>
public sealed class DocumentDbPushRepository(IDocumentStore store, IOptions<DocumentDbOptions> options) : IPushRepository
{
    static readonly JsonTypeInfo<PushRegistrationDocument> TypeInfo = PushDocumentJsonContext.Default.PushRegistrationDocument;

    readonly DocumentDbOptions options = options.Value;


    public Task Save(DeviceRegistration registration, CancellationToken cancellationToken = default)
        => store.Upsert(PushRegistrationDocument.From(registration), TypeInfo);


    public Task<bool> Remove(string deviceToken, DevicePlatform platform, CancellationToken cancellationToken = default)
        => store.Remove<PushRegistrationDocument>(PushRegistrationDocument.BuildId(platform, deviceToken), cancellationToken);


    public async Task UpdateToken(string oldToken, DevicePlatform platform, string newToken, CancellationToken cancellationToken = default)
    {
        var oldId = PushRegistrationDocument.BuildId(platform, oldToken);
        var existing = await store.Get<PushRegistrationDocument>(oldId, TypeInfo).ConfigureAwait(false);
        if (existing == null)
            return;

        await store.Remove<PushRegistrationDocument>(oldId, cancellationToken).ConfigureAwait(false);
        existing.DeviceToken = newToken;
        existing.Id = PushRegistrationDocument.BuildId(platform, newToken);
        await store.Upsert(existing, TypeInfo).ConfigureAwait(false);
    }


    public async Task Subscribe(string deviceToken, DevicePlatform platform, string topic, CancellationToken cancellationToken = default)
    {
        var id = PushRegistrationDocument.BuildId(platform, deviceToken);
        var doc = await store.Get<PushRegistrationDocument>(id, TypeInfo).ConfigureAwait(false);
        if (doc == null || doc.Topics.Contains(topic))
            return;

        doc.Topics.Add(topic);
        await store.Upsert(doc, TypeInfo).ConfigureAwait(false);
    }


    public async Task Unsubscribe(string deviceToken, DevicePlatform platform, string topic, CancellationToken cancellationToken = default)
    {
        var id = PushRegistrationDocument.BuildId(platform, deviceToken);
        var doc = await store.Get<PushRegistrationDocument>(id, TypeInfo).ConfigureAwait(false);
        if (doc == null || !doc.Topics.Remove(topic))
            return;

        await store.Upsert(doc, TypeInfo).ConfigureAwait(false);
    }


    public async Task<IReadOnlyList<DeviceRegistration>> GetRegistrations(PushFilter filter, CancellationToken cancellationToken = default)
    {
        var results = new List<DeviceRegistration>();
        await foreach (var reg in StreamRegistrations(filter, cancellationToken).ConfigureAwait(false))
            results.Add(reg);
        return results;
    }


    public async IAsyncEnumerable<DeviceRegistration> StreamRegistrations(
        PushFilter filter,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var query = store.Query<PushRegistrationDocument>(TypeInfo);

        // Push down the high-value, translation-safe scalar equalities; the rest is applied in-process.
        if (options.QueryPushdown)
        {
            if (filter.UserIdentifier is { } user)
                query = query.Where(d => d.UserIdentifier == user);

            if (filter.AppId is { } appId)
                query = query.Where(d => d.AppId == appId);
        }

        await foreach (var doc in query.ToAsyncEnumerable().WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            var reg = doc.ToRegistration();
            if (filter.Matches(reg))
                yield return reg;
        }
    }
}
