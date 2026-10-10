using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Graph;
using Microsoft.Kiota.Abstractions.Authentication;
using Microsoft.Kiota.Http.HttpClientLibrary;

using MockGraph.Server.Options;

namespace MockGraph.Tests;

public sealed class MockGraphFactory : WebApplicationFactory<Program>
{
    public GraphServiceClient Client(string token) =>
        TestSupport.CreateClient(CreateClient(), token);
}

public static class TestSupport
{
    public const string BaseUrl = "http://localhost/v1.0";

    public static WebApplicationFactory<Program> WithPageSize(this WebApplicationFactory<Program> factory, int pageSize) =>
        factory.WithWebHostBuilder(b => b.ConfigureServices(s =>
            s.PostConfigure<MockGraphOptions>(o => o.DefaultPageSize = pageSize)));

    public static GraphServiceClient CreateClient(HttpClient http, string token)
    {
        var auth = new BaseBearerTokenAuthenticationProvider(new StaticTokenProvider(token));
        var adapter = new HttpClientRequestAdapter(auth, httpClient: http) { BaseUrl = BaseUrl };
        return new GraphServiceClient(adapter);
    }

    public static GraphServiceClient Client(this WebApplicationFactory<Program> factory, string token) =>
        CreateClient(factory.CreateClient(), token);
}

internal sealed class StaticTokenProvider(string token) : IAccessTokenProvider
{
    public AllowedHostsValidator AllowedHostsValidator { get; } = new();

    public Task<string> GetAuthorizationTokenAsync(
        Uri uri, Dictionary<string, object>? additionalAuthenticationContext = null, CancellationToken cancellationToken = default) =>
        Task.FromResult(token);
}