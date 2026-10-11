using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Graph;
using Microsoft.Graph.Drives.Item.Items.Item.Delta;
using Microsoft.Graph.Models;

using MockGraph.Server.State;

namespace MockGraph.Tests;

/// <summary>An isolated server per test class instance (xUnit creates one instance per test), so mutations never leak.</summary>
public abstract class IsolatedServerTest : IDisposable
{
    protected static readonly string DriveId = Ids.Drive("kb-docs");

    private readonly MockGraphFactory _factory = new();
    private readonly HttpClient _http;

    protected HttpClient Http => _http;

    protected MockGraphFactory Factory => _factory;

    protected IsolatedServerTest()
    {
        _http = _factory.CreateClient();
    }

    protected GraphStore Store => _factory.Services.GetRequiredService<GraphStore>();

    protected static string IdOf(string path) => Ids.Item(DriveId, path);

    protected GraphServiceClient As(string token) => _factory.Client(token);

    public void Dispose()
    {
        _http.Dispose();
        _factory.Dispose();
        GC.SuppressFinalize(this);
    }

    // ---- /_admin (raw HTTP) ----

    protected async Task<HttpResponseMessage> Send(HttpMethod method, string url, object? body = null)
    {
        using var request = new HttpRequestMessage(method, url);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        return await _http.SendAsync(request);
    }

    protected async Task<JsonNode> Admin(HttpMethod method, string url, object? body = null, HttpStatusCode expected = HttpStatusCode.OK)
    {
        using var response = await Send(method, url, body);
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == expected, $"{method} {url} -> {(int)response.StatusCode} {text}");
        return string.IsNullOrEmpty(text) ? new JsonObject() : JsonNode.Parse(text)!;
    }

    protected Task Grant(string path, string principal, string role = "read") =>
        Admin(HttpMethod.Post, "/_admin/permissions", new { itemPath = path, principal, role });

    protected Task Revoke(string path, string principal) =>
        Admin(HttpMethod.Delete, "/_admin/permissions", new { itemPath = path, principal });

    protected Task AddMember(string group, string member) =>
        Admin(HttpMethod.Post, $"/_admin/groups/{group}/members", new { member });

    protected Task RemoveMember(string group, string member) =>
        Admin(HttpMethod.Delete, $"/_admin/groups/{group}/members", new { member });

    protected Task<JsonNode> PutFile(string path, string content, HttpStatusCode expected = HttpStatusCode.Created) =>
        PutBytes(path, Encoding.UTF8.GetBytes(content), expected);

    protected async Task<JsonNode> PutBytes(string path, byte[] content, HttpStatusCode expected = HttpStatusCode.Created)
    {
        using var form = new MultipartFormDataContent
        {
            { new StringContent(path), "path" },
            { new ByteArrayContent(content), "file", "upload.bin" },
        };
        using var response = await _http.PostAsync("/_admin/files", form);
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == expected, $"POST /_admin/files -> {(int)response.StatusCode} {text}");
        return JsonNode.Parse(text)!;
    }

    protected Task DeleteFile(string path) => Admin(HttpMethod.Delete, $"/_admin/files?path={Uri.EscapeDataString(path)}");

    // ---- raw Graph calls ----

    protected async Task<JsonNode> RawGet(string token, string url, string? prefer = null, HttpStatusCode expected = HttpStatusCode.OK)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (prefer is not null)
        {
            request.Headers.TryAddWithoutValidation("Prefer", prefer);
        }

        using var response = await _http.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == expected, $"GET {url} -> {(int)response.StatusCode} {text}");
        return JsonNode.Parse(text)!;
    }

    /// <summary>Follows nextLinks over raw JSON until a deltaLink; returns all value entries and the deltaLink.</summary>
    protected async Task<(List<JsonObject> Items, string DeltaLink)> RawDrain(string token, string url, string? prefer = null)
    {
        var items = new List<JsonObject>();
        for (var i = 0; i < 50; i++)
        {
            var page = await RawGet(token, url, prefer);
            items.AddRange(page["value"]!.AsArray().Select(n => n!.AsObject()));
            if (page["@odata.deltaLink"] is { } deltaLink)
            {
                return (items, deltaLink.GetValue<string>());
            }

            url = page["@odata.nextLink"]!.GetValue<string>();
        }

        throw new InvalidOperationException("Too many pages.");
    }

    // ---- SDK delta ----

    protected static async Task<DeltaResult> Drain(GraphServiceClient client, string? deltaLink = null, string? prefer = null)
    {
        var items = new List<DriveItem>();
        var pages = 0;
        var builder = client.Drives[DriveId].Items["root"].Delta;
        if (deltaLink is not null)
        {
            builder = builder.WithUrl(deltaLink);
        }

        while (true)
        {
            var page = await builder.GetAsDeltaGetResponseAsync(r =>
            {
                if (prefer is not null)
                {
                    r.Headers.Add("Prefer", prefer);
                }
            });
            pages++;
            items.AddRange(page!.Value!);
            if (page.OdataDeltaLink is { } done)
            {
                return new DeltaResult(items, done, pages);
            }

            builder = client.Drives[DriveId].Items["root"].Delta.WithUrl(page.OdataNextLink!);
        }
    }

    protected const string SharingPrefer = "deltashowsharingchanges";

    protected static bool IsShared(DriveItem item) => item.AdditionalData.ContainsKey("@microsoft.graph.sharedChanged");

    protected static JsonObject? ById(IEnumerable<JsonObject> items, string path) =>
        items.FirstOrDefault(i => i["id"]!.GetValue<string>() == IdOf(path));

    protected static async Task<string[]> OpenablePaths(GraphServiceClient client, params string[] paths)
    {
        var ok = new List<string>();
        foreach (var path in paths)
        {
            try
            {
                await client.Drives[DriveId].Items[IdOf(path)].GetAsync();
                ok.Add(path);
            }
            catch (Microsoft.Graph.Models.ODataErrors.ODataError e) when (e.ResponseStatusCode == 404)
            {
            }
        }

        return [.. ok];
    }
}

public sealed record DeltaResult(List<DriveItem> Items, string DeltaLink, int Pages)
{
    public string[] Ids => [.. Items.Select(i => i.Id!)];
}