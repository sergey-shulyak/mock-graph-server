namespace MockGraph.Server.Serialization;

public sealed class GraphException(int statusCode, string code, string message) : Exception(message)
{
    public int StatusCode { get; } = statusCode;

    public string Code { get; } = code;

    public static GraphException BadRequest(string message) => new(StatusCodes.Status400BadRequest, "BadRequest", message);

    public static GraphException NotFound(string code, string message) => new(StatusCodes.Status404NotFound, code, message);

    public static GraphException Unauthorized(string message) => new(StatusCodes.Status401Unauthorized, "InvalidAuthenticationToken", message);

    public static GraphException ItemNotFound() => new(
        StatusCodes.Status404NotFound,
        "itemNotFound",
        "The resource could not be found.");

    public static GraphException ResourceNotFound(string id) => new(
        StatusCodes.Status404NotFound,
        "Request_ResourceNotFound",
        $"Resource '{id}' does not exist or one of its queried reference-property objects are not present.");
}