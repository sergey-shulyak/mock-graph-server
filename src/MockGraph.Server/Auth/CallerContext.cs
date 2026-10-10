using MockGraph.Server.Serialization;
using MockGraph.Server.State;

namespace MockGraph.Server.Auth;

public abstract record CallerContext
{
    public const string ItemKey = "mockgraph.caller";
}

public sealed record AppCaller : CallerContext;

public sealed record UserCaller(UserRecord User) : CallerContext;

public static class CallerExtensions
{
    public static CallerContext GetCaller(this HttpContext context) =>
        context.Items[CallerContext.ItemKey] as CallerContext ?? throw GraphException.Unauthorized("Access token is empty.");
}

internal sealed class AuthMiddleware(RequestDelegate next, GraphStore store)
{
    public async Task InvokeAsync(HttpContext context)
    {
        if (context.Request.Path.StartsWithSegments("/v1.0"))
        {
            context.Items[CallerContext.ItemKey] = Authenticate(context.Request);
        }

        await next(context);
    }

    private CallerContext Authenticate(HttpRequest request)
    {
        var header = request.Headers.Authorization.ToString();
        if (string.IsNullOrWhiteSpace(header))
        {
            throw GraphException.Unauthorized("Access token is empty.");
        }

        const string prefix = "Bearer ";
        if (!header.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw GraphException.Unauthorized("Authorization header must use the Bearer scheme.");
        }

        var token = header[prefix.Length..].Trim();
        if (token == "app")
        {
            return new AppCaller();
        }

        if (token.StartsWith("user:", StringComparison.Ordinal))
        {
            var user = store.FindUser(token["user:".Length..]);
            return user is null
                ? throw GraphException.Unauthorized($"Unknown user in token '{token}'.")
                : new UserCaller(user);
        }

        throw GraphException.Unauthorized("Invalid token. Expected 'app' or 'user:<id|upn>'.");
    }
}