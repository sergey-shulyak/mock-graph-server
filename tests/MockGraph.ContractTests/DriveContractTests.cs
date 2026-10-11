using Microsoft.Graph.Models;
using Microsoft.Graph.Models.ODataErrors;

namespace MockGraph.ContractTests;

[Collection(ContractCollection.Name)]
public sealed class DriveContractTests(ContractTarget target)
{
    private const long MaxDownloadBytes = 2 * 1024 * 1024;

    private string Drive
    {
        get
        {
            Skip.If(target.DriveId is null, "No drive available (set GRAPH_DRIVE_ID or GRAPH_SITE_ID).");
            return target.DriveId!;
        }
    }

    [SkippableFact]
    public async Task SiteHasIdAndName()
    {
        Skip.If(target.SiteId is null, "No site available.");
        var site = await target.Client.Sites[target.SiteId!].GetAsync();
        Assert.NotNull(site);
        Assert.False(string.IsNullOrEmpty(site!.Id));
        // Loosened: "name" is absent on some site types, displayName is not.
        Assert.False(string.IsNullOrEmpty(site.DisplayName ?? site.Name));
    }

    [SkippableFact]
    public async Task SiteDrivesAreDocumentedDrives()
    {
        Skip.If(target.SiteId is null, "No site available.");
        var drives = await target.Client.Sites[target.SiteId!].Drives.GetAsync();
        Assert.NotNull(drives?.Value);
        Assert.NotEmpty(drives!.Value!);
        Assert.All(drives.Value!, d =>
        {
            Assert.False(string.IsNullOrEmpty(d.Id));
            Assert.False(string.IsNullOrEmpty(d.DriveType));
        });
    }

    [SkippableFact]
    public async Task DriveHasIdAndType()
    {
        var drive = await target.Client.Drives[Drive].GetAsync();
        Assert.NotNull(drive);
        Assert.Equal(Drive, drive!.Id, ignoreCase: true);
        Assert.False(string.IsNullOrEmpty(drive.DriveType));
    }

    [SkippableFact]
    public async Task RootIsAFolder()
    {
        var root = await target.Client.Drives[Drive].Root.GetAsync();
        Assert.NotNull(root);
        Assert.False(string.IsNullOrEmpty(root!.Id));
        Assert.NotNull(root.Folder);
        Assert.Null(root.File);
    }

    [SkippableFact]
    public async Task ChildrenHaveDriveItemFacets()
    {
        var items = await Children("root");
        Skip.If(items.Count == 0, "Drive is empty.");
        foreach (var item in items)
        {
            AssertDriveItem(item);
        }
    }

    [SkippableFact]
    public async Task ItemByIdMatchesItsListing()
    {
        var items = await Children("root");
        Skip.If(items.Count == 0, "Drive is empty.");
        var listed = items[0];
        var fetched = await target.Client.Drives[Drive].Items[listed.Id!].GetAsync();
        AssertDriveItem(fetched!);
        Assert.Equal(listed.Id, fetched!.Id, ignoreCase: true);
        Assert.Equal(listed.Name, fetched.Name);
    }

    [SkippableFact]
    public async Task ChildrenHonourTopAndNextLinkIsFollowable()
    {
        var first = await target.Client.Drives[Drive].Items["root"].Children.GetAsync(r => r.QueryParameters.Top = 1);
        Skip.If(first?.OdataNextLink is null, "Root folder has fewer than 2 children, cannot test paging.");
        Assert.Single(first!.Value!);
        await PagingAssert.FollowAsync<DriveItem, DriveItemCollectionResponse>(target, first, 60, i => i.Id);
    }

    [SkippableFact]
    public async Task DriveItemSelectReturnsOnlySelectedPropertiesPlusId()
    {
        var drive = Drive; // triggers the skip when no drive is known
        var (status, body) = await target.RawGetAsync($"/drives/{drive}/root/children?$top=1&$select=name,size");
        Assert.Equal(200, status);
        var value = body!["value"]!.AsArray();
        Skip.If(value.Count == 0, "Drive is empty.");
        ContractAssert.OnlyProperties(value[0]!.AsObject(), "id", "name", "size");
    }

    [SkippableFact]
    public async Task FileContentLengthEqualsSize()
    {
        var file = await FindFile(MaxDownloadBytes);
        Skip.If(file is null, "No small file found near the drive root.");
        // The SDK follows the 302 (mock) or the pre-authenticated download redirect (SharePoint).
        await using var stream = await target.Client.Drives[Drive].Items[file!.Id!].Content.GetAsync();
        Assert.NotNull(stream);
        using var buffer = new MemoryStream();
        await stream!.CopyToAsync(buffer);
        // Loosened: SharePoint may report a slightly different size for files whose server-side metadata was edited; we only demand equality for the file as stored.
        Assert.Equal(file.Size, buffer.Length);
    }

    [SkippableFact]
    public async Task ContentOfUnknownItemIsItemNotFound()
    {
        var ex = await Assert.ThrowsAsync<ODataError>(() => target.Client.Drives[Drive].Items[UnknownItemId()].Content.GetAsync());
        ContractAssert.GraphError(ex, 404, "itemNotFound");
    }

    [SkippableFact]
    public async Task UnknownItemIsItemNotFound()
    {
        var ex = await Assert.ThrowsAsync<ODataError>(() => target.Client.Drives[Drive].Items[UnknownItemId()].GetAsync());
        ContractAssert.GraphError(ex, 404, "itemNotFound");
    }

    [SkippableFact]
    public async Task PermissionsHaveRolesAndAGrantee()
    {
        var candidates = (await Children("root")).Take(10).ToList();
        var file = await FindFile(long.MaxValue);
        if (file is not null)
        {
            candidates.Add(file);
        }

        var checkedAny = false;
        foreach (var item in candidates)
        {
            var permissions = await target.Client.Drives[Drive].Items[item.Id!].Permissions.GetAsync();
            foreach (var permission in permissions?.Value ?? [])
            {
                checkedAny = true;
                AssertPermission(permission);
            }
        }

        // Loosened: a real tenant may expose no permission entries on the items we sampled.
        Skip.If(!checkedAny, "No permission entries on the sampled items.");
    }

    private static void AssertPermission(Permission permission)
    {
        Assert.False(string.IsNullOrEmpty(permission.Id));
        Assert.NotNull(permission.Roles);
        Assert.NotEmpty(permission.Roles!);

        // Loosened: real SharePoint expresses grantees in several ways: grantedToV2 (user, group, siteUser, siteGroup),
        // grantedToIdentitiesV2 (link grants to several people), or a sharing link without a named grantee.
        var grantee = permission.GrantedToV2;
        var hasGrantee =
            grantee is not null && (grantee.User is not null || grantee.Group is not null || grantee.SiteUser is not null || grantee.SiteGroup is not null
                                    || grantee.Application is not null || grantee.Device is not null)
            || permission.GrantedToIdentitiesV2 is { Count: > 0 }
            || permission.Link is not null
            || permission.GrantedTo is not null
            || permission.GrantedToIdentities is { Count: > 0 };
        Assert.True(hasGrantee, $"Permission {permission.Id} names no grantee or link.");

        if (grantee?.User is { } user)
        {
            Assert.False(string.IsNullOrEmpty(user.Id ?? user.DisplayName));
        }

        if (permission.InheritedFrom is { } inherited)
        {
            Assert.False(string.IsNullOrEmpty(inherited.Id) && string.IsNullOrEmpty(inherited.DriveId) && string.IsNullOrEmpty(inherited.Path));
        }
    }

    private void AssertDriveItem(DriveItem item)
    {
        Assert.False(string.IsNullOrEmpty(item.Id));
        Assert.False(string.IsNullOrEmpty(item.Name));
        // Loosened: besides file/folder, real items can be package (OneNote) or remoteItem (shortcut); only file+folder together is illegal.
        Assert.False(item.File is not null && item.Folder is not null, "file and folder facets are mutually exclusive");
        Assert.True(item.File is not null || item.Folder is not null || item.Package is not null || item.RemoteItem is not null,
            $"{item.Name} has no file/folder facet");
        Assert.NotNull(item.ParentReference);
        Assert.Equal(Drive, item.ParentReference!.DriveId, ignoreCase: true);
        Assert.False(string.IsNullOrEmpty(item.ETag));
        Assert.NotNull(item.Size);
        Assert.True(item.Size >= 0);
        if (item.File is not null)
        {
            Assert.False(string.IsNullOrEmpty(item.File.MimeType));
        }
    }

    private async Task<List<DriveItem>> Children(string itemId)
    {
        var page = await target.Client.Drives[Drive].Items[itemId].Children.GetAsync();
        return page?.Value ?? [];
    }

    /// <summary>Breadth-first search (bounded) for a file with 0 &lt; size &lt;= <paramref name="maxSize"/>.</summary>
    private async Task<DriveItem?> FindFile(long maxSize)
    {
        var queue = new Queue<string>();
        queue.Enqueue("root");
        for (var visited = 0; queue.Count > 0 && visited < 15; visited++)
        {
            foreach (var child in await Children(queue.Dequeue()))
            {
                if (child.File is not null && child.Size is > 0 && child.Size <= maxSize)
                {
                    return child;
                }

                if (child.Folder is not null)
                {
                    queue.Enqueue(child.Id!);
                }
            }
        }

        return null;
    }

    private static string UnknownItemId() => "01" + Guid.NewGuid().ToString("N").ToUpperInvariant();
}