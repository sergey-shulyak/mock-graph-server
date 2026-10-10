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
    string? ContentType);

public sealed class GraphModel(
    string tenantDomain,
    IReadOnlyList<UserRecord> users,
    IReadOnlyList<GroupRecord> groups,
    IReadOnlyList<SiteRecord> sites,
    IReadOnlyList<DriveRecord> drives,
    IReadOnlyList<ItemRecord> items)
{
    public string TenantDomain { get; } = tenantDomain;

    public IReadOnlyList<UserRecord> Users { get; } = users;

    public IReadOnlyList<GroupRecord> Groups { get; } = groups;

    public IReadOnlyList<SiteRecord> Sites { get; } = sites;

    public IReadOnlyList<DriveRecord> Drives { get; } = drives;

    public IReadOnlyList<ItemRecord> Items { get; } = items;

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