using System.Security.Cryptography;
using System.Text;

using Microsoft.AspNetCore.WebUtilities;

namespace MockGraph.Server.State;

public static class Ids
{
    private static readonly Guid Namespace = new("6f1c2a4e-5b7d-4c3a-9e21-0d8f4b6a7c10");

    public static string User(string key) => Uuid5($"user:{key}").ToString();

    public static string Group(string key) => Uuid5($"group:{key}").ToString();

    public static string Site(string tenantDomain, string key)
    {
        var host = tenantDomain.Split('.')[0] + ".sharepoint.com";
        return $"{host},{Uuid5($"site:{key}")},{Uuid5($"web:{key}")}";
    }

    public static string Drive(string key)
    {
        var bytes = Uuid5($"drive:{key}").ToByteArray();
        return "b!" + WebEncoders.Base64UrlEncode([.. bytes, .. bytes]);
    }

    public static string Item(string driveId, string path) =>
        "01" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{driveId}:{path.ToLowerInvariant()}")).AsSpan(0, 16));

    public static string Permission(string itemId, string principal, string role) =>
        WebEncoders.Base64UrlEncode(SHA256.HashData(Encoding.UTF8.GetBytes($"{itemId}|{principal}|{role}")).AsSpan(0, 12));

    private static Guid Uuid5(string name)
    {
        var nameBytes = Encoding.UTF8.GetBytes(name);
        var input = new byte[16 + nameBytes.Length];
        Namespace.TryWriteBytes(input, bigEndian: true, out _);
        nameBytes.CopyTo(input, 16);

        var hash = SHA1.HashData(input);
        var bytes = hash.AsSpan(0, 16).ToArray();
        bytes[6] = (byte)((bytes[6] & 0x0F) | 0x50);
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);
        return new Guid(bytes, bigEndian: true);
    }
}