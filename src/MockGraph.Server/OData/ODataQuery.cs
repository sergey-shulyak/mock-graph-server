using System.Text.Json;

using Microsoft.AspNetCore.WebUtilities;

using MockGraph.Server.Options;
using MockGraph.Server.Serialization;

namespace MockGraph.Server.OData;

public sealed record ODataQuery(int? Top, string? SelectRaw, string? SkipToken)
{
    public IReadOnlyList<string>? Select => SelectRaw is null
        ? null
        : SelectRaw.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

    public static ODataQuery Parse(HttpRequest request, bool collection, MockGraphOptions options)
    {
        int? top = null;
        string? select = null;
        string? skipToken = null;

        foreach (var (key, values) in request.Query)
        {
            var value = values.FirstOrDefault() ?? "";
            if (key.Equals("$select", StringComparison.OrdinalIgnoreCase))
            {
                select = value;
            }
            else if (collection && key.Equals("$top", StringComparison.OrdinalIgnoreCase))
            {
                top = int.TryParse(value, out var parsed) && parsed >= 1 && parsed <= options.MaxPageSize
                    ? parsed
                    : throw GraphException.BadRequest($"Invalid value '{value}' for $top. Expected an integer between 1 and {options.MaxPageSize}.");
            }
            else if (collection && key.Equals("$skiptoken", StringComparison.OrdinalIgnoreCase))
            {
                skipToken = value;
            }
            else
            {
                throw GraphException.BadRequest($"Query option '{key}' is not supported by this endpoint.");
            }
        }

        if (select is not null && select.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Length == 0)
        {
            throw GraphException.BadRequest("$select must name at least one property.");
        }

        return new ODataQuery(top, select, skipToken);
    }
}

public static class Paging
{
    private sealed record Token(int O, string E);

    public static (List<T> Items, string? NextLink) Page<T>(
        IReadOnlyList<T> all, ODataQuery query, HttpRequest request, MockGraphOptions options, string epoch)
    {
        var offset = 0;
        if (query.SkipToken is not null)
        {
            offset = Decode(query.SkipToken, epoch);
        }

        var size = query.Top ?? options.DefaultPageSize;
        var items = all.Skip(offset).Take(size).ToList();
        var next = offset + items.Count;
        if (next >= all.Count)
        {
            return (items, null);
        }

        var url = $"{BaseUrl(request)}{request.PathBase.Add(request.Path).ToUriComponent()}";
        var parts = new List<string>();
        if (query.Top is { } top)
        {
            parts.Add($"$top={top}");
        }

        if (query.SelectRaw is not null)
        {
            parts.Add($"$select={Uri.EscapeDataString(query.SelectRaw)}");
        }

        parts.Add($"$skiptoken={Encode(next, epoch)}");
        return (items, $"{url}?{string.Join('&', parts)}");
    }

    public static string BaseUrl(HttpRequest request) => $"{request.Scheme}://{request.Host}{request.PathBase}";

    private static string Encode(int offset, string epoch) =>
        WebEncoders.Base64UrlEncode(JsonSerializer.SerializeToUtf8Bytes(new Token(offset, epoch)));

    private static int Decode(string text, string epoch)
    {
        try
        {
            var token = JsonSerializer.Deserialize<Token>(WebEncoders.Base64UrlDecode(text));
            if (token is not null && token.E == epoch && token.O >= 0)
            {
                return token.O;
            }
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
        }

        throw GraphException.BadRequest("Invalid $skiptoken.");
    }
}