using Microsoft.Extensions.Options;

using MockGraph.Server.Fixtures;
using MockGraph.Server.Options;

namespace MockGraph.Server.State;

/// <summary>
/// Singleton holding the current model, the epoch, the append-only change log and one immutable model snapshot per
/// change (so delta can diff "state at token" against "state now"). Every mutation and its log append happen in one
/// critical section; readers get immutable snapshots.
/// </summary>
public sealed partial class GraphStore
{
    private readonly Lock _gate = new();
    private readonly string _fixturePath;
    private readonly TimeProvider _time;
    private GraphModel _model;
    private string _epoch;
    private long _sequence;
    private List<GraphModel> _history = [];
    private List<ChangeEntry> _log = [];

    public GraphStore(IOptions<MockGraphOptions> options, IHostEnvironment environment, TimeProvider time)
    {
        _fixturePath = FixtureLoader.ResolvePath(options.Value.FixturePath, environment.ContentRootPath);
        _time = time;
        _model = FixtureLoader.Load(_fixturePath);
        _epoch = NewEpoch();
        _history = [_model];
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

    public long Sequence
    {
        get
        {
            lock (_gate)
            {
                return _sequence;
            }
        }
    }

    public IReadOnlyList<UserRecord> Users => Model.Users;

    public IReadOnlyList<GroupRecord> Groups => Model.Groups;

    /// <summary>Model and epoch read atomically.</summary>
    public (GraphModel Model, string Epoch) Snapshot()
    {
        lock (_gate)
        {
            return (_model, _epoch);
        }
    }

    /// <summary>The model as it was after change <paramref name="sequence"/> (the latest snapshot at or before it).</summary>
    public GraphModel ModelAt(long sequence)
    {
        lock (_gate)
        {
            return _history.LastOrDefault(m => m.Sequence <= sequence) ?? _history[0];
        }
    }

    public IReadOnlyList<ChangeEntry> ChangesBetween(long afterExclusive, long uptoInclusive)
    {
        lock (_gate)
        {
            return [.. _log.Where(c => c.Seq > afterExclusive && c.Seq <= uptoInclusive)];
        }
    }

    /// <summary>Reloads the fixture, drops the change log and starts a new epoch (all outstanding delta tokens die).</summary>
    public void Reload()
    {
        lock (_gate)
        {
            _model = FixtureLoader.Load(_fixturePath);
            _epoch = NewEpoch();
            _sequence = 0;
            _history = [_model];
            _log = [];
        }
    }

    public UserRecord? FindUser(string idOrUpn) => Model.FindUser(idOrUpn);

    public GroupRecord? FindGroup(string id) => Model.FindGroup(id);

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

    private sealed record Mutation(GraphModel Model, IReadOnlyList<PendingChange> Changes, AdminOutcome Outcome);

    /// <summary>Runs a mutation against the current model; if it produced changes, logs them and publishes the new snapshot.</summary>
    private AdminOutcome Apply(Func<GraphModel, Mutation> build)
    {
        lock (_gate)
        {
            var mutation = build(_model);
            if (mutation.Changes.Count == 0)
            {
                return mutation.Outcome with { Changed = false, Sequence = _sequence };
            }

            foreach (var c in mutation.Changes)
            {
                _sequence++;
                _log.Add(new ChangeEntry(_sequence, c.Kind, c.DriveKey, c.ItemId, c.ItemPath, c.GroupKey, c.Member));
            }

            _model = mutation.Model.With(sequence: _sequence);
            _history.Add(_model);
            return mutation.Outcome with { Changed = true, Sequence = _sequence };
        }
    }
}