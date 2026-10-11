using Microsoft.Extensions.Options;
using Microsoft.Graph.Models;
using Microsoft.Kiota.Abstractions;
using Microsoft.Kiota.Abstractions.Serialization;

using MockGraph.Server.Auth;
using MockGraph.Server.Mapping;
using MockGraph.Server.OData;
using MockGraph.Server.Options;
using MockGraph.Server.Serialization;
using MockGraph.Server.State;

using static MockGraph.Server.Endpoints.GraphResults;

namespace MockGraph.Server.Endpoints;

public static class DirectoryEndpoints
{
    private const string UsersContext = "users";
    private const string GroupsContext = "groups";
    private const string ObjectsContext = "directoryObjects";

    private static readonly string[] UserFilterable = ["mail", "userPrincipalName", "displayName", "id"];
    private static readonly string[] UserSortable = ["displayName", "userPrincipalName"];
    private static readonly string[] GroupFilterable = ["displayName", "mail", "id"];
    private static readonly string[] GroupSortable = ["displayName"];

    private static string? UserProperty(UserRecord u, string name) => name switch
    {
        "mail" or "userPrincipalName" => u.UserPrincipalName,
        "displayName" => u.DisplayName,
        "id" => u.Id,
        _ => null,
    };

    // The mock's groups are security groups without a mailbox, so `mail` is null and never matches.
    private static string? GroupProperty(GroupRecord g, string name) => name switch
    {
        "displayName" => g.DisplayName,
        "id" => g.Id,
        _ => null,
    };

    public static IEndpointRouteBuilder MapDirectoryEndpoints(this IEndpointRouteBuilder app)
    {
        var v1 = app.MapGroup("/v1.0");

        v1.MapGet("/users", (HttpContext ctx, GraphStore store, IOptions<MockGraphOptions> o) =>
            Collection(ctx, store, o.Value, store.Users, UsersContext, (items, next) => new UserCollectionResponse
            {
                Value = [.. items.Select(u => DirectoryMapper.ToUser(u))],
                OdataNextLink = next,
            },
            DirectoryQuery.Options,
            (q, all) => DirectoryQuery.Refine(ctx.Request, q, all, "users", UserFilterable, UserSortable, UserProperty)));
        v1.MapGet("/users/{id}", (string id, HttpContext ctx, GraphStore store, IOptions<MockGraphOptions> o) =>
            Entity(ctx, o.Value, FindUser(store, id), DirectoryMapper.ToUser, $"{UsersContext}/$entity"));
        v1.MapGet("/users/{id}/memberOf", (string id, HttpContext ctx, GraphStore store, IOptions<MockGraphOptions> o) =>
            Objects(ctx, store, o.Value, store.MemberOf(FindUser(store, id))));
        v1.MapGet("/users/{id}/transitiveMemberOf", (string id, HttpContext ctx, GraphStore store, IOptions<MockGraphOptions> o) =>
            Objects(ctx, store, o.Value, store.TransitiveMemberOf(FindUser(store, id))));

        v1.MapGet("/me", (HttpContext ctx, GraphStore store, IOptions<MockGraphOptions> o) =>
            Entity(ctx, o.Value, Me(ctx), DirectoryMapper.ToUser, $"{UsersContext}/$entity"));
        v1.MapGet("/me/memberOf", (HttpContext ctx, GraphStore store, IOptions<MockGraphOptions> o) =>
            Objects(ctx, store, o.Value, store.MemberOf(Me(ctx))));
        v1.MapGet("/me/transitiveMemberOf", (HttpContext ctx, GraphStore store, IOptions<MockGraphOptions> o) =>
            Objects(ctx, store, o.Value, store.TransitiveMemberOf(Me(ctx))));

        v1.MapGet("/groups", (HttpContext ctx, GraphStore store, IOptions<MockGraphOptions> o) =>
            Collection(ctx, store, o.Value, store.Groups, GroupsContext, (items, next) => new GroupCollectionResponse
            {
                Value = [.. items.Select(g => DirectoryMapper.ToGroup(g))],
                OdataNextLink = next,
            },
            DirectoryQuery.Options,
            (q, all) => DirectoryQuery.Refine(ctx.Request, q, all, "groups", GroupFilterable, GroupSortable, GroupProperty)));
        v1.MapGet("/groups/{id}", (string id, HttpContext ctx, GraphStore store, IOptions<MockGraphOptions> o) =>
            Entity(ctx, o.Value, FindGroup(store, id), DirectoryMapper.ToGroup, $"{GroupsContext}/$entity"));
        v1.MapGet("/groups/{id}/members", (string id, HttpContext ctx, GraphStore store, IOptions<MockGraphOptions> o) =>
            Objects(ctx, store, o.Value, store.Members(FindGroup(store, id))));
        v1.MapGet("/groups/{id}/transitiveMembers", (string id, HttpContext ctx, GraphStore store, IOptions<MockGraphOptions> o) =>
            Objects(ctx, store, o.Value, store.TransitiveMembers(FindGroup(store, id))));

        app.MapFallback("/v1.0/{**path}", (HttpContext ctx) => ctx.Request.Method == HttpMethods.Get
            ? throw new GraphException(StatusCodes.Status404NotFound, "Request_ResourceNotFound", $"Resource not found for the segment '{ctx.Request.Path.Value?["/v1.0/".Length..].Split('/')[0]}'.")
            : throw new GraphException(StatusCodes.Status405MethodNotAllowed, "BadRequest", $"{ctx.Request.Method} is not supported by this server. Mutations go through /_admin."));

        return app;
    }

    private static UserRecord Me(HttpContext ctx) => ctx.GetCaller() switch
    {
        UserCaller caller => caller.User,
        _ => throw GraphException.BadRequest("/me request is only valid with delegated authentication flow."),
    };

    private static UserRecord FindUser(GraphStore store, string id) =>
        store.FindUser(id) ?? throw GraphException.ResourceNotFound(id);

    private static GroupRecord FindGroup(GraphStore store, string id) =>
        store.FindGroup(id) ?? throw GraphException.ResourceNotFound(id);

    private static IResult Objects(HttpContext ctx, GraphStore store, MockGraphOptions options, IReadOnlyList<DirectoryEntry> entries) =>
        Collection(ctx, store, options, entries, ObjectsContext, (items, next) => new DirectoryObjectCollectionResponse
        {
            Value = [.. items.Select(DirectoryMapper.ToDirectoryObject)],
            OdataNextLink = next,
        });

    private static IResult Objects(HttpContext ctx, GraphStore store, MockGraphOptions options, IReadOnlyList<GroupRecord> groups) =>
        Objects(ctx, store, options, [.. groups.Cast<DirectoryEntry>()]);
}