using System.Text.Json.Nodes;

namespace MockGraph.Server.OData;

public static class SelectFilter
{
    public static void Apply(JsonObject item, IReadOnlyList<string> select)
    {
        var drop = item
            .Select(p => p.Key)
            .Where(k => !k.StartsWith('@') && !k.Equals("id", StringComparison.OrdinalIgnoreCase)
                && !select.Contains(k, StringComparer.OrdinalIgnoreCase))
            .ToList();

        foreach (var key in drop)
        {
            item.Remove(key);
        }
    }
}