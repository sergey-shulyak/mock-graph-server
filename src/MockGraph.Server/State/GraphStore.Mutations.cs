using MockGraph.Server.Fixtures;
using MockGraph.Server.Serialization;

namespace MockGraph.Server.State;

/// <summary>Admin mutations. Each validates against the current model, then logs its changes atomically.</summary>
public sealed partial class GraphStore
{
    public AdminOutcome GrantPermission(string? driveRef, string? itemId, string? itemPath, string? principalText, string? roleText) =>
        Apply(m =>
        {
            var (drive, item) = ResolveItem(m, driveRef, itemId, itemPath);
            var principal = ResolvePrincipal(m, principalText);
            var role = ParseRole(roleText ?? throw GraphException.BadRequest("'role' is required (read, write or owner)."));

            var existing = item.Grants.FirstOrDefault(g => g.Principal == principal);
            if (existing?.Role == role)
            {
                return new Mutation(m, [], Outcome(drive, item));
            }

            GrantRecord[] grants = [.. item.Grants.Where(g => g.Principal != principal), new GrantRecord(principal, role)];
            return SharingMutation(m, drive, item, grants);
        });

    public AdminOutcome RevokePermission(string? driveRef, string? itemId, string? itemPath, string? principalText, string? roleText) =>
        Apply(m =>
        {
            var (drive, item) = ResolveItem(m, driveRef, itemId, itemPath);
            var principal = ResolvePrincipal(m, principalText);
            PermissionRole? role = roleText is null ? null : ParseRole(roleText);

            var remaining = item.Grants.Where(g => !(g.Principal == principal && (role is null || g.Role == role))).ToList();
            return remaining.Count == item.Grants.Count
                ? throw GraphException.NotFound("itemNotFound", $"'{item.Path}' has no direct grant for '{principal}'{(role is null ? "" : $" with role '{role.ToString()!.ToLowerInvariant()}'")}.")
                : SharingMutation(m, drive, item, remaining);
        });

    public AdminOutcome AddGroupMember(string groupRef, string? memberText) =>
        Apply(m =>
        {
            var group = m.FindGroup(groupRef) ?? throw GraphException.ResourceNotFound(groupRef);
            var member = ResolvePrincipal(m, memberText);
            if (group.Members.Contains(member))
            {
                return new Mutation(m, [], new AdminOutcome(false, 0));
            }

            if (member.Kind == PrincipalKind.Group && (member.Key == group.Key || Reaches(m, member, group.Ref)))
            {
                throw GraphException.BadRequest($"Adding '{member}' to '{group.Key}' would create a membership cycle.");
            }

            var updated = group with { Members = [.. group.Members, member] };
            return new Mutation(
                m.With(groups: Replace(m.Groups, group, updated)),
                [new PendingChange(ChangeKind.GroupMemberAdded, GroupKey: group.Key, Member: member)],
                new AdminOutcome(true, 0));
        });

    public AdminOutcome RemoveGroupMember(string groupRef, string? memberText) =>
        Apply(m =>
        {
            var group = m.FindGroup(groupRef) ?? throw GraphException.ResourceNotFound(groupRef);
            var member = ResolvePrincipal(m, memberText);
            if (!group.Members.Contains(member))
            {
                throw GraphException.NotFound("Request_ResourceNotFound", $"'{member}' is not a direct member of '{group.Key}'.");
            }

            var updated = group with { Members = [.. group.Members.Where(x => x != member)] };
            return new Mutation(
                m.With(groups: Replace(m.Groups, group, updated)),
                [new PendingChange(ChangeKind.GroupMemberRemoved, GroupKey: group.Key, Member: member)],
                new AdminOutcome(true, 0));
        });

    /// <summary>Adds or replaces a file, creating missing parent folders.</summary>
    public AdminOutcome PutFile(string? driveRef, string? pathText, byte[] content) =>
        Apply(m =>
        {
            var drive = ResolveDrive(m, driveRef);
            var path = NormalizePath(pathText);
            var now = _time.GetUtcNow();
            var changes = new List<PendingChange>();
            var items = m.Items.ToList();

            var existing = m.FindItemByPath(drive, path);
            if (existing is { IsFolder: true })
            {
                throw GraphException.BadRequest($"'{existing.Path}' is a folder; cannot replace it with a file.");
            }

            for (var parent = ItemPaths.ParentOf(path); parent != "/"; parent = ItemPaths.ParentOf(parent))
            {
                var found = m.FindItemByPath(drive, parent);
                if (found is { IsFolder: false })
                {
                    throw GraphException.BadRequest($"'{found.Path}' is a file; it cannot be a parent folder.");
                }

                if (found is null)
                {
                    var folder = new ItemRecord(drive.Key, Ids.Item(drive.Id, parent), parent, Leaf(parent), true, false, [], null, null) { Modified = now };
                    items.Add(folder);
                    changes.Add(new PendingChange(ChangeKind.ItemUpserted, drive.Key, folder.Id, folder.Path));
                }
            }

            ItemRecord file;
            if (existing is null)
            {
                file = new ItemRecord(drive.Key, Ids.Item(drive.Id, path), path, Leaf(path), false, false, [], content, FixtureLoader.ContentTypeFor(Leaf(path))) { Modified = now };
                items.Add(file);
            }
            else
            {
                file = existing with { Content = content, Modified = now, Version = existing.Version + 1 };
                items[items.FindIndex(i => i.DriveKey == existing.DriveKey && i.Id == existing.Id)] = file;
            }

            changes.Add(new PendingChange(ChangeKind.ItemUpserted, drive.Key, file.Id, file.Path));
            return new Mutation(m.With(items: Sorted(m, items)), changes, Outcome(drive, file) with { Created = existing is null });
        });

    /// <summary>Deletes a file, or a folder with all its descendants (each reported separately in delta).</summary>
    public AdminOutcome DeleteFile(string? driveRef, string? pathText) =>
        Apply(m =>
        {
            var drive = ResolveDrive(m, driveRef);
            var path = NormalizePath(pathText);
            var item = m.FindItemByPath(drive, path) ?? throw GraphException.ItemNotFound();
            var doomed = m.DescendantsOf(item).Append(item).OrderByDescending(i => i.Path, StringComparer.Ordinal).ToList();
            var ids = doomed.Select(i => i.Id).ToHashSet();
            return new Mutation(
                m.With(items: [.. m.Items.Where(i => i.DriveKey != drive.Key || !ids.Contains(i.Id))]),
                [.. doomed.Select(i => new PendingChange(ChangeKind.ItemDeleted, drive.Key, i.Id, i.Path))],
                Outcome(drive, item));
        });

    private Mutation SharingMutation(GraphModel m, DriveRecord drive, ItemRecord item, IReadOnlyList<GrantRecord> grants)
    {
        var updated = item with { Grants = grants, Version = item.Version + 1, Modified = _time.GetUtcNow() };
        var affected = new[] { item }.Concat(InheritingDescendants(m, item));
        return new Mutation(
            m.With(items: [.. m.Items.Select(i => i.DriveKey == item.DriveKey && i.Id == item.Id ? updated : i)]),
            [.. affected.Select(i => new PendingChange(ChangeKind.ItemSharingChanged, drive.Key, i.Id, i.Path))],
            Outcome(drive, item));
    }

    /// <summary>Descendants whose effective permissions include the item's own grants (no breakInheritance in between).</summary>
    private static IEnumerable<ItemRecord> InheritingDescendants(GraphModel m, ItemRecord item) =>
        m.DescendantsOf(item).Where(d =>
        {
            for (var c = d; c is not null && c.Id != item.Id; c = m.ParentOf(c))
            {
                if (c.BreakInheritance)
                {
                    return false;
                }
            }

            return true;
        }).ToList();

    private static AdminOutcome Outcome(DriveRecord drive, ItemRecord item) => new(true, 0, drive.Id, item.Id, item.Path);

    private static string Leaf(string path) => path[(path.LastIndexOf('/') + 1)..];

    private static IReadOnlyList<GroupRecord> Replace(IReadOnlyList<GroupRecord> groups, GroupRecord old, GroupRecord updated) =>
        [.. groups.Select(g => g.Key == old.Key ? updated : g)];

    private static IReadOnlyList<ItemRecord> Sorted(GraphModel m, List<ItemRecord> items) =>
        [.. m.Drives.SelectMany(d => items.Where(i => i.DriveKey == d.Key).OrderBy(i => i.Path, StringComparer.Ordinal))];

    private static string NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw GraphException.BadRequest("'path' is required.");
        }

        string normalized;
        try
        {
            normalized = FixtureLoader.NormalizePath(path);
        }
        catch (InvalidDataException ex)
        {
            throw GraphException.BadRequest(ex.Message);
        }

        return normalized.Split('/').Any(s => s is "." or "..")
            ? throw GraphException.BadRequest("'path' must not contain '.' or '..' segments.")
            : normalized;
    }

    private static PermissionRole ParseRole(string role)
    {
        try
        {
            return FixtureLoader.ParseRole(role);
        }
        catch (InvalidDataException ex)
        {
            throw GraphException.BadRequest(ex.Message);
        }
    }

    private static DriveRecord ResolveDrive(GraphModel m, string? reference)
    {
        if (string.IsNullOrWhiteSpace(reference))
        {
            return m.Drives.Count == 1
                ? m.Drives[0]
                : throw GraphException.BadRequest("'driveId' is required because there is more than one drive.");
        }

        return m.FindDrive(reference) ?? throw GraphException.NotFound("itemNotFound", $"Drive '{reference}' not found.");
    }

    private static (DriveRecord Drive, ItemRecord Item) ResolveItem(GraphModel m, string? driveRef, string? itemId, string? itemPath)
    {
        if ((itemId is null) == (itemPath is null))
        {
            throw GraphException.BadRequest("Provide exactly one of 'itemPath' or 'itemId'.");
        }

        var drive = ResolveDrive(m, driveRef);
        var item = (itemId is not null ? m.FindItem(drive, itemId) : m.FindItemByPath(drive, NormalizeLoose(itemPath!)))
            ?? throw GraphException.NotFound("itemNotFound", $"Item '{itemId ?? itemPath}' not found in drive '{drive.Key}'.");
        return item.Path == "/"
            ? throw GraphException.BadRequest("The drive root cannot carry permissions.")
            : (drive, item);
    }

    private static string NormalizeLoose(string path) => path.Trim().Length == 0 ? "/" : path;

    private static PrincipalRef ResolvePrincipal(GraphModel m, string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            throw GraphException.BadRequest("A principal like 'user:alice' or 'group:eng-backend' is required.");
        }

        PrincipalRef parsed;
        try
        {
            parsed = PrincipalRef.Parse(text);
        }
        catch (InvalidDataException ex)
        {
            throw GraphException.BadRequest(ex.Message);
        }

        DirectoryEntry? entry = parsed.Kind == PrincipalKind.User ? m.FindUser(parsed.Key) : m.FindGroup(parsed.Key);
        return entry?.Ref ?? throw GraphException.NotFound("Request_ResourceNotFound", $"Unknown principal '{text}'.");
    }

    /// <summary>True when <paramref name="target"/> is reachable below <paramref name="start"/> through group membership.</summary>
    private static bool Reaches(GraphModel m, PrincipalRef start, PrincipalRef target)
    {
        var seen = new HashSet<PrincipalRef> { start };
        var queue = new Queue<PrincipalRef>([start]);
        while (queue.TryDequeue(out var current))
        {
            foreach (var member in m.Groups.FirstOrDefault(g => g.Ref == current)?.Members ?? [])
            {
                if (member == target)
                {
                    return true;
                }

                if (member.Kind == PrincipalKind.Group && seen.Add(member))
                {
                    queue.Enqueue(member);
                }
            }
        }

        return false;
    }
}