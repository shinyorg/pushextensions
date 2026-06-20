using Microsoft.Extensions.DependencyInjection;
using Shiny.DocumentDb;
using Shiny.Extensions.Push;

namespace Shiny.Extensions.Push.DocumentDb;


public static class DocumentDbRegistration
{
    /// <summary>
    /// Uses a Shiny.DocumentDb-backed repository. Assumes an <see cref="IDocumentStore"/> has already
    /// been registered (e.g. via <c>services.AddDocumentStore(…)</c> with your chosen provider).
    /// </summary>
    public static IPushBuilder UseDocumentDb(this IPushBuilder builder)
    {
        builder.UseRepository<DocumentDbPushRepository>();
        return builder;
    }


    /// <summary>
    /// Registers a document store with the supplied options and uses it as the push repository. Set
    /// <see cref="DocumentStoreOptions.DatabaseProvider"/> to pick the backend (SQLite, Postgres, …).
    /// </summary>
    public static IPushBuilder UseDocumentDb(this IPushBuilder builder, Action<DocumentStoreOptions> configureStore)
    {
        builder.Services.AddDocumentStore(configureStore);
        builder.UseRepository<DocumentDbPushRepository>();
        return builder;
    }
}
