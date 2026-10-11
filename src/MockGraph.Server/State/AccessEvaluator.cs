using MockGraph.Server.Auth;

namespace MockGraph.Server.State;

/// <param name="Grant">The grant itself.</param>
/// <param name="Source">The item that carries the grant (an ancestor when <paramref name="Inherited"/>).</param>
/// <param name="Inherited">True when the grant comes from an ancestor.</param>
public sealed record EffectiveGrant(GrantRecord Grant, ItemRecord Source, bool Inherited);

/// <summary>
/// SharePoint-style access: an item's effective grants are its own plus those of its ancestors, up to and
/// including the nearest node with <c>breakInheritance</c>. Any role implies read. Instances are cheap and
/// meant to live for one request over one model snapshot.
/// </summary>
public sealed class AccessEvaluator(GraphModel model)
{
    private readonly Dictionary<string, HashSet<PrincipalRef>> _principalSets = [];

    public IReadOnlyList<EffectiveGrant> EffectiveGrants(ItemRecord item)
    {
        var result = new List<EffectiveGrant>();
        var inherited = false;
        for (var current = item; current is not null; current = model.ParentOf(current))
        {
            result.AddRange(current.Grants.Select(g => new EffectiveGrant(g, current, inherited)));
            if (current.BreakInheritance)
            {
                break;
            }

            inherited = true;
        }

        return result;
    }

    public bool CanRead(CallerContext caller, ItemRecord item) => caller switch
    {
        AppCaller => true,
        UserCaller user => item.Path == "/" || EffectiveGrants(item).Any(g => PrincipalsOf(user.User).Contains(g.Grant.Principal)),
        _ => false,
    };

    /// <summary>The user itself plus every group (and SharePoint site group) it belongs to, directly or through nesting (cycle-safe).</summary>
    private HashSet<PrincipalRef> PrincipalsOf(UserRecord user)
    {
        if (_principalSets.TryGetValue(user.Key, out var cached))
        {
            return cached;
        }

        var set = new HashSet<PrincipalRef> { user.Ref };
        var queue = new Queue<PrincipalRef>([user.Ref]);
        while (queue.TryDequeue(out var current))
        {
            foreach (var group in model.Groups.Where(g => g.Members.Contains(current)))
            {
                if (set.Add(group.Ref))
                {
                    queue.Enqueue(group.Ref);
                }
            }
        }

        // Site groups: a user is in one when it, or any group it belongs to, is a listed member.
        foreach (var siteGroup in model.SiteGroups.Where(g => g.Members.Any(set.Contains)))
        {
            set.Add(siteGroup.Ref);
        }

        return _principalSets[user.Key] = set;
    }
}