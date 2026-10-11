using Microsoft.Graph.Drives.Item.Items.Item.Delta;
using Microsoft.Graph.Models;

namespace MockGraph.ContractTests;

[Collection(ContractCollection.Name)]
public sealed class ChangeTrackingContractTests(ContractTarget target)
{
    private const int MaxPages = 500;

    private string Drive
    {
        get
        {
            Skip.If(target.DriveId is null, "No drive available (set GRAPH_DRIVE_ID or GRAPH_SITE_ID).");
            return target.DriveId!;
        }
    }

    [SkippableFact]
    public async Task FullEnumerationEndsWithADeltaLink()
    {
        var (items, deltaLink, _) = await Drain(null);
        Assert.False(string.IsNullOrEmpty(deltaLink));
        Assert.All(items, i => Assert.False(string.IsNullOrEmpty(i.Id)));
        // Loosened: real Graph may repeat an item across pages (later occurrence wins), so no uniqueness assertion.
        // Loosened: the drive root itself (root facet) has no parentReference.
        Assert.All(items.Where(i => i.Deleted is null && i.Root is null), i => Assert.NotNull(i.ParentReference));
    }

    [SkippableFact]
    public async Task RequeryingTheDeltaLinkReturnsAPageAndANewDeltaLink()
    {
        var (_, first, _) = await Drain(null);
        var (changes, second, _) = await Drain(first);
        // Loosened: the page is "possibly empty", not necessarily empty: real tenants have background activity
        // (indexing, other users), and the new token is only required to be present, not different.
        Assert.False(string.IsNullOrEmpty(second));
        Assert.All(changes, i => Assert.False(string.IsNullOrEmpty(i.Id)));
    }

    [SkippableFact]
    public async Task TokenLatestReturnsNoItemsAndADeltaLink()
    {
        var page = await target.Client.Drives[Drive].Items["root"].Delta
            .WithUrl($"{target.BaseUrl}/drives/{Drive}/root/delta?token=latest").GetAsDeltaGetResponseAsync();
        Assert.NotNull(page);
        Assert.True(page!.Value is null or { Count: 0 });
        Assert.False(string.IsNullOrEmpty(page.OdataDeltaLink));
    }

    [SkippableFact]
    public async Task DeltaWireShapeHasValueAndExactlyOneLinkKind()
    {
        var (status, body) = await target.RawGetAsync($"/drives/{Drive}/root/delta", prefer: "deltashowsharingchanges");
        // Prefer: deltashowsharingchanges must be accepted (it only adds annotations).
        Assert.Equal(200, status);
        Assert.IsType<System.Text.Json.Nodes.JsonArray>(body!["value"]);
        var next = body["@odata.nextLink"]?.GetValue<string>();
        var delta = body["@odata.deltaLink"]?.GetValue<string>();
        Assert.True((next is null) != (delta is null), "Exactly one of @odata.nextLink / @odata.deltaLink must be present.");
        Assert.StartsWith("http", next ?? delta, StringComparison.OrdinalIgnoreCase);

        // Annotation value is documented as the string "True"; loosened: it is only emitted when something was shared, so it is checked only if present.
        foreach (var item in body["value"]!.AsArray())
        {
            if (item!["@microsoft.graph.sharedChanged"] is { } shared)
            {
                Assert.Equal("True", shared.GetValue<string>());
            }
        }
    }

    [SkippableFact]
    public async Task DeletedItemsCarryTheDeletedFacetWhenPresent()
    {
        var (items, _, _) = await Drain(null);
        var deleted = items.Where(i => i.Deleted is not null).ToList();
        // Loosened: a fresh enumeration normally contains no deletions; the facet is checked only if the tenant returns some.
        Skip.If(deleted.Count == 0, "No deleted items in the enumeration.");
        Assert.All(deleted, i => Assert.False(string.IsNullOrEmpty(i.Id)));
    }

    private async Task<(List<DriveItem> Items, string DeltaLink, int Pages)> Drain(string? deltaLink)
    {
        var builder = target.Client.Drives[Drive].Items["root"].Delta;
        var page = deltaLink is null
            ? await builder.GetAsDeltaGetResponseAsync()
            : await builder.WithUrl(deltaLink).GetAsDeltaGetResponseAsync();
        var items = new List<DriveItem>();
        var pages = 1;
        while (true)
        {
            items.AddRange(page!.Value ?? []);
            if (page.OdataNextLink is not { } next)
            {
                break;
            }

            Assert.True(pages++ < MaxPages, "Delta did not terminate.");
            page = await builder.WithUrl(next).GetAsDeltaGetResponseAsync();
        }

        Assert.NotNull(page.OdataDeltaLink);
        return (items, page.OdataDeltaLink!, pages);
    }
}