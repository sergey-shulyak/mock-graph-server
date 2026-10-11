using System.Net.Http.Headers;
using System.Text.Json.Nodes;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Graph;
using Microsoft.Graph.Models;
using Microsoft.Graph.Models.ODataErrors;
using Microsoft.Kiota.Abstractions.Authentication;
using Microsoft.Kiota.Http.HttpClientLibrary;

namespace MockGraph.ContractTests;

/// <summary>
/// The system under test. With no environment it boots the mock in-process (token <c>app</c>); with
/// <c>GRAPH_BASE_URL</c> + <c>GRAPH_TOKEN</c> it talks to a real endpoint. The token is never logged or exposed in messages.
/// </summary>
public sealed class ContractTarget : IAsyncLifetime
{
    private const string MockBaseUrl = "http://localhost/v1.0";

    private WebApplicationFactory<Program>? _factory;
    private HttpClient _rawHttp = null!;
    private string _token = "";

    public bool IsMock { get; private set; }

    public string BaseUrl { get; private set; } = MockBaseUrl;

    public GraphServiceClient Client { get; private set; } = null!;

    public string? SiteId { get; private set; }

    public string? DriveId { get; private set; }

    public string? UserId { get; private set; }

    public string? GroupId { get; private set; }

    public async Task InitializeAsync()
    {
        var baseUrl = Env("GRAPH_BASE_URL");
        var token = Env("GRAPH_TOKEN");
        if ((baseUrl is null) != (token is null))
        {
            throw new InvalidOperationException("Set both GRAPH_BASE_URL and GRAPH_TOKEN to test a real endpoint, or neither to test the in-process mock.");
        }

        HttpClient sdkHttp;
        if (baseUrl is null)
        {
            IsMock = true;
            _token = "app";
            _factory = new WebApplicationFactory<Program>();
            sdkHttp = _factory.CreateClient();
            _rawHttp = _factory.CreateClient();
        }
        else
        {
            BaseUrl = baseUrl.TrimEnd('/');
            _token = token!;
            // GraphClientFactory adds the SDK's default middleware (redirects, retry on 429/503).
            sdkHttp = GraphClientFactory.Create();
            _rawHttp = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
        }

        var auth = new BaseBearerTokenAuthenticationProvider(new StaticTokenProvider(_token));
        Client = new GraphServiceClient(new HttpClientRequestAdapter(auth, httpClient: sdkHttp) { BaseUrl = BaseUrl });

        await DiscoverAsync();
    }

    public Task DisposeAsync()
    {
        _rawHttp.Dispose();
        _factory?.Dispose();
        return Task.CompletedTask;
    }

    /// <summary>Raw GET through the same bearer token (for wire-level checks the SDK models hide). Pass null token for anonymous.</summary>
    public async Task<(int Status, JsonNode? Body)> RawGetAsync(string pathOrUrl, bool authenticated = true, string? prefer = null)
    {
        var url = pathOrUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? pathOrUrl : BaseUrl + pathOrUrl;
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (authenticated)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
        }

        if (prefer is not null)
        {
            request.Headers.TryAddWithoutValidation("Prefer", prefer);
        }

        using var response = await _rawHttp.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        JsonNode? body = null;
        if (!string.IsNullOrWhiteSpace(text))
        {
            try
            {
                body = JsonNode.Parse(text);
            }
            catch (System.Text.Json.JsonException)
            {
                // Non-JSON body: leave null, the caller asserts on status.
            }
        }

        return ((int)response.StatusCode, body);
    }

    private async Task DiscoverAsync()
    {
        UserId = Env("GRAPH_USER_ID") ?? (await Client.Users.GetAsync(r => r.QueryParameters.Top = 1))?.Value?.FirstOrDefault()?.Id;
        GroupId = Env("GRAPH_GROUP_ID") ?? (await Client.Groups.GetAsync(r => r.QueryParameters.Top = 1))?.Value?.FirstOrDefault()?.Id;
        SiteId = Env("GRAPH_SITE_ID");
        DriveId = Env("GRAPH_DRIVE_ID");

        SiteId ??= await FirstSiteIdAsync();
        if (DriveId is null && SiteId is not null)
        {
            DriveId = (await Client.Sites[SiteId].Drives.GetAsync())?.Value?.FirstOrDefault()?.Id;
        }
    }

    private async Task<string?> FirstSiteIdAsync()
    {
        try
        {
            return (await Client.Sites.GetAsync(r => r.QueryParameters.Top = 1))?.Value?.FirstOrDefault()?.Id;
        }
        catch (ODataError)
        {
            // Real Graph: plain GET /sites is not a supported listing for app-only callers; the documented form needs search=*.
        }

        try
        {
            return (await Client.Sites.GetAsync(r => r.QueryParameters.Search = "*"))?.Value?.FirstOrDefault()?.Id;
        }
        catch (ODataError)
        {
            return null; // No discoverable site: site/drive tests skip unless GRAPH_SITE_ID / GRAPH_DRIVE_ID are given.
        }
    }

    private static string? Env(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
}

[CollectionDefinition(Name)]
public sealed class ContractCollection : ICollectionFixture<ContractTarget>
{
    public const string Name = "contract";
}

internal sealed class StaticTokenProvider(string token) : IAccessTokenProvider
{
    public AllowedHostsValidator AllowedHostsValidator { get; } = new();

    public Task<string> GetAuthorizationTokenAsync(
        Uri uri, Dictionary<string, object>? additionalAuthenticationContext = null, CancellationToken cancellationToken = default) =>
        Task.FromResult(token);
}