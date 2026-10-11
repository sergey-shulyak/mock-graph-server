using System.Text.Json.Nodes;

namespace MockGraph.Server.OData;

public static class SelectFilter
{
    public static void Apply(JsonObject item, IReadOnlyList<string> select)
    {
        var drop = item
            .Select(p => p.Key)
            .Where(k => !k.Contains('@') && !k.Equals("id", StringComparison.OrdinalIgnoreCase)
                && !k.Equals("deleted", StringComparison.OrdinalIgnoreCase)
                && !select.Contains(k, StringComparer.OrdinalIgnoreCase))
            .ToList();

        foreach (var key in drop)
        {
            item.Remove(key);
        }
    }
}