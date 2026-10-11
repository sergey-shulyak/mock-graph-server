using Microsoft.Extensions.Options;
using Microsoft.Graph.Models;
using Microsoft.Graph.Sites.GetAllSites;

using MockGraph.Server.Mapping;
using MockGraph.Server.Options;
using MockGraph.Server.Serialization;
using MockGraph.Server.State;

using static MockGraph.Server.Endpoints.GraphResults;

namespace MockGraph.Server.Endpoints;

/// <summary>Sites and drive metadata. Visible to every authenticated caller; only items are access-filtered.</summary>
public static class SitesEndpoints
{
    public static IEndpointRouteBuilder MapSitesEndpoints(this IEndpointRouteBuilder app)
    {
        var v1 = app.MapGroup("/v1.0");

        // Plain GET /sites lists the fixture's sites; the implicit root site only shows up via search / getAllSites / root.
        v1.MapGet("/sites", (HttpContext ctx, GraphStore store, IOptions<MockGraphOptions> o) =>
        {
            var m = FileMapper.For(ctx, store);
            return Collection(
                ctx, store, o.Value, m.Model.Sites, "sites", (items, next) => new SiteCollectionResponse
                {
                    Value = [.. items.Select(m.ToSite)],
                    OdataNextLink = next,
                },
                ["search", "$search"], // docs show `search=`, the SDK sends `$search=`
                (q, all) => (Search(all, q.Extra("search") ?? q.Extra("$search")), null));
        });
        // The SDK calls the function as getAllSites(); the docs show getAllSites. Serve both.
        foreach (var route in new[] { "/sites/getAllSites", "/sites/getAllSites()" })
        {
            v1.MapGet(route, (HttpContext ctx, GraphStore store, IOptions<MockGraphOptions> o) =>
            {
                var m = FileMapper.For(ctx, store);
                return Collection(ctx, store, o.Value, m.Model.Sites, "sites", (items, next) => new GetAllSitesGetResponse
                {
                    Value = [.. items.Select(m.ToSite)],
                    OdataNextLink = next,
                });
            });
        }
        v1.MapGet("/sites/root", (HttpContext ctx, GraphStore store, IOptions<MockGraphOptions> o) =>
        {
            var m = FileMapper.For(ctx, store);
            return Entity(ctx, o.Value, m.ToSite(FindSite(m, "root")), "sites/$entity");
        });
        v1.MapGet("/sites/root/drives", (HttpContext ctx, GraphStore store, IOptions<MockGraphOptions> o) =>
            SiteDrives(ctx, store, o.Value, "root"));
        v1.MapGet("/sites/root/drive", (HttpContext ctx, GraphStore store, IOptions<MockGraphOptions> o) =>
            SiteDrive(ctx, store, o.Value, "root"));

        // GET /sites/{siteId} and path addressing /sites/{hostname}:/{server-relative-path}[:/drives|:/drive] (one catch-all keeps routing unambiguous).
        v1.MapGet("/sites/{**rest}", (string rest, HttpContext ctx, GraphStore store, IOptions<MockGraphOptions> o) =>
        {
            var colon = rest.IndexOf(':');
            if (colon <= 0 && !rest.Contains('/'))
            {
                var single = FileMapper.For(ctx, store);
                return Entity(ctx, o.Value, single.ToSite(FindSite(single, rest)), "sites/$entity");
            }

            if (colon <= 0)
            {
                throw new GraphException(StatusCodes.Status404NotFound, "Request_ResourceNotFound", $"Resource not found for the segment '{rest.Split('/')[0]}'.");
            }

            var tail = rest[(colon + 1)..];
            var second = tail.LastIndexOf(':');
            var (path, action) = second < 0 ? (tail, "") : (tail[..second], tail[(second + 1)..].Trim('/'));
            var reference = $"{rest[..colon]}:{path}";
            return action switch
            {
                "" => Entity(ctx, o.Value, FileMapper.For(ctx, store) is var m ? m.ToSite(FindSite(m, reference)) : null!, "sites/$entity"),
                "drives" => SiteDrives(ctx, store, o.Value, reference),
                "drive" => SiteDrive(ctx, store, o.Value, reference),
                _ => throw new GraphException(StatusCodes.Status404NotFound, "Request_ResourceNotFound", $"Resource not found for the segment '{action}'."),
            };
        });
        v1.MapGet("/sites/{siteId}/drives", (string siteId, HttpContext ctx, GraphStore store, IOptions<MockGraphOptions> o) =>
            SiteDrives(ctx, store, o.Value, siteId));
        v1.MapGet("/sites/{siteId}/drive", (string siteId, HttpContext ctx, GraphStore store, IOptions<MockGraphOptions> o) =>
            SiteDrive(ctx, store, o.Value, siteId));
        v1.MapGet("/drives/{driveId}", (string driveId, HttpContext ctx, GraphStore store, IOptions<MockGraphOptions> o) =>
        {
            var m = FileMapper.For(ctx, store);
            var drive = m.Model.FindDrive(driveId) ?? throw GraphException.ItemNotFound();
            return Entity(ctx, o.Value, m.ToDrive(drive), "drives/$entity");
        });

        return app;
    }

    private static IResult SiteDrives(HttpContext ctx, GraphStore store, MockGraphOptions options, string siteRef)
    {
        var m = FileMapper.For(ctx, store);
        var site = FindSite(m, siteRef);
        var drives = m.Model.Drives.Where(d => d.SiteKey == site.Key).ToList();
        return Collection(ctx, store, options, drives, $"sites('{site.Id}')/drives", (items, next) => new DriveCollectionResponse
        {
            Value = [.. items.Select(m.ToDrive)],
            OdataNextLink = next,
        });
    }

    private static IResult SiteDrive(HttpContext ctx, GraphStore store, MockGraphOptions options, string siteRef)
    {
        var m = FileMapper.For(ctx, store);
        var site = FindSite(m, siteRef);
        var drive = m.Model.Drives.FirstOrDefault(d => d.SiteKey == site.Key) ?? throw GraphException.ItemNotFound();
        return Entity(ctx, options, m.ToDrive(drive), "drives/$entity");
    }

    /// <summary>`search=*` (or no search) keeps everything; a keyword matches name or displayName, case-insensitively.</summary>
    private static IReadOnlyList<SiteRecord> Search(IReadOnlyList<SiteRecord> sites, string? term)
    {
        if (term is null)
        {
            return [.. sites.Where(s => !s.IsImplicit)];
        }

        var keyword = term.Trim().Trim('"');
        return keyword is "*" or ""
            ? sites
            : [.. sites.Where(s => s.Key.Contains(keyword, StringComparison.OrdinalIgnoreCase)
                || s.DisplayName.Contains(keyword, StringComparison.OrdinalIgnoreCase))];
    }

    private static SiteRecord FindSite(FileMapper mapper, string siteId) =>
        mapper.Model.FindSite(siteId) ?? throw GraphException.ItemNotFound();
}