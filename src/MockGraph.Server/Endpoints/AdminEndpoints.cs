using System.Text.Json;

using MockGraph.Server.Fixtures;
using MockGraph.Server.Serialization;
using MockGraph.Server.State;

namespace MockGraph.Server.Endpoints;

/// <summary>Non-Graph test/demo API under /_admin. Plain JSON in, plain JSON out; no authentication.</summary>
public static class AdminEndpoints
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private sealed record PermissionRequest(string? DriveId, string? ItemPath, string? ItemId, string? Principal, string? Role);

    private sealed record MemberRequest(string? Member);

    private sealed record ThrottleRequest(int? Count, int? RetryAfterSeconds);

    public static IEndpointRouteBuilder MapAdminEndpoints(this IEndpointRouteBuilder app)
    {
        var admin = app.MapGroup("/_admin");

        admin.MapPost("/reset", (GraphStore store, ThrottleState throttle) =>
        {
            store.Reload();
            throttle.Clear();
            return Results.Ok(new { status = "reset", epoch = store.Epoch });
        });

        admin.MapPost("/throttle", async (HttpContext ctx, ThrottleState throttle) =>
        {
            var r = await Body<ThrottleRequest>(ctx);
            if (r.Count is not > 0)
            {
                throw GraphException.BadRequest("'count' is required and must be at least 1.");
            }

            var count = r.Count.Value;
            var seconds = r.RetryAfterSeconds ?? 1;
            if (seconds is < 0 or > 3600)
            {
                throw GraphException.BadRequest("'retryAfterSeconds' must be between 0 and 3600.");
            }

            throttle.Set(count, seconds);
            return Results.Ok(new { remaining = count, retryAfterSeconds = seconds });
        });
        admin.MapDelete("/throttle", (ThrottleState throttle) =>
        {
            throttle.Clear();
            return Results.Ok(new { remaining = 0 });
        });

        admin.MapPost("/permissions", async (HttpContext ctx, GraphStore store) =>
        {
            var r = await Body<PermissionRequest>(ctx);
            return Done(store.GrantPermission(r.DriveId, r.ItemId, r.ItemPath, r.Principal, r.Role));
        });
        admin.MapDelete("/permissions", async (HttpContext ctx, GraphStore store) =>
        {
            var r = await Body<PermissionRequest>(ctx);
            return Done(store.RevokePermission(r.DriveId, r.ItemId, r.ItemPath, r.Principal, r.Role));
        });

        admin.MapPost("/groups/{groupId}/members", async (string groupId, HttpContext ctx, GraphStore store) =>
            Done(store.AddGroupMember(groupId, (await Body<MemberRequest>(ctx)).Member)));
        admin.MapDelete("/groups/{groupId}/members", async (string groupId, HttpContext ctx, GraphStore store) =>
            Done(store.RemoveGroupMember(groupId, (await Body<MemberRequest>(ctx)).Member)));

        admin.MapPost("/sites/{siteId}/siteGroups/{groupId}/members", async (string siteId, string groupId, HttpContext ctx, GraphStore store) =>
            Done(store.AddSiteGroupMember(siteId, groupId, (await Body<MemberRequest>(ctx)).Member)));
        admin.MapDelete("/sites/{siteId}/siteGroups/{groupId}/members", async (string siteId, string groupId, HttpContext ctx, GraphStore store) =>
            Done(store.RemoveSiteGroupMember(siteId, groupId, (await Body<MemberRequest>(ctx)).Member)));

        admin.MapPost("/files", async (HttpContext ctx, GraphStore store) =>
        {
            if (!ctx.Request.HasFormContentType)
            {
                throw GraphException.BadRequest("Expected multipart/form-data with fields 'path' and 'file' (and optional 'driveId').");
            }

            var form = await ctx.Request.ReadFormAsync();
            var file = form.Files["file"] ?? throw GraphException.BadRequest("Multipart field 'file' is required.");
            using var buffer = new MemoryStream();
            await file.CopyToAsync(buffer);
            var drive = form["driveId"].FirstOrDefault() ?? form["drive"].FirstOrDefault();
            var outcome = store.PutFile(drive, form["path"].FirstOrDefault(), buffer.ToArray());
            return Done(outcome, outcome.Created ? StatusCodes.Status201Created : StatusCodes.Status200OK);
        });
        admin.MapDelete("/files", (HttpContext ctx, GraphStore store) =>
            Done(store.DeleteFile(
                ctx.Request.Query["driveId"].FirstOrDefault() ?? ctx.Request.Query["drive"].FirstOrDefault(),
                ctx.Request.Query["path"].FirstOrDefault())));

        admin.MapGet("/state", (GraphStore store) =>
            Results.Text(FixtureWriter.Dump(store.Model), "text/yaml", System.Text.Encoding.UTF8));

        return app;
    }

    private static IResult Done(AdminOutcome o, int status = StatusCodes.Status200OK) => Results.Json(
        new { changed = o.Changed, sequence = o.Sequence, driveId = o.DriveId, itemId = o.ItemId, path = o.Path, created = o.Created },
        Json,
        statusCode: status);

    private static async Task<T> Body<T>(HttpContext ctx)
    {
        try
        {
            return await JsonSerializer.DeserializeAsync<T>(ctx.Request.Body, Json)
                ?? throw GraphException.BadRequest("A JSON request body is required.");
        }
        catch (JsonException ex)
        {
            throw GraphException.BadRequest($"Invalid JSON body: {ex.Message}");
        }
    }
}