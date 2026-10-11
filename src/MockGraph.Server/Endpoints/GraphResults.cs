using Microsoft.Kiota.Abstractions;
using Microsoft.Kiota.Abstractions.Serialization;

using MockGraph.Server.OData;
using MockGraph.Server.Options;
using MockGraph.Server.Serialization;
using MockGraph.Server.State;

namespace MockGraph.Server.Endpoints;

/// <summary>Shared helpers that turn a record list or a single model into a Graph-shaped response.</summary>
internal static class GraphResults
{
    public static IResult Collection<TEntry, TResponse>(
        HttpContext ctx,
        GraphStore store,
        MockGraphOptions options,
        IReadOnlyList<TEntry> all,
        string context,
        Func<List<TEntry>, string?, TResponse> build)
        where TResponse : IParsable, IAdditionalDataHolder
    {
        var query = ODataQuery.Parse(ctx.Request, collection: true, options);
        var (items, next) = Paging.Page(all, query, ctx.Request, options, store.Epoch);
        var body = build(items, next);
        body.AdditionalData["@odata.context"] = $"{Paging.BaseUrl(ctx.Request)}/v1.0/$metadata#{context}";
        return new GraphJsonResult(body, query);
    }

    public static IResult Entity<TRecord, TModel>(
        HttpContext ctx, MockGraphOptions options, TRecord record, Func<TRecord, bool, TModel> map, string context)
        where TModel : IParsable, IAdditionalDataHolder =>
        Entity(ctx, options, map(record, false), context);

    public static IResult Entity<TModel>(HttpContext ctx, MockGraphOptions options, TModel body, string context)
        where TModel : IParsable, IAdditionalDataHolder
    {
        var query = ODataQuery.Parse(ctx.Request, collection: false, options);
        body.AdditionalData["@odata.context"] = $"{Paging.BaseUrl(ctx.Request)}/v1.0/$metadata#{context}";
        return new GraphJsonResult(body, query);
    }
}