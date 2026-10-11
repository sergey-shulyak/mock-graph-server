using System.Text.Json;

using Microsoft.AspNetCore.WebUtilities;

namespace MockGraph.Server.Endpoints;

/// <summary>Opaque tokens for the unauthenticated /_download/{token} URLs (stand-ins for Graph's pre-authenticated URLs).</summary>
internal static class DownloadTokens
{
    private sealed record Payload(string D, string I, string E);

    public static string Encode(string driveKey, string itemId, string epoch) =>
        WebEncoders.Base64UrlEncode(JsonSerializer.SerializeToUtf8Bytes(new Payload(driveKey, itemId, epoch)));

    public static (string DriveKey, string ItemId, string Epoch)? Decode(string token)
    {
        try
        {
            var payload = JsonSerializer.Deserialize<Payload>(WebEncoders.Base64UrlDecode(token));
            return payload is { D: not null, I: not null, E: not null } ? (payload.D, payload.I, payload.E) : null;
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
            return null;
        }
    }
}