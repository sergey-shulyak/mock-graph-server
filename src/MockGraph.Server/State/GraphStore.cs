using Microsoft.Extensions.Options;

using MockGraph.Server.Fixtures;
using MockGraph.Server.Options;

namespace MockGraph.Server.State;

public sealed class GraphStore
{
    private readonly Lock _gate = new();
    private readonly string _fixturePath;
    private GraphModel _model;
    private string _epoch;

    public GraphStore(IOptions<MockGraphOptions> options, IHostEnvironment environment)
    {
        _fixturePath = FixtureLoader.ResolvePath(options.Value.FixturePath, environment.ContentRootPath);
        _model = FixtureLoader.Load(_fixturePath);
        _epoch = NewEpoch();
    }

    public string Epoch
    {
        get
        {
            lock (_gate)
            {
                return _epoch;
            }
        }
    }

    public GraphModel Model
    {
        get
        {
            lock (_gate)
            {
                return _model;
            }
        }
    }

    public IReadOnlyList<UserRecord> Users => Model.Users;

    public IReadOnlyList<GroupRecord> Groups => Model.Groups;

    public void Reload()
    {
        lock (_gate)
        {
            _model = FixtureLoader.Load(_fixturePath);
            _epoch = NewEpoch();
        }
    }

    public UserRecord? FindUser(string idOrUpn) => Model.Users.FirstOrDefault(u =>
        string.Equals(u.Key, idOrUpn, StringComparison.OrdinalIgnoreCase)
        || string.Equals(u.Id, idOrUpn, StringComparison.OrdinalIgnoreCase)
        || string.Equals(u.UserPrincipalName, idOrUpn, StringComparison.OrdinalIgnoreCase));

    public GroupRecord? FindGroup(string id) => Model.Groups.FirstOrDefault(g =>
        string.Equals(g.Key, id, StringComparison.OrdinalIgnoreCase)
        || string.Equals(g.Id, id, StringComparison.OrdinalIgnoreCase));

    public IReadOnlyList<GroupRecord> MemberOf(DirectoryEntry entry) =>
        Model.Groups.Where(g => g.Members.Contains(entry.Ref)).ToList();

    public IReadOnlyList<GroupRecord> TransitiveMemberOf(DirectoryEntry entry)
    {
        var model = Model;
        var result = new List<GroupRecord>();
        var queue = new Queue<PrincipalRef>([entry.Ref]);
        var seen = new HashSet<PrincipalRef> { entry.Ref };

        while (queue.TryDequeue(out var current))
        {
            foreach (var group in model.Groups.Where(g => g.Members.Contains(current)))
            {
                if (seen.Add(group.Ref))
                {
                    result.Add(group);
                    queue.Enqueue(group.Ref);
                }
            }
        }

        return result;
    }

    public IReadOnlyList<DirectoryEntry> Members(GroupRecord group)
    {
        var model = Model;
        return [.. group.Members.Select(model.Resolve).OfType<DirectoryEntry>()];
    }

    public IReadOnlyList<DirectoryEntry> TransitiveMembers(GroupRecord group)
    {
        var model = Model;
        var result = new List<DirectoryEntry>();
        var queue = new Queue<GroupRecord>([group]);
        var seen = new HashSet<PrincipalRef> { group.Ref };

        while (queue.TryDequeue(out var current))
        {
            foreach (var member in current.Members.Where(seen.Add).Select(model.Resolve).OfType<DirectoryEntry>())
            {
                result.Add(member);
                if (member is GroupRecord nested)
                {
                    queue.Enqueue(nested);
                }
            }
        }

        return result;
    }

    private static string NewEpoch() => Guid.NewGuid().ToString("N");
}