using MockGraph.Server.Serialization;

namespace MockGraph.Server.Endpoints;

/// <summary>Admin-controlled 429 simulation: the next N Graph requests are answered with TooManyRequests and a Retry-After header.</summary>
public sealed class ThrottleState
{
    private readonly Lock _gate = new();
    private int _remaining;
    private int _retryAfterSeconds;

    public void Set(int count, int retryAfterSeconds)
    {
        lock (_gate)
        {
            (_remaining, _retryAfterSeconds) = (count, retryAfterSeconds);
        }
    }

    public void Clear() => Set(0, 0);

    public int Remaining
    {
        get
        {
            lock (_gate)
            {
                return _remaining;
            }
        }
    }

    /// <summary>Consumes one throttled request; returns the Retry-After value, or null when the request may proceed.</summary>
    public int? TryConsume()
    {
        lock (_gate)
        {
            if (_remaining <= 0)
            {
                return null;
            }

            _remaining--;
            return _retryAfterSeconds;
        }
    }

    /// <summary>Error body as documented for Graph throttling (code TooManyRequests, innerError with code/message/status).</summary>
    public static Task WriteResponseAsync(HttpContext context, int retryAfterSeconds)
    {
        context.Response.Headers["Retry-After"] = retryAfterSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return GraphErrors.WriteAsync(
            context,
            StatusCodes.Status429TooManyRequests,
            "TooManyRequests",
            "Please retry again later.",
            new Dictionary<string, object> { ["code"] = "429", ["message"] = "Please retry after", ["status"] = "429" });
    }
}

internal sealed class ThrottleMiddleware(RequestDelegate next, ThrottleState throttle)
{
    public async Task InvokeAsync(HttpContext context)
    {
        // The batch envelope itself is never throttled; each sub-request is checked in BatchMiddleware.
        if (context.Request.Path.StartsWithSegments("/v1.0") && !BatchMiddleware.IsBatch(context.Request)
            && throttle.TryConsume() is { } retryAfter)
        {
            await ThrottleState.WriteResponseAsync(context, retryAfter);
            return;
        }

        await next(context);
    }
}