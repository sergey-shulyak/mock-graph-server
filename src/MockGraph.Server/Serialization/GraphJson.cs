using System.Text.Json.Nodes;

using Microsoft.Kiota.Abstractions.Serialization;
using Microsoft.Kiota.Serialization.Json;

using MockGraph.Server.OData;

namespace MockGraph.Server.Serialization;

public static class GraphJson
{
    public const string ContentType = "application/json;odata.metadata=minimal;odata.streaming=true;IEEE754Compatible=false;charset=utf-8";

    public static byte[] Serialize(IParsable value, IReadOnlyList<string>? select = null)
    {
        using var writer = new JsonSerializationWriter();
        writer.WriteObjectValue(null, value);
        using var stream = writer.GetSerializedContent();
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);

        if (select is null)
        {
            return buffer.ToArray();
        }

        var root = JsonNode.Parse(buffer.ToArray())!;
        if (root["value"] is JsonArray items)
        {
            foreach (var item in items.OfType<JsonObject>())
            {
                SelectFilter.Apply(item, select);
            }
        }
        else if (root is JsonObject obj)
        {
            SelectFilter.Apply(obj, select);
        }

        return System.Text.Encoding.UTF8.GetBytes(root.ToJsonString());
    }
}

public sealed class GraphJsonResult(IParsable body, ODataQuery? query = null, int statusCode = 200) : IResult
{
    public async Task ExecuteAsync(HttpContext httpContext)
    {
        var bytes = GraphJson.Serialize(body, query?.Select);
        httpContext.Response.StatusCode = statusCode;
        httpContext.Response.ContentType = GraphJson.ContentType;
        httpContext.Response.ContentLength = bytes.Length;
        await httpContext.Response.Body.WriteAsync(bytes);
    }
}