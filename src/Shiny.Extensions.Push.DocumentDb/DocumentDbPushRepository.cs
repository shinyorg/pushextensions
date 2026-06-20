using System.Runtime.CompilerServices;
using System.Text.Json.Serialization.Metadata;
using Shiny.DocumentDb;
using Shiny.Extensions.Push;

namespace Shiny.Extensions.Push.DocumentDb;


/// <summary>
/// An <see cref="IPushRepository"/> backed by Shiny.DocumentDb. Works over any DocumentDb provider
/// (SQLite, Postgres, SQL Server, Cosmos, Mongo, …) the host registers as <see cref="IDocumentStore"/>.
/// </summary>
/// <remarks>
/// Write/lookup hot paths (save, remove, token rotation) are O(1) point operations keyed on the
/// document id <c>{Platform}|{DeviceToken}</c>. Reads (<see cref="GetRegistrations"/> /
/// <see cref="StreamRegistrations"/>) currently stream the type and evaluate <see cref="PushFilter"/>
/// in-process — this keeps the repository fully AOT-safe (no JSON-path predicate translation, so no
/// serializer-naming coupling) and provider-agnostic. Pushing equality clauses (UserIdentifier/AppId)
/// down into the store query is a future optimization; see CLAUDE.md.
/// </remarks>
public sealed class DocumentDbPushRepository : IPushRepository
{
    static readonly JsonTypeInfo<PushRegistrationDocument> TypeInfo = PushDocumentJsonContext.Default.PushRegistrationDocument;

    readonly IDocumentStore store;
    public DocumentDbPushRepository(IDocumentStore store) => this.store = store;


    public Task Save(DeviceRegistration registration, CancellationToken cancellationToken = default)
        => this.store.Upsert(PushRegistrationDocument.From(registration), TypeInfo);


    public Task<bool> Remove(string deviceToken, DevicePlatform platform, CancellationToken cancellationToken = default)
        => this.store.Remove<PushRegistrationDocument>(PushRegistrationDocument.BuildId(platform, deviceToken), cancellationToken);


    public async Task UpdateToken(string oldToken, DevicePlatform platform, string newToken, CancellationToken cancellationToken = default)
    {
        var oldId = PushRegistrationDocument.BuildId(platform, oldToken);
        var existing = await this.store.Get<PushRegistrationDocument>(oldId, TypeInfo).ConfigureAwait(false);
        if (existing == null)
            return;

        await this.store.Remove<PushRegistrationDocument>(oldId, cancellationToken).ConfigureAwait(false);
        existing.DeviceToken = newToken;
        existing.Id = PushRegistrationDocument.BuildId(platform, newToken);
        await this.store.Upsert(existing, TypeInfo).ConfigureAwait(false);
    }


    public async Task<IReadOnlyList<DeviceRegistration>> GetRegistrations(PushFilter filter, CancellationToken cancellationToken = default)
    {
        var results = new List<DeviceRegistration>();
        await foreach (var reg in this.StreamRegistrations(filter, cancellationToken).ConfigureAwait(false))
            results.Add(reg);
        return results;
    }


    public async IAsyncEnumerable<DeviceRegistration> StreamRegistrations(
        PushFilter filter,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var query = this.store.Query<PushRegistrationDocument>(TypeInfo).ToAsyncEnumerable();
        await foreach (var doc in query.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            var reg = doc.ToRegistration();
            if (filter.Matches(reg))
                yield return reg;
        }
    }
}
