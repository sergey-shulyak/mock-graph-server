using System.Security.Cryptography;

using Microsoft.Graph.Models;

using MockGraph.Server.Auth;
using MockGraph.Server.Endpoints;
using MockGraph.Server.OData;
using MockGraph.Server.State;

namespace MockGraph.Server.Mapping;

/// <summary>Request-scoped mapper from site/drive/item records to Graph models, aware of the caller's visibility.</summary>
public sealed class FileMapper
{
    private readonly string _baseUrl;
    private readonly string _epoch;

    private FileMapper(GraphModel model, CallerContext caller, string baseUrl, string epoch)
    {
        Model = model;
        Caller = caller;
        Access = new AccessEvaluator(model);
        _baseUrl = baseUrl;
        _epoch = epoch;
    }

    public GraphModel Model { get; }

    public CallerContext Caller { get; }

    public AccessEvaluator Access { get; }

    public static FileMapper For(HttpContext ctx, GraphStore store) =>
        new(store.Model, ctx.GetCaller(), Paging.BaseUrl(ctx.Request), store.Epoch);

    public static FileMapper For(HttpContext ctx, GraphStore store, GraphModel model) =>
        new(model, ctx.GetCaller(), Paging.BaseUrl(ctx.Request), store.Epoch);

    public Site ToSite(SiteRecord site)
    {
        var result = new Site
        {
            Id = site.Id,
            OdataType = null,
            Name = site.Key,
            DisplayName = site.DisplayName,
            IsPersonalSite = false,
            WebUrl = SiteUrl(site),
            CreatedDateTime = ItemPaths.SeedTime,
            LastModifiedDateTime = ItemPaths.SeedTime,
        };

        if (site.IsRoot)
        {
            // Graph reports siteCollection (and the root facet) on the root site only.
            result.Root = new Root();
            result.SiteCollection = new SiteCollection { Hostname = Model.Host, Root = new Root() };
        }

        return result;
    }

    public Drive ToDrive(DriveRecord drive) => new()
    {
        Id = drive.Id,
        OdataType = null,
        Name = drive.Name,
        DriveType = "documentLibrary",
        WebUrl = DriveUrl(drive),
        CreatedDateTime = ItemPaths.SeedTime,
        LastModifiedDateTime = ItemPaths.SeedTime,
    };

    public DriveItem ToDriveItem(DriveRecord drive, ItemRecord item)
    {
        var isRoot = item.Path == "/";
        var guid = new Guid(Convert.FromHexString(item.Id.AsSpan(2, 32))).ToString("D").ToUpperInvariant();
        var result = new DriveItem
        {
            Id = item.Id,
            OdataType = null,
            Name = item.Name,
            CreatedDateTime = ItemPaths.SeedTime,
            LastModifiedDateTime = item.Modified,
            WebUrl = isRoot ? DriveUrl(drive) : DriveUrl(drive) + EscapePath(item.Path),
            ETag = $"\"{{{guid}}},{item.Version}\"",
            CTag = $"\"c:{{{guid}}},{item.Version}\"",
            ParentReference = new ItemReference
            {
                DriveId = drive.Id,
                DriveType = "documentLibrary",
                SiteId = Model.SiteOf(drive).Id,
            },
        };

        if (!isRoot)
        {
            var parent = Model.ParentOf(item)!;
            result.ParentReference.Id = parent.Id;
            result.ParentReference.Path = ItemPath(drive, parent.Path);
        }

        if (item.IsFolder)
        {
            result.Folder = new Folder { ChildCount = Model.ChildrenOf(item).Count(c => Access.CanRead(Caller, c)) };
            result.Size = Model.DescendantsOf(item).Where(d => Access.CanRead(Caller, d)).Sum(d => (long)(d.Content?.Length ?? 0));
            if (isRoot)
            {
                result.Root = new Root();
            }
        }
        else
        {
            var content = item.Content ?? [];
            result.Size = content.Length;
            result.File = new FileObject
            {
                MimeType = item.ContentType ?? "application/octet-stream",
                Hashes = new Hashes
                {
                    Sha1Hash = Convert.ToHexString(SHA1.HashData(content)),
                    Sha256Hash = Convert.ToHexString(SHA256.HashData(content)),
                },
            };
            result.AdditionalData["@microsoft.graph.downloadUrl"] = DownloadUrl(item);
        }

        return result;
    }

    public Permission ToPermission(DriveRecord drive, EffectiveGrant effective)
    {
        var grant = effective.Grant;
        if (grant.Principal.Kind == PrincipalKind.SiteGroup)
        {
            return SiteGroupPermission(drive, effective);
        }

        var entry = Model.Resolve(grant.Principal)!;
        var identity = new Identity { Id = entry.Id, DisplayName = entry.DisplayName };
        identity.AdditionalData["email"] = entry is UserRecord user ? user.UserPrincipalName : $"{entry.Key}@{Model.TenantDomain}";

        var granted = new SharePointIdentitySet { OdataType = null };
        if (grant.Principal.Kind == PrincipalKind.User)
        {
            granted.User = identity;
        }
        else
        {
            granted.Group = identity;
        }

        return new Permission
        {
            Id = Ids.Permission(effective.Source.Id, grant.Principal.ToString(), grant.Role.ToString().ToLowerInvariant()),
            OdataType = null,
            Roles = [grant.Role.ToString().ToLowerInvariant()],
            GrantedToV2 = granted,
            InheritedFrom = effective.Inherited
                ? new ItemReference { DriveId = drive.Id, Id = effective.Source.Id, Path = ItemPath(drive, effective.Source.Path) }
                : null,
        };
    }

    /// <summary>
    /// SharePoint site groups surface only as grantedToV2.siteGroup (numeric, site-local id; loginName equals the title).
    /// The deprecated grantedTo is left out, as for users and groups.
    /// </summary>
    private Permission SiteGroupPermission(DriveRecord drive, EffectiveGrant effective)
    {
        var grant = effective.Grant;
        var group = Model.FindSiteGroup(grant.Principal.Key)!;
        return new Permission
        {
            Id = Ids.Permission(effective.Source.Id, grant.Principal.ToString(), grant.Role.ToString().ToLowerInvariant()),
            OdataType = null,
            Roles = [grant.Role.ToString().ToLowerInvariant()],
            GrantedToV2 = new SharePointIdentitySet
            {
                OdataType = null,
                SiteGroup = new SharePointIdentity { Id = group.WireId, DisplayName = group.DisplayName, LoginName = group.DisplayName },
            },
            InheritedFrom = effective.Inherited
                ? new ItemReference { DriveId = drive.Id, Id = effective.Source.Id, Path = ItemPath(drive, effective.Source.Path) }
                : null,
        };
    }

    public string DownloadUrl(ItemRecord item) =>
        $"{_baseUrl}/_download/{DownloadTokens.Encode(item.DriveKey, item.Id, _epoch)}";

    private static string ItemPath(DriveRecord drive, string path) =>
        $"/drives/{drive.Id}/root:{(path == "/" ? "" : path)}";

    private static string EscapePath(string path) =>
        string.Concat(path.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(s => "/" + Uri.EscapeDataString(s)));

    private string SiteUrl(SiteRecord site) => $"https://{Model.Host}{(site.Path == "/" ? "" : site.Path)}";

    private string DriveUrl(DriveRecord drive) => $"{SiteUrl(Model.SiteOf(drive))}/{Uri.EscapeDataString(drive.Name)}";
}