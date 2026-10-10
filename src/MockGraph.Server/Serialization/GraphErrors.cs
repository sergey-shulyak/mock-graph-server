using Microsoft.Graph.Models.ODataErrors;

namespace MockGraph.Server.Serialization;

public static class GraphErrors
{
    public const string RequestIdKey = "mockgraph.request-id";

    public static async Task WriteAsync(HttpContext context, int status, string code, string message)
    {
        var requestId = context.Items[RequestIdKey] as string ?? Guid.NewGuid().ToString();
        var error = new ODataError
        {
            Error = new MainError
            {
                Code = code,
                Message = message,
                InnerError = new InnerError
                {
                    RequestId = requestId,
                    Date = DateTimeOffset.UtcNow,
                    ClientRequestId = context.Request.Headers["client-request-id"].FirstOrDefault() ?? requestId,
                },
            },
        };

        var bytes = GraphJson.Serialize(error);
        context.Response.StatusCode = status;
        context.Response.ContentType = GraphJson.ContentType;
        context.Response.ContentLength = bytes.Length;
        await context.Response.Body.WriteAsync(bytes);
    }
}

internal sealed class GraphErrorMiddleware(RequestDelegate next, ILogger<GraphErrorMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var requestId = Guid.NewGuid().ToString();
        context.Items[GraphErrors.RequestIdKey] = requestId;
        context.Response.OnStarting(() =>
        {
            context.Response.Headers["request-id"] = requestId;
            return Task.CompletedTask;
        });

        try
        {
            await next(context);
        }
        catch (GraphException ex) when (!context.Response.HasStarted)
        {
            context.Response.Clear();
            await GraphErrors.WriteAsync(context, ex.StatusCode, ex.Code, ex.Message);
        }
        catch (Exception ex) when (!context.Response.HasStarted && context.Request.Path.StartsWithSegments("/v1.0"))
        {
            logger.LogError(ex, "Unhandled error");
            context.Response.Clear();
            await GraphErrors.WriteAsync(context, 500, "generalException", "An unexpected error occurred.");
        }
    }
}