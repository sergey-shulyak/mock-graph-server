using System.Text.Json.Nodes;

using Microsoft.Graph;
using Microsoft.Graph.Models.ODataErrors;

namespace MockGraph.ContractTests;

internal static class ContractAssert
{
    /// <summary>All non-annotation properties must be in <paramref name="allowed"/>. Annotations (@odata.*, @microsoft.graph.*) are always fine.</summary>
    public static void OnlyProperties(JsonObject obj, params string[] allowed)
    {
        var extras = obj.Select(p => p.Key)
            .Where(k => !k.Contains('@') && !allowed.Contains(k, StringComparer.OrdinalIgnoreCase))
            .ToList();
        Assert.True(extras.Count == 0, $"Unexpected properties with $select: {string.Join(", ", extras)}");
        Assert.True(obj.ContainsKey("id"), "id must always be present");
    }

    public static void GraphError(ODataError error, int status, string code)
    {
        Assert.Equal(status, error.ResponseStatusCode);
        Assert.NotNull(error.Error);
        Assert.Equal(code, error.Error!.Code);
        Assert.False(string.IsNullOrEmpty(error.Error.Message));
        Assert.False(string.IsNullOrEmpty(error.Error.InnerError?.RequestId));
    }
}

internal static class PagingAssert
{
    /// <summary>Follows nextLinks with the SDK's PageIterator, asserting ids are unique across pages (capped at <paramref name="max"/> items).</summary>
    public static async Task FollowAsync<TItem, TPage>(ContractTarget target, TPage firstPage, int max, Func<TItem, string?> id)
        where TPage : Microsoft.Kiota.Abstractions.Serialization.IParsable, Microsoft.Kiota.Abstractions.Serialization.IAdditionalDataHolder, new()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var iterator = PageIterator<TItem, TPage>.CreatePageIterator(target.Client, firstPage, item =>
        {
            var key = id(item);
            Assert.False(string.IsNullOrEmpty(key));
            Assert.True(seen.Add(key!), $"Duplicate id across pages: {key}");
            return seen.Count < max;
        });
        await iterator.IterateAsync();
        Assert.True(seen.Count >= 2, "Paging must yield more than the first page's single item.");
    }
}