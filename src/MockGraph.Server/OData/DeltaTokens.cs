using System.Text.Json;

using Microsoft.AspNetCore.WebUtilities;

using MockGraph.Server.Serialization;

namespace MockGraph.Server.OData;

/// <summary>
/// Opaque delta/skip token (base64url JSON). S = sequence the client is synced to, T = sequence this enumeration is
/// pinned at, O = page offset, I = initial full enumeration, P = mid-enumeration (nextLink) vs final (deltaLink),
/// D = drive key, Sc = scope item id.
/// </summary>
public sealed record DeltaToken(string K, string E, long S, long T, int O, bool I, bool P, string? D = null, string? Sc = null)
{
    public const string Drive = "drive";
    public const string Groups = "groups";

    public string Encode() => WebEncoders.Base64UrlEncode(JsonSerializer.SerializeToUtf8Bytes(this));

    /// <summary>Decodes and validates a token; a token from another epoch (after reset/restart) is 410 resyncRequired.</summary>
    public static DeltaToken Decode(string text, string kind, string epoch, long currentSequence)
    {
        DeltaToken? token = null;
        try
        {
            token = JsonSerializer.Deserialize<DeltaToken>(WebEncoders.Base64UrlDecode(text));
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
        }

        if (token is null || token.K != kind || token.O < 0)
        {
            throw GraphException.BadRequest("Invalid delta token.");
        }

        return token.E != epoch || token.S > currentSequence || token.T > currentSequence
            ? throw new GraphException(
                StatusCodes.Status410Gone,
                "resyncRequired",
                "The delta token is no longer valid because the server state was reset. Start a new enumeration without a token.")
            : token;
    }
}