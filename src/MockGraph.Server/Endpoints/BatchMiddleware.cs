using System.Text.Json;
using System.Text.Json.Nodes;

using Microsoft.AspNetCore.WebUtilities;

using MockGraph.Server.Auth;
using MockGraph.Server.Serialization;

namespace MockGraph.Server.Endpoints;

/// <summary>
/// <c>POST /v1.0/$batch</c>: runs up to 20 GET sub-requests through the normal routing and endpoint pipeline with the
/// caller's identity and returns Graph's <c>responses[]</c> envelope. Sits before routing so each sub-request is routed itself.
/// </summary>
internal sealed class BatchMiddleware(RequestDelegate next, ThrottleState throttle)
{
    private const int MaxRequests = 20;

    public static bool IsBatch(HttpRequest request) =>
        HttpMethods.IsPost(request.Method) && request.Path.Equals("/v1.0/$batch", StringComparison.OrdinalIgnoreCase);

    public async Task InvokeAsync(HttpContext context)
    {
        if (!IsBatch(context.Request))
        {
            await next(context);
            return;
        }

        var requests = await ReadRequests(context.Request);
        var results = new Dictionary<string, JsonObject>(StringComparer.OrdinalIgnoreCase);
        var inProgress = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        async Task<JsonObject> Run(JsonObject req)
        {
            var id = req["id"]!.GetValue<string>();
            if (results.TryGetValue(id, out var done))
            {
                return done;
            }

            if (!inProgress.Add(id))
            {
                throw GraphException.BadRequest($"Batch request '{id}' is part of a dependsOn cycle.");
            }

            foreach (var dependency in req["dependsOn"]?.AsArray().Select(d => d?.GetValue<string>()) ?? [])
            {
                var target = requests.FirstOrDefault(r => string.Equals(r["id"]!.GetValue<string>(), dependency, StringComparison.OrdinalIgnoreCase))
                    ?? throw GraphException.BadRequest($"Batch request '{id}' depends on unknown request '{dependency}'.");
                if ((int)(await Run(target))["status"]! >= 400)
                {
                    return results[id] = await ErrorResponse(context, id, 424, "FailedDependency", $"The request depends on '{dependency}', which failed.");
                }
            }

            return results[id] = await Execute(context, req);
        }

        foreach (var request in requests)
        {
            await Run(request);
        }

        var body = new JsonObject
        {
            ["responses"] = new JsonArray([.. requests.Select(r => (JsonNode)results[r["id"]!.GetValue<string>()])]),
        };
        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsync(body.ToJsonString());
    }

    private static async Task<List<JsonObject>> ReadRequests(HttpRequest request)
    {
        JsonNode? root;
        try
        {
            root = await JsonNode.ParseAsync(request.Body);
        }
        catch (JsonException ex)
        {
            throw GraphException.BadRequest($"Invalid JSON body: {ex.Message}");
        }

        if (root is not JsonObject { } obj || obj["requests"] is not JsonArray array)
        {
            throw GraphException.BadRequest("The batch body must be a JSON object with a 'requests' array.");
        }

        if (array.Count > MaxRequests)
        {
            throw GraphException.BadRequest($"Batch request has {array.Count} requests; the maximum is {MaxRequests}.");
        }

        var list = new List<JsonObject>();
        foreach (var node in array)
        {
            if (node is not JsonObject item
                || item["id"] is not JsonValue idValue || !idValue.TryGetValue<string>(out var id) || string.IsNullOrWhiteSpace(id)
                || item["method"] is not JsonValue || item["url"] is not JsonValue)
            {
                throw GraphException.BadRequest("Every batch request needs a string 'id', 'method' and 'url'.");
            }

            if (list.Any(r => string.Equals(r["id"]!.GetValue<string>(), id, StringComparison.OrdinalIgnoreCase)))
            {
                throw GraphException.BadRequest($"Batch request id '{id}' is not unique.");
            }

            list.Add(item);
        }

        return list;
    }

    private async Task<JsonObject> Execute(HttpContext outer, JsonObject req)
    {
        var id = req["id"]!.GetValue<string>();
        var method = req["method"]!.GetValue<string>().ToUpperInvariant();
        if (method != HttpMethods.Get)
        {
            return await ErrorResponse(
                outer, id, method is "POST" or "PUT" or "PATCH" or "DELETE" ? 405 : 400, "BadRequest",
                method is "POST" or "PUT" or "PATCH" or "DELETE"
                    ? $"{method} is not supported by this server. Batches may only contain GET requests; mutations go through /_admin."
                    : $"Unknown HTTP method '{method}'.");
        }

        var url = req["url"]!.GetValue<string>().Trim();
        var path = url.TrimStart('/');
        if (path.StartsWith("v1.0/", StringComparison.OrdinalIgnoreCase))
        {
            path = path["v1.0/".Length..];
        }

        var split = path.IndexOf('?');
        var (rawPath, rawQuery) = split < 0 ? (path, "") : (path[..split], path[split..]);

        var sub = new DefaultHttpContext { RequestServices = outer.RequestServices, RequestAborted = outer.RequestAborted };
        sub.Items[CallerContext.ItemKey] = outer.GetCaller();
        sub.Items[GraphErrors.RequestIdKey] = Guid.NewGuid().ToString();
        sub.Request.Method = HttpMethods.Get;
        sub.Request.Scheme = outer.Request.Scheme;
        sub.Request.Host = outer.Request.Host;
        sub.Request.PathBase = outer.Request.PathBase;
        sub.Request.Path = new PathString("/v1.0/" + DecodeKeepingSlashes(rawPath));
        sub.Request.QueryString = new QueryString(rawQuery);
        if (req["headers"] is JsonObject headers)
        {
            foreach (var (name, value) in headers)
            {
                sub.Request.Headers[name] = value?.ToString();
            }
        }

        using var buffer = new MemoryStream();
        sub.Response.Body = buffer;
        try
        {
            if (throttle.TryConsume() is { } retryAfter)
            {
                await ThrottleState.WriteResponseAsync(sub, retryAfter);
            }
            else
            {
                await next(sub);
            }
        }
        catch (GraphException ex)
        {
            buffer.SetLength(0);
            sub.Response.Headers.Clear();
            await GraphErrors.WriteAsync(sub, ex.StatusCode, ex.Code, ex.Message);
        }
        catch (Exception) when (!outer.RequestAborted.IsCancellationRequested)
        {
            buffer.SetLength(0);
            await GraphErrors.WriteAsync(sub, 500, "generalException", "An unexpected error occurred.");
        }

        return ToResponse(id, sub, buffer.ToArray());
    }

    private static async Task<JsonObject> ErrorResponse(HttpContext outer, string id, int status, string code, string message)
    {
        var sub = new DefaultHttpContext { RequestServices = outer.RequestServices };
        sub.Items[GraphErrors.RequestIdKey] = Guid.NewGuid().ToString();
        using var buffer = new MemoryStream();
        sub.Response.Body = buffer;
        await GraphErrors.WriteAsync(sub, status, code, message);
        return ToResponse(id, sub, buffer.ToArray());
    }

    private static JsonObject ToResponse(string id, HttpContext sub, byte[] bytes)
    {
        var headers = new JsonObject();
        foreach (var (name, values) in sub.Response.Headers)
        {
            if (!name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
            {
                headers[name] = values.ToString();
            }
        }

        if (sub.Response.ContentType is { } contentType)
        {
            headers["Content-Type"] = contentType;
        }

        var response = new JsonObject { ["id"] = id, ["status"] = sub.Response.StatusCode, ["headers"] = headers };
        if (bytes.Length > 0)
        {
            // JSON bodies are embedded as JSON; anything else (file bytes) as base64url, like Graph.
            response["body"] = (sub.Response.ContentType ?? "").Contains("json", StringComparison.OrdinalIgnoreCase)
                ? JsonNode.Parse(bytes)
                : JsonValue.Create(WebEncoders.Base64UrlEncode(bytes));
        }

        return response;
    }

    /// <summary>Decodes percent-escapes except %2F, the way Kestrel presents the request path.</summary>
    private static string DecodeKeepingSlashes(string path) =>
        string.Join("%2F", path.Split("%2F", StringSplitOptions.None).Select(Uri.UnescapeDataString));
}