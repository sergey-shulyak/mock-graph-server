using MockGraph.Server.Auth;

namespace MockGraph.Server.State;

/// <param name="Item">The item from the new model, or from the old model when <paramref name="Deleted"/>.</param>
public sealed record DeltaEntry(ItemRecord Item, string? ParentId, bool Deleted, bool SharedChanged);

public sealed record GroupDeltaEntry(GroupRecord Group, IReadOnlyList<PrincipalRef> Added, IReadOnlyList<PrincipalRef> Removed);

/// <summary>Pure functions that turn model snapshots plus the change log into delta results.</summary>
public static class DeltaCalculator
{
    /// <summary>Full enumeration: the scope root first, then every item the caller can read, parents before children.</summary>
    public static List<DeltaEntry> InitialDrive(GraphModel model, DriveRecord drive, string scopePath, CallerContext caller)
    {
        var access = new AccessEvaluator(model);
        var result = new List<DeltaEntry>();
        if (model.FindItemByPath(drive, scopePath) is { } root)
        {
            result.Add(new DeltaEntry(root, model.ParentOf(root)?.Id, false, false));
        }

        result.AddRange(model.Items
            .Where(i => i.DriveKey == drive.Key && InScope(i.Path, scopePath) && i.Path != scopePath && access.CanRead(caller, i))
            .OrderBy(i => i.Path, StringComparer.Ordinal)
            .Select(i => new DeltaEntry(i, model.ParentOf(i)?.Id, false, false)));
        return result;
    }

    /// <summary>
    /// Changes in (old, new] as the caller sees them. Visibility is evaluated on both snapshots, so an item that became
    /// unreadable is a delete and one that became readable is an upsert, whatever caused it (grant, revoke, group change).
    /// Sharing-only changes are reported only when <paramref name="showSharing"/> is set, annotated as sharedChanged.
    /// </summary>
    public static List<DeltaEntry> IncrementalDrive(
        GraphModel oldModel, GraphModel newModel, DriveRecord drive, string scopePath, CallerContext caller,
        IEnumerable<ChangeEntry> changes, bool showSharing)
    {
        var touched = new Dictionary<string, (bool Upsert, bool Sharing)>();
        foreach (var c in changes.Where(c => c.DriveKey == drive.Key && c.ItemId is not null))
        {
            var (upsert, sharing) = touched.GetValueOrDefault(c.ItemId!);
            touched[c.ItemId!] = (upsert || c.Kind == ChangeKind.ItemUpserted, sharing || c.Kind == ChangeKind.ItemSharingChanged);
        }

        var oldItems = oldModel.Items.Where(i => i.DriveKey == drive.Key).ToDictionary(i => i.Id);
        var newItems = newModel.Items.Where(i => i.DriveKey == drive.Key).ToDictionary(i => i.Id);
        var oldAccess = new AccessEvaluator(oldModel);
        var newAccess = new AccessEvaluator(newModel);

        // For an app caller visibility never changes, so only logged items matter; for a user any item may flip.
        var candidates = caller is AppCaller ? touched.Keys.ToHashSet() : [.. oldItems.Keys, .. newItems.Keys];
        var result = new List<DeltaEntry>();

        foreach (var id in candidates)
        {
            oldItems.TryGetValue(id, out var before);
            newItems.TryGetValue(id, out var after);
            var visibleBefore = before is not null && InScope(before.Path, scopePath) && oldAccess.CanRead(caller, before);
            var visibleAfter = after is not null && InScope(after.Path, scopePath) && newAccess.CanRead(caller, after);
            var (upsert, sharing) = touched.GetValueOrDefault(id);

            if (visibleAfter)
            {
                var becameVisible = !visibleBefore;
                if (upsert || becameVisible || (sharing && showSharing))
                {
                    var shared = showSharing && (sharing || (becameVisible && before is not null));
                    result.Add(new DeltaEntry(after!, newModel.ParentOf(after!)?.Id, false, shared));
                }
            }
            else if (visibleBefore)
            {
                result.Add(new DeltaEntry(before!, oldModel.ParentOf(before!)?.Id, true, false));
            }
        }

        return [.. result.OrderBy(e => e.Item.Path, StringComparer.Ordinal)];
    }

    public static List<GroupDeltaEntry> InitialGroups(GraphModel model) =>
        [.. model.Groups.Select(g => new GroupDeltaEntry(g, g.Members, []))];

    /// <summary>Groups whose direct membership differs between the snapshots, with the net added and removed members.</summary>
    public static List<GroupDeltaEntry> IncrementalGroups(GraphModel oldModel, GraphModel newModel, IEnumerable<ChangeEntry> changes)
    {
        var keys = changes
            .Where(c => c.Kind is ChangeKind.GroupMemberAdded or ChangeKind.GroupMemberRemoved)
            .Select(c => c.GroupKey)
            .ToHashSet();
        var result = new List<GroupDeltaEntry>();
        foreach (var group in newModel.Groups.Where(g => keys.Contains(g.Key)))
        {
            var before = oldModel.Groups.FirstOrDefault(g => g.Key == group.Key)?.Members ?? [];
            var added = group.Members.Except(before).ToList();
            var removed = before.Except(group.Members).ToList();
            if (added.Count + removed.Count > 0)
            {
                result.Add(new GroupDeltaEntry(group, added, removed));
            }
        }

        return result;
    }

    private static bool InScope(string path, string scopePath) =>
        scopePath == "/" || path == scopePath || path.StartsWith(scopePath + "/", StringComparison.OrdinalIgnoreCase);
}