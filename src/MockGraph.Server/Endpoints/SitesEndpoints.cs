using Microsoft.Extensions.Options;
using Microsoft.Graph.Models;

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

        v1.MapGet("/sites", (HttpContext ctx, GraphStore store, IOptions<MockGraphOptions> o) =>
        {
            var m = FileMapper.For(ctx, store);
            return Collection(ctx, store, o.Value, m.Model.Sites, "sites", (items, next) => new SiteCollectionResponse
            {
                Value = [.. items.Select(m.ToSite)],
                OdataNextLink = next,
            });
        });
        v1.MapGet("/sites/{siteId}", (string siteId, HttpContext ctx, GraphStore store, IOptions<MockGraphOptions> o) =>
        {
            var m = FileMapper.For(ctx, store);
            return Entity(ctx, o.Value, m.ToSite(FindSite(m, siteId)), "sites/$entity");
        });
        v1.MapGet("/sites/{siteId}/drives", (string siteId, HttpContext ctx, GraphStore store, IOptions<MockGraphOptions> o) =>
        {
            var m = FileMapper.For(ctx, store);
            var site = FindSite(m, siteId);
            var drives = m.Model.Drives.Where(d => d.SiteKey == site.Key).ToList();
            return Collection(ctx, store, o.Value, drives, $"sites('{site.Id}')/drives", (items, next) => new DriveCollectionResponse
            {
                Value = [.. items.Select(m.ToDrive)],
                OdataNextLink = next,
            });
        });
        v1.MapGet("/sites/{siteId}/drive", (string siteId, HttpContext ctx, GraphStore store, IOptions<MockGraphOptions> o) =>
        {
            var m = FileMapper.For(ctx, store);
            var site = FindSite(m, siteId);
            var drive = m.Model.Drives.FirstOrDefault(d => d.SiteKey == site.Key) ?? throw GraphException.ItemNotFound();
            return Entity(ctx, o.Value, m.ToDrive(drive), "drives/$entity");
        });
        v1.MapGet("/drives/{driveId}", (string driveId, HttpContext ctx, GraphStore store, IOptions<MockGraphOptions> o) =>
        {
            var m = FileMapper.For(ctx, store);
            var drive = m.Model.FindDrive(driveId) ?? throw GraphException.ItemNotFound();
            return Entity(ctx, o.Value, m.ToDrive(drive), "drives/$entity");
        });

        return app;
    }

    private static SiteRecord FindSite(FileMapper mapper, string siteId) =>
        mapper.Model.FindSite(siteId) ?? throw GraphException.ItemNotFound();
}