namespace Shiny.Extensions.Push.DocumentDb;


/// <summary>Tuning for the DocumentDb-backed repository.</summary>
public class DocumentDbOptions
{
    /// <summary>
    /// When true (default), equality clauses on <c>UserIdentifier</c>/<c>AppId</c> are pushed into the
    /// store query so reads don't scan the whole type; the remaining clauses (tags, topics, platforms,
    /// tokens, environment) are still evaluated in-process.
    /// <para>
    /// Pushdown maps the document's CLR property names to JSON paths via the store's serializer, so it is
    /// only correct when the store uses the document's serialized naming (the default PascalCase, which is
    /// what this library's source-gen context emits). If you configure a custom naming policy on the
    /// store, set this to <c>false</c>.
    /// </para>
    /// </summary>
    public bool QueryPushdown { get; set; } = true;
}
