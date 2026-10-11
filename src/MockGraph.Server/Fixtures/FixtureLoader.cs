using Microsoft.AspNetCore.StaticFiles;

using MockGraph.Server.State;

using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace MockGraph.Server.Fixtures;

public static class FixtureLoader
{
    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    private static readonly FileExtensionContentTypeProvider ContentTypes = new();

    /// <summary>
    /// Relative paths are resolved against the content root and then each parent directory,
    /// so the same setting works for `dotnet run`, tests and a Docker image with fixtures at /app/fixtures.
    /// </summary>
    public static string ResolvePath(string configured, string contentRoot)
    {
        if (Path.IsPathRooted(configured))
        {
            return File.Exists(configured)
                ? configured
                : throw new FileNotFoundException($"Fixture file not found: {configured}");
        }

        for (var dir = new DirectoryInfo(contentRoot); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, configured);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException(
            $"Fixture file '{configured}' not found in '{contentRoot}' or any parent directory. Set MockGraph:FixturePath (MOCKGRAPH__FIXTUREPATH).");
    }

    public static GraphModel Load(string fixturePath)
    {
        var dto = Deserializer.Deserialize<FixtureDto>(File.ReadAllText(fixturePath)) ?? new FixtureDto();
        return Build(dto, Path.GetDirectoryName(Path.GetFullPath(fixturePath))!);
    }

    private static GraphModel Build(FixtureDto dto, string fixtureDir)
    {
        var domain = dto.Tenant.Domain;

        var users = dto.Users
            .Select(u => new UserRecord(Require(u.Id, "user id"), Ids.User(u.Id), u.DisplayName, u.Upn))
            .ToList();
        EnsureUnique(users.Select(u => u.Key), "user");

        var groupDtos = dto.Groups;
        EnsureUnique(groupDtos.Select(g => g.Id), "group");

        var groups = groupDtos
            .Select(g => new GroupRecord(
                Require(g.Id, "group id"),
                Ids.Group(g.Id),
                g.DisplayName,
                [.. g.Members.Select(PrincipalRef.Parse)]))
            .ToList();

        var userKeys = users.Select(u => u.Key).ToHashSet();
        var groupKeys = groups.Select(g => g.Key).ToHashSet();
        bool Exists(PrincipalRef p) => p.Kind == PrincipalKind.User ? userKeys.Contains(p.Key) : p.Kind == PrincipalKind.Group && groupKeys.Contains(p.Key);

        foreach (var member in groups.SelectMany(g => g.Members.Select(m => (Group: g, Member: m))).Where(x => !Exists(x.Member)))
        {
            throw new InvalidDataException($"Group '{member.Group.Key}' has unknown member '{member.Member}'.");
        }

        var sites = new List<SiteRecord>();
        var siteGroups = new List<SiteGroupRecord>();
        var drives = new List<DriveRecord>();
        var items = new List<ItemRecord>();

        EnsureUnique(dto.Sites.Select(s => s.Id), "site");
        if (dto.Sites.Count(s => s.Root) > 1)
        {
            throw new InvalidDataException("Only one site can be marked 'root: true'.");
        }

        EnsureUnique(dto.Sites.SelectMany(s => s.SiteGroups).Select(g => g.Id), "site group");
        foreach (var siteDto in dto.Sites)
        {
            var key = Require(siteDto.Id, "site id");
            var path = siteDto.Path is { } p ? "/" + p.Trim('/') : siteDto.Root ? "/" : $"/sites/{key}";
            sites.Add(new SiteRecord(key, Ids.Site(domain, key), siteDto.DisplayName) { Path = path, IsRoot = siteDto.Root });

            var index = 0;
            foreach (var groupDto in siteDto.SiteGroups)
            {
                var members = groupDto.Members.Select(PrincipalRef.Parse).ToList();
                foreach (var member in members)
                {
                    if (member.Kind == PrincipalKind.SiteGroup)
                    {
                        throw new InvalidDataException($"Site group '{groupDto.Id}' has member '{member}'; members must be users or groups.");
                    }

                    if (!Exists(member))
                    {
                        throw new InvalidDataException($"Site group '{groupDto.Id}' has unknown member '{member}'.");
                    }
                }

                // SharePoint numbers site groups from 3 (Owners, Visitors, Members are 3-5 on a classic team site).
                siteGroups.Add(new SiteGroupRecord(Require(groupDto.Id, "site group id"), key, (3 + index++).ToString(), groupDto.DisplayName, members));
            }
        }

        if (!sites.Any(s => s.IsRoot))
        {
            // Real tenants always have a root site; students may never have modelled one.
            sites.Insert(0, new SiteRecord("root", Ids.Site(domain, "root"), domain.Split('.')[0]) { Path = "/", IsRoot = true, IsImplicit = true });
        }

        var groupsBySite = siteGroups.GroupBy(g => g.SiteKey).ToDictionary(g => g.Key, g => g.Select(x => x.Key).ToHashSet());
        foreach (var siteDto in dto.Sites)
        {
            foreach (var driveDto in siteDto.Drives)
            {
                var drive = new DriveRecord(Require(driveDto.Id, "drive id"), Ids.Drive(driveDto.Id), siteDto.Id, driveDto.Name);
                drives.Add(drive);
                var ownGroups = groupsBySite.GetValueOrDefault(siteDto.Id) ?? [];
                items.AddRange(BuildItems(
                    drive, driveDto, fixtureDir,
                    p => p.Kind == PrincipalKind.SiteGroup ? ownGroups.Contains(p.Key) : Exists(p)));
            }
        }

        EnsureUnique(drives.Select(d => d.Key), "drive");
        EnsureUnique(sites.Select(s => s.Key), "site");
        return new GraphModel(domain, users, groups, sites, drives, items, siteGroups);
    }

    private static IEnumerable<ItemRecord> BuildItems(DriveRecord drive, DriveDto dto, string fixtureDir, Func<PrincipalRef, bool> exists)
    {
        var byPath = new Dictionary<string, ItemRecord>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in dto.Items)
        {
            var path = NormalizePath(item.Path);
            if (byPath.ContainsKey(path))
            {
                throw new InvalidDataException($"Duplicate item path '{path}' in drive '{drive.Key}'.");
            }

            var grants = item.Permissions.Select(p =>
            {
                var principal = PrincipalRef.Parse(p.Principal);
                return exists(principal)
                    ? new GrantRecord(principal, ParseRole(p.Role))
                    : throw new InvalidDataException($"Item '{path}' grants unknown principal '{p.Principal}' (site groups must belong to the item's own site).");
            }).ToList();

            var isFolder = item.Content is null && item.Source is null && item.ContentBase64 is null;
            var name = path[(path.LastIndexOf('/') + 1)..];
            byPath[path] = new ItemRecord(
                drive.Key,
                Ids.Item(drive.Id, path),
                path,
                name,
                isFolder,
                item.BreakInheritance,
                grants,
                isFolder ? null : ReadContent(item, fixtureDir),
                isFolder ? null : ContentTypeFor(name));
        }

        foreach (var path in byPath.Keys.ToList())
        {
            for (var parent = ItemPaths.ParentOf(path); parent != "/"; parent = ItemPaths.ParentOf(parent))
            {
                if (!byPath.ContainsKey(parent))
                {
                    byPath[parent] = new ItemRecord(
                        drive.Key, Ids.Item(drive.Id, parent), parent, parent[(parent.LastIndexOf('/') + 1)..], true, false, [], null, null);
                }
            }
        }

        return byPath.Values.OrderBy(i => i.Path, StringComparer.Ordinal);
    }

    private static byte[] ReadContent(ItemDto item, string fixtureDir)
    {
        if (item.Content is not null)
        {
            return System.Text.Encoding.UTF8.GetBytes(item.Content);
        }

        if (item.ContentBase64 is not null)
        {
            return Convert.FromBase64String(item.ContentBase64);
        }

        var candidates = new[] { Path.Combine(fixtureDir, item.Source!), Path.Combine(fixtureDir, "files", item.Source!) };
        var file = candidates.FirstOrDefault(File.Exists)
            ?? throw new FileNotFoundException($"Fixture source file '{item.Source}' for '{item.Path}' not found under '{fixtureDir}'.");
        return File.ReadAllBytes(file);
    }

    public static string ContentTypeFor(string name)
    {
        if (ContentTypes.TryGetContentType(name, out var type))
        {
            return type;
        }

        return name.EndsWith(".md", StringComparison.OrdinalIgnoreCase) ? "text/markdown" : "application/octet-stream";
    }

    public static string NormalizePath(string path)
    {
        var trimmed = path.Trim().Trim('/');
        return trimmed.Length == 0 ? throw new InvalidDataException("Item path must not be empty.") : "/" + trimmed;
    }

    public static PermissionRole ParseRole(string role) => role.ToLowerInvariant() switch
    {
        "read" => PermissionRole.Read,
        "write" => PermissionRole.Write,
        "owner" => PermissionRole.Owner,
        _ => throw new InvalidDataException($"Unknown role '{role}'. Expected read, write or owner."),
    };

    private static string Require(string value, string what) =>
        string.IsNullOrWhiteSpace(value) ? throw new InvalidDataException($"Fixture is missing {what}.") : value;

    private static void EnsureUnique(IEnumerable<string> keys, string what)
    {
        var duplicate = keys.GroupBy(k => k, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null)
        {
            throw new InvalidDataException($"Duplicate {what} id '{duplicate.Key}'.");
        }
    }
}