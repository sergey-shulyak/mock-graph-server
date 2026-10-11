using Microsoft.Extensions.Options;
using Microsoft.Graph.Models;

using MockGraph.Server.Mapping;
using MockGraph.Server.OData;
using MockGraph.Server.Options;
using MockGraph.Server.Serialization;
using MockGraph.Server.State;

using static MockGraph.Server.Endpoints.GraphResults;

namespace MockGraph.Server.Endpoints;

/// <summary>Drive items, content (302 to /_download) and permissions, with user-scoped visibility.</summary>
public static class DriveEndpoints
{
    private sealed record Target(FileMapper Mapper, DriveRecord Drive, ItemRecord Item);

    public static IEndpointRouteBuilder MapDriveEndpoints(this IEndpointRouteBuilder app)
    {
        var v1 = app.MapGroup("/v1.0/drives/{driveId}");

        v1.MapGet("/root", (string driveId, HttpContext ctx, GraphStore store, IOptions<MockGraphOptions> o) =>
            GetItem(ctx, o.Value, Resolve(ctx, store, driveId, (m, d) => m.RootOf(d))));
        v1.MapGet("/root/children", (string driveId, HttpContext ctx, GraphStore store, IOptions<MockGraphOptions> o) =>
            GetChildren(ctx, store, o.Value, Resolve(ctx, store, driveId, (m, d) => m.RootOf(d))));
        v1.MapGet("/items/{itemId}", (string driveId, string itemId, HttpContext ctx, GraphStore store, IOptions<MockGraphOptions> o) =>
            GetItem(ctx, o.Value, Resolve(ctx, store, driveId, (m, d) => m.FindItem(d, itemId))));
        v1.MapGet("/items/{itemId}/children", (string driveId, string itemId, HttpContext ctx, GraphStore store, IOptions<MockGraphOptions> o) =>
            GetChildren(ctx, store, o.Value, Resolve(ctx, store, driveId, (m, d) => m.FindItem(d, itemId))));
        v1.MapGet("/items/{itemId}/content", (string driveId, string itemId, HttpContext ctx, GraphStore store) =>
            GetContent(ctx, Resolve(ctx, store, driveId, (m, d) => m.FindItem(d, itemId))));
        v1.MapGet("/items/{itemId}/permissions", (string driveId, string itemId, HttpContext ctx, GraphStore store, IOptions<MockGraphOptions> o) =>
            GetPermissions(ctx, store, o.Value, Resolve(ctx, store, driveId, (m, d) => m.FindItem(d, itemId))));

        // Path addressing as used by the SDK's ItemWithPath: root:/a/b.md:, root:/a/b.md:/content, root:/a:/children.
        v1.MapGet("/root:/{**tail}", (string driveId, string tail, HttpContext ctx, GraphStore store, IOptions<MockGraphOptions> o) =>
        {
            var split = tail.LastIndexOf(':');
            var (path, action) = split < 0 ? (tail, "") : (tail[..split], tail[(split + 1)..].Trim('/'));
            var target = Resolve(ctx, store, driveId, (m, d) => m.FindItemByPath(d, path));
            return action switch
            {
                "" => GetItem(ctx, o.Value, target),
                "children" => GetChildren(ctx, store, o.Value, target),
                "content" => GetContent(ctx, target),
                "permissions" => GetPermissions(ctx, store, o.Value, target),
                _ => throw new GraphException(StatusCodes.Status404NotFound, "Request_ResourceNotFound", $"Resource not found for the segment '{action}'."),
            };
        });

        app.MapGet("/_download/{token}", (string token, GraphStore store) =>
        {
            var decoded = DownloadTokens.Decode(token);
            var model = store.Model;
            var item = decoded is { } d && d.Epoch == store.Epoch
                ? model.FindDrive(d.DriveKey) is { } drive ? model.FindItem(drive, d.ItemId) : null
                : null;
            return item?.Content is { } bytes
                ? Results.Bytes(bytes, item.ContentType ?? "application/octet-stream")
                : throw GraphException.ItemNotFound();
        });

        return app;
    }

    /// <summary>Resolves a drive item and enforces visibility: unknown and unreadable both yield 404 itemNotFound.</summary>
    private static Target Resolve(HttpContext ctx, GraphStore store, string driveId, Func<GraphModel, DriveRecord, ItemRecord?> find)
    {
        var mapper = FileMapper.For(ctx, store);
        var drive = mapper.Model.FindDrive(driveId) ?? throw GraphException.ItemNotFound();
        var item = find(mapper.Model, drive);
        return item is not null && mapper.Access.CanRead(mapper.Caller, item)
            ? new Target(mapper, drive, item)
            : throw GraphException.ItemNotFound();
    }

    private static IResult GetItem(HttpContext ctx, MockGraphOptions options, Target t) =>
        Entity(ctx, options, t.Mapper.ToDriveItem(t.Drive, t.Item), $"drives('{t.Drive.Id}')/items/$entity");

    private static IResult GetChildren(HttpContext ctx, GraphStore store, MockGraphOptions options, Target t)
    {
        var visible = t.Item.IsFolder
            ? t.Mapper.Model.ChildrenOf(t.Item).Where(c => t.Mapper.Access.CanRead(t.Mapper.Caller, c)).ToList()
            : throw GraphException.ItemNotFound();
        return Collection(ctx, store, options, visible, $"drives('{t.Drive.Id}')/items('{t.Item.Id}')/children", (items, next) => new DriveItemCollectionResponse
        {
            Value = [.. items.Select(i => t.Mapper.ToDriveItem(t.Drive, i))],
            OdataNextLink = next,
        });
    }

    private static IResult GetContent(HttpContext ctx, Target t) =>
        t.Item.IsFolder ? throw GraphException.ItemNotFound() : Results.Redirect(t.Mapper.DownloadUrl(t.Item));

    private static IResult GetPermissions(HttpContext ctx, GraphStore store, MockGraphOptions options, Target t)
    {
        var grants = t.Mapper.Access.EffectiveGrants(t.Item);
        return Collection(ctx, store, options, grants, $"drives('{t.Drive.Id}')/items('{t.Item.Id}')/permissions", (items, next) => new PermissionCollectionResponse
        {
            Value = [.. items.Select(g => t.Mapper.ToPermission(t.Drive, g))],
            OdataNextLink = next,
        });
    }
}