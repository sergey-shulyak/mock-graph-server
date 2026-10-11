using System.Text.Json.Nodes;

using Microsoft.Extensions.Options;
using Microsoft.Graph.Drives.Item.Items.Item.Delta;
using Microsoft.Graph.Models;
using Microsoft.Kiota.Abstractions.Serialization;

using MockGraph.Server.Auth;
using MockGraph.Server.Mapping;
using MockGraph.Server.OData;
using MockGraph.Server.Options;
using MockGraph.Server.Serialization;
using MockGraph.Server.State;

namespace MockGraph.Server.Endpoints;

/// <summary>Drive delta (<c>/drives/{id}/root/delta</c>, <c>/items/{id}/delta</c>) and groups delta (<c>/groups/delta</c>).</summary>
public static class DeltaEndpoints
{
    private const string SharedChanged = "@microsoft.graph.sharedChanged";

    public static IEndpointRouteBuilder MapDeltaEndpoints(this IEndpointRouteBuilder app)
    {
        var drives = app.MapGroup("/v1.0/drives/{driveId}");

        // Served: ".../delta", ".../delta()" (what the SDK sends), ".../delta?token=x" and ".../delta(token='x')" (SDK DeltaWithToken).
        drives.MapGet("/root/delta", (string driveId, HttpContext ctx, GraphStore s, IOptions<MockGraphOptions> o) =>
            DriveDelta(ctx, s, o.Value, driveId, "root", "root", null));
        drives.MapGet("/root/delta()", (string driveId, HttpContext ctx, GraphStore s, IOptions<MockGraphOptions> o) =>
            DriveDelta(ctx, s, o.Value, driveId, "root", "root", null));
        drives.MapGet("/root/delta(token='{token}')", (string driveId, string token, HttpContext ctx, GraphStore s, IOptions<MockGraphOptions> o) =>
            DriveDelta(ctx, s, o.Value, driveId, "root", "root", token));
        drives.MapGet("/items/{itemId}/delta", (string driveId, string itemId, HttpContext ctx, GraphStore s, IOptions<MockGraphOptions> o) =>
            DriveDelta(ctx, s, o.Value, driveId, itemId, $"items/{Uri.EscapeDataString(itemId)}", null));
        drives.MapGet("/items/{itemId}/delta()", (string driveId, string itemId, HttpContext ctx, GraphStore s, IOptions<MockGraphOptions> o) =>
            DriveDelta(ctx, s, o.Value, driveId, itemId, $"items/{Uri.EscapeDataString(itemId)}", null));
        drives.MapGet("/items/{itemId}/delta(token='{token}')", (string driveId, string itemId, string token, HttpContext ctx, GraphStore s, IOptions<MockGraphOptions> o) =>
            DriveDelta(ctx, s, o.Value, driveId, itemId, $"items/{Uri.EscapeDataString(itemId)}", token));

        // Literal segments: matched before /groups/{id}.
        app.MapGet("/v1.0/groups/delta()", (HttpContext ctx, GraphStore s, IOptions<MockGraphOptions> o) =>
            GroupsDelta(ctx, s, o.Value));
        app.MapGet("/v1.0/groups/delta", (HttpContext ctx, GraphStore s, IOptions<MockGraphOptions> o) =>
            GroupsDelta(ctx, s, o.Value));

        return app;
    }

    private static IResult DriveDelta(
        HttpContext ctx, GraphStore store, MockGraphOptions options, string driveId, string itemRef, string linkPath, string? routeToken)
    {
        var query = ODataQuery.Parse(ctx.Request, collection: true, options, "token");
        if (query.SkipToken is not null)
        {
            throw GraphException.BadRequest("Query option '$skiptoken' is not supported by this endpoint; use 'token'.");
        }

        var caller = ctx.GetCaller();
        var (current, epoch) = store.Snapshot();
        var drive = current.FindDrive(driveId) ?? throw GraphException.ItemNotFound();
        var scope = current.FindItem(drive, itemRef);
        if (scope is null || !scope.IsFolder || !new AccessEvaluator(current).CanRead(caller, scope))
        {
            throw GraphException.ItemNotFound();
        }

        var tokenText = routeToken ?? ctx.Request.Query["token"].FirstOrDefault();
        var link = new LinkBuilder(ctx, $"/v1.0/drives/{drive.Id}/{linkPath}/delta", "token", query);
        DeltaToken Token(long since, long target, int offset, bool initial, bool paging) =>
            new(DeltaToken.Drive, epoch, since, target, offset, initial, paging, drive.Key, scope.Id);

        long since = 0, target = current.Sequence;
        var offset = 0;
        var initial = true;
        if (tokenText == "latest")
        {
            var latest = new DeltaGetResponse { Value = [], OdataDeltaLink = link.To(Token(target, target, 0, false, false)) };
            return Respond(ctx, query, latest);
        }

        if (tokenText is not null)
        {
            var token = DeltaToken.Decode(tokenText, DeltaToken.Drive, epoch, current.Sequence);
            if (token.D != drive.Key || token.Sc != scope.Id)
            {
                throw GraphException.BadRequest("The delta token was issued for a different drive or folder.");
            }

            (since, offset, initial) = (token.S, token.O, token.I);
            target = token.P ? token.T : current.Sequence;
        }

        var model = target == current.Sequence ? current : store.ModelAt(target);
        var showSharing = ctx.Request.Headers["Prefer"].ToString().Contains("deltashowsharingchanges", StringComparison.OrdinalIgnoreCase);
        var entries = initial
            ? DeltaCalculator.InitialDrive(model, drive, scope.Path, caller)
            : DeltaCalculator.IncrementalDrive(store.ModelAt(since), model, drive, scope.Path, caller, store.ChangesBetween(since, target), showSharing);

        var mapper = FileMapper.For(ctx, store, model);
        var size = query.Top ?? options.DefaultPageSize;
        var page = entries.Skip(offset).Take(size).ToList();
        var next = offset + page.Count;
        var body = new DeltaGetResponse { Value = [.. page.Select(e => MapEntry(mapper, drive, e))] };
        if (next < entries.Count)
        {
            body.OdataNextLink = link.To(Token(since, target, next, initial, true));
        }
        else
        {
            body.OdataDeltaLink = link.To(Token(target, target, 0, false, false));
        }

        return Respond(ctx, query, body);
    }

    private static DriveItem MapEntry(FileMapper mapper, DriveRecord drive, DeltaEntry entry)
    {
        if (entry.Deleted)
        {
            return new DriveItem
            {
                Id = entry.Item.Id,
                OdataType = null,
                Deleted = new Deleted { State = "deleted" },
                ParentReference = new ItemReference { DriveId = drive.Id, Id = entry.ParentId },
            };
        }

        var item = mapper.ToDriveItem(drive, entry.Item);
        if (entry.SharedChanged)
        {
            item.AdditionalData[SharedChanged] = "True";
        }

        return item;
    }

    private static IResult GroupsDelta(HttpContext ctx, GraphStore store, MockGraphOptions options)
    {
        var query = ODataQuery.Parse(ctx.Request, collection: true, options, "$deltatoken");
        var deltaToken = ctx.Request.Query["$deltatoken"].FirstOrDefault();
        if (deltaToken is not null && query.SkipToken is not null)
        {
            throw GraphException.BadRequest("Specify either '$deltatoken' or '$skiptoken', not both.");
        }

        var (current, epoch) = store.Snapshot();
        var link = new LinkBuilder(ctx, "/v1.0/groups/delta", "$skiptoken", query);
        var deltaLinkBuilder = new LinkBuilder(ctx, "/v1.0/groups/delta", "$deltatoken", query);
        DeltaToken Token(long since, long target, int offset, bool initial, bool paging) =>
            new(DeltaToken.Groups, epoch, since, target, offset, initial, paging);

        long since = 0, target = current.Sequence;
        var offset = 0;
        var initial = true;
        var tokenText = deltaToken ?? query.SkipToken;
        if (tokenText == "latest")
        {
            return Respond(ctx, query, new Microsoft.Graph.Groups.Delta.DeltaGetResponse
            {
                Value = [],
                OdataDeltaLink = deltaLinkBuilder.To(Token(target, target, 0, false, false)),
            }, "groups");
        }

        if (tokenText is not null)
        {
            var token = DeltaToken.Decode(tokenText, DeltaToken.Groups, epoch, current.Sequence);
            (since, offset, initial) = (token.S, token.O, token.I);
            target = token.P ? token.T : current.Sequence;
        }

        var model = target == current.Sequence ? current : store.ModelAt(target);
        var entries = initial
            ? DeltaCalculator.InitialGroups(model)
            : DeltaCalculator.IncrementalGroups(store.ModelAt(since), model, store.ChangesBetween(since, target));

        var includeMembers = query.Select is null || query.Select.Contains("members", StringComparer.OrdinalIgnoreCase);
        var page = entries.Skip(offset).Take(query.Top ?? options.DefaultPageSize).ToList();
        var next = offset + page.Count;
        var body = new Microsoft.Graph.Groups.Delta.DeltaGetResponse
        {
            Value = [.. page.Select(e => MapGroup(e, includeMembers))],
        };
        if (next < entries.Count)
        {
            body.OdataNextLink = link.To(Token(since, target, next, initial, true));
        }
        else
        {
            body.OdataDeltaLink = deltaLinkBuilder.To(Token(target, target, 0, false, false));
        }

        return Respond(ctx, query, body, "groups");
    }

    private static Group MapGroup(GroupDeltaEntry entry, bool includeMembers)
    {
        var group = DirectoryMapper.ToGroup(entry.Group);
        if (includeMembers)
        {
            var members = entry.Added.Select(m => MemberNode(m, removed: false))
                .Concat(entry.Removed.Select(m => MemberNode(m, removed: true)))
                .Select(n => (UntypedNode)n);
            group.AdditionalData["members@delta"] = new UntypedArray(members);
        }

        return group;
    }

    private static UntypedObject MemberNode(PrincipalRef member, bool removed)
    {
        var props = new Dictionary<string, UntypedNode>
        {
            ["@odata.type"] = new UntypedString(member.Kind == PrincipalKind.User ? "#microsoft.graph.user" : "#microsoft.graph.group"),
            ["id"] = new UntypedString(member.Kind == PrincipalKind.User ? Ids.User(member.Key) : Ids.Group(member.Key)),
        };
        if (removed)
        {
            props["@removed"] = new UntypedObject(new Dictionary<string, UntypedNode> { ["reason"] = new UntypedString("deleted") });
        }

        return new UntypedObject(props);
    }

    private static IResult Respond<T>(HttpContext ctx, ODataQuery query, T body, string? context = null)
        where T : IParsable, IAdditionalDataHolder
    {
        body.AdditionalData["@odata.context"] = $"{Paging.BaseUrl(ctx.Request)}/v1.0/$metadata#{context ?? "Collection(driveItem)"}";
        return new GraphJsonResult(body, query);
    }

    /// <summary>Builds absolute nextLink/deltaLink URLs that keep $top and $select.</summary>
    private sealed class LinkBuilder(HttpContext ctx, string path, string tokenParameter, ODataQuery query)
    {
        public string To(DeltaToken token)
        {
            var parts = new List<string>();
            if (query.Top is { } top)
            {
                parts.Add($"$top={top}");
            }

            if (query.SelectRaw is not null)
            {
                parts.Add($"$select={Uri.EscapeDataString(query.SelectRaw)}");
            }

            parts.Add($"{tokenParameter}={token.Encode()}");
            return $"{Paging.BaseUrl(ctx.Request)}{path}?{string.Join('&', parts)}";
        }
    }
}