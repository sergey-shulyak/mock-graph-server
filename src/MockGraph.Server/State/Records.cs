namespace MockGraph.Server.State;

public enum PrincipalKind
{
    User,
    Group,
}

public enum PermissionRole
{
    Read,
    Write,
    Owner,
}

public readonly record struct PrincipalRef(PrincipalKind Kind, string Key)
{
    public static PrincipalRef Parse(string text)
    {
        var (prefix, _, key) = text.Partition(':');
        var kind = prefix switch
        {
            "user" => PrincipalKind.User,
            "group" => PrincipalKind.Group,
            _ => throw new InvalidDataException($"Invalid principal '{text}'. Expected 'user:<id>' or 'group:<id>'."),
        };

        return key.Length == 0
            ? throw new InvalidDataException($"Invalid principal '{text}'. Missing id.")
            : new PrincipalRef(kind, key);
    }

    public override string ToString() => $"{(Kind == PrincipalKind.User ? "user" : "group")}:{Key}";
}

public abstract record DirectoryEntry(string Key, string Id, string DisplayName)
{
    public PrincipalRef Ref => new(this is UserRecord ? PrincipalKind.User : PrincipalKind.Group, Key);
}

public sealed record UserRecord(string Key, string Id, string DisplayName, string UserPrincipalName)
    : DirectoryEntry(Key, Id, DisplayName);

public sealed record GroupRecord(string Key, string Id, string DisplayName, IReadOnlyList<PrincipalRef> Members)
    : DirectoryEntry(Key, Id, DisplayName);

public sealed record GrantRecord(PrincipalRef Principal, PermissionRole Role);

public sealed record SiteRecord(string Key, string Id, string DisplayName);

public sealed record DriveRecord(string Key, string Id, string SiteKey, string Name);

public sealed record ItemRecord(
    string DriveKey,
    string Id,
    string Path,
    string Name,
    bool IsFolder,
    bool BreakInheritance,
    IReadOnlyList<GrantRecord> Grants,
    byte[]? Content,
    string? ContentType)
{
    /// <summary>Last modification time; seeded items use <see cref="ItemPaths.SeedTime"/>. Admin mutations set it via `with`.</summary>
    public DateTimeOffset Modified { get; init; } = ItemPaths.SeedTime;

    /// <summary>Bumped on every content or permission change; feeds eTag/cTag.</summary>
    public int Version { get; init; } = 1;
}

public sealed class GraphModel(
    string tenantDomain,
    IReadOnlyList<UserRecord> users,
    IReadOnlyList<GroupRecord> groups,
    IReadOnlyList<SiteRecord> sites,
    IReadOnlyList<DriveRecord> drives,
    IReadOnlyList<ItemRecord> items)
{
    public string TenantDomain { get; } = tenantDomain;

    /// <summary>Change-log sequence number this snapshot is the state after (0 for a freshly loaded fixture).</summary>
    public long Sequence { get; private init; }

    public IReadOnlyList<UserRecord> Users { get; } = users;

    public IReadOnlyList<GroupRecord> Groups { get; } = groups;

    public IReadOnlyList<SiteRecord> Sites { get; } = sites;

    public IReadOnlyList<DriveRecord> Drives { get; } = drives;

    public IReadOnlyList<ItemRecord> Items { get; } = items;

    public GraphModel With(
        IReadOnlyList<GroupRecord>? groups = null, IReadOnlyList<ItemRecord>? items = null, long? sequence = null) =>
        new(TenantDomain, Users, groups ?? Groups, Sites, Drives, items ?? Items) { Sequence = sequence ?? Sequence };

    public GroupRecord? FindGroup(string idOrKey) => Groups.FirstOrDefault(g =>
        string.Equals(g.Key, idOrKey, StringComparison.OrdinalIgnoreCase)
        || string.Equals(g.Id, idOrKey, StringComparison.OrdinalIgnoreCase));

    public UserRecord? FindUser(string idOrUpn) => Users.FirstOrDefault(u =>
        string.Equals(u.Key, idOrUpn, StringComparison.OrdinalIgnoreCase)
        || string.Equals(u.Id, idOrUpn, StringComparison.OrdinalIgnoreCase)
        || string.Equals(u.UserPrincipalName, idOrUpn, StringComparison.OrdinalIgnoreCase));

    public DriveRecord? FindDrive(string idOrKey) => Drives.FirstOrDefault(d =>
        string.Equals(d.Id, idOrKey, StringComparison.OrdinalIgnoreCase)
        || string.Equals(d.Key, idOrKey, StringComparison.OrdinalIgnoreCase));

    public SiteRecord? FindSite(string idOrKey) => Sites.FirstOrDefault(s =>
        string.Equals(s.Id, idOrKey, StringComparison.OrdinalIgnoreCase)
        || string.Equals(s.Key, idOrKey, StringComparison.OrdinalIgnoreCase));

    public SiteRecord SiteOf(DriveRecord drive) => Sites.First(s => s.Key == drive.SiteKey);

    public DriveRecord DriveOf(ItemRecord item) => Drives.First(d => d.Key == item.DriveKey);

    /// <summary>The drive root is virtual: it has no fixture entry, no grants and always inherits nothing.</summary>
    public ItemRecord RootOf(DriveRecord drive) =>
        new(drive.Key, Ids.Item(drive.Id, "/"), "/", "root", true, false, [], null, null);

    /// <summary>Finds an item by wire id (or the literal "root").</summary>
    public ItemRecord? FindItem(DriveRecord drive, string id)
    {
        var root = RootOf(drive);
        return id.Equals("root", StringComparison.OrdinalIgnoreCase) || id.Equals(root.Id, StringComparison.OrdinalIgnoreCase)
            ? root
            : Items.FirstOrDefault(i => i.DriveKey == drive.Key && string.Equals(i.Id, id, StringComparison.OrdinalIgnoreCase));
    }

    public ItemRecord? FindItemByPath(DriveRecord drive, string path)
    {
        var normalized = "/" + path.Trim('/');
        return normalized == "/"
            ? RootOf(drive)
            : Items.FirstOrDefault(i => i.DriveKey == drive.Key && string.Equals(i.Path, normalized, StringComparison.OrdinalIgnoreCase));
    }

    public ItemRecord? ParentOf(ItemRecord item) =>
        item.Path == "/" ? null : FindItemByPath(DriveOf(item), ItemPaths.ParentOf(item.Path));

    public IReadOnlyList<ItemRecord> ChildrenOf(ItemRecord folder) =>
        [.. Items.Where(i => i.DriveKey == folder.DriveKey && string.Equals(ItemPaths.ParentOf(i.Path), folder.Path, StringComparison.OrdinalIgnoreCase))];

    public IEnumerable<ItemRecord> DescendantsOf(ItemRecord folder)
    {
        var prefix = folder.Path == "/" ? "/" : folder.Path + "/";
        return Items.Where(i => i.DriveKey == folder.DriveKey && i.Path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    }

    public DirectoryEntry? Resolve(PrincipalRef principal) => principal.Kind == PrincipalKind.User
        ? Users.FirstOrDefault(u => u.Key == principal.Key)
        : Groups.FirstOrDefault(g => g.Key == principal.Key);
}

internal static class StringExtensions
{
    public static (string Head, string Separator, string Tail) Partition(this string text, char separator)
    {
        var index = text.IndexOf(separator);
        return index < 0 ? (text, "", "") : (text[..index], separator.ToString(), text[(index + 1)..]);
    }
}