namespace MockGraph.Server.State;

public static class ItemPaths
{
    /// <summary>Deterministic timestamp for seeded items.</summary>
    public static readonly DateTimeOffset SeedTime = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public static string ParentOf(string path)
    {
        var index = path.LastIndexOf('/');
        return index <= 0 ? "/" : path[..index];
    }
}