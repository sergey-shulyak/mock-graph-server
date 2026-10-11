using System.Diagnostics;
using System.Net;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Graph;
using Microsoft.Graph.Models.ODataErrors;
using Microsoft.Kiota.Abstractions.Authentication;
using Microsoft.Kiota.Http.HttpClientLibrary;

using MockGraph.Server.Endpoints;

namespace MockGraph.Tests;

public sealed class ThrottleTests : IsolatedServerTest
{
    private Task<RawResponse> Users() => RawGraph.Get(Http, "app", "/v1.0/users");

    [Fact]
    public async Task NextNRequestsGet429_WithRetryAfterAndTheDocumentedBody()
    {
        await Admin(HttpMethod.Post, "/_admin/throttle", new { count = 2, retryAfterSeconds = 7 });

        for (var i = 0; i < 2; i++)
        {
            var throttled = await Users();
            Assert.Equal(429, throttled.Status);
            Assert.Equal("7", Assert.Single(throttled.Headers.GetValues("Retry-After")));
            Assert.Equal("TooManyRequests", (string?)throttled.Body!["error"]!["code"]);
            Assert.Equal("Please retry again later.", (string?)throttled.Body["error"]!["message"]);
            var inner = throttled.Body["error"]!["innerError"]!;
            Assert.Equal("429", (string?)inner["status"]);
            Assert.NotNull(inner["request-id"]);
            Assert.NotNull(inner["date"]);
        }

        Assert.Equal(200, (await Users()).Status);
    }

    [Fact]
    public async Task Delete_And_Reset_ClearIt_AdminCallsAreNeverThrottled()
    {
        await Admin(HttpMethod.Post, "/_admin/throttle", new { count = 5 });
        using (var state = await Send(HttpMethod.Get, "/_admin/state"))
        {
            Assert.Equal(HttpStatusCode.OK, state.StatusCode);
        }

        await Admin(HttpMethod.Delete, "/_admin/throttle");
        Assert.Equal(200, (await Users()).Status);

        await Admin(HttpMethod.Post, "/_admin/throttle", new { count = 5, retryAfterSeconds = 1 });
        Assert.Equal(429, (await Users()).Status);
        await Admin(HttpMethod.Post, "/_admin/reset");
        Assert.Equal(200, (await Users()).Status);
    }

    [Fact]
    public async Task BadThrottleRequests_Are400()
    {
        await Admin(HttpMethod.Post, "/_admin/throttle", new { }, HttpStatusCode.BadRequest);
        await Admin(HttpMethod.Post, "/_admin/throttle", new { count = 0 }, HttpStatusCode.BadRequest);
        await Admin(HttpMethod.Post, "/_admin/throttle", new { count = 1, retryAfterSeconds = -1 }, HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Batch_ThrottlesSubRequestsIndividually_AndTheEnvelopeStays200()
    {
        await Admin(HttpMethod.Post, "/_admin/throttle", new { count = 1, retryAfterSeconds = 3 });
        var response = await RawGraph.Post(Http, "app", "/v1.0/$batch", new
        {
            requests = new[] { new { id = "1", method = "GET", url = "/users" }, new { id = "2", method = "GET", url = "/groups" } },
        });
        Assert.Equal(200, response.Status);
        var responses = response.Body!["responses"]!.AsArray();
        Assert.Equal(429, (int)responses[0]!["status"]!);
        Assert.Equal("3", (string?)responses[0]!["headers"]!["Retry-After"]);
        Assert.Equal("TooManyRequests", (string?)responses[0]!["body"]!["error"]!["code"]);
        Assert.Equal(200, (int)responses[1]!["status"]!);
    }

    [Fact]
    public async Task SdkWithoutRetry_SeesA429()
    {
        await Admin(HttpMethod.Post, "/_admin/throttle", new { count = 1, retryAfterSeconds = 1 });
        var ex = await Assert.ThrowsAsync<ODataError>(() => As("app").Users.GetAsync());
        Assert.Equal(429, ex.ResponseStatusCode);
        Assert.Equal("TooManyRequests", ex.Error!.Code);
    }

    [Fact]
    public async Task SdkDefaultRetryHandler_RecoversTransparently()
    {
        await Admin(HttpMethod.Post, "/_admin/throttle", new { count = 1, retryAfterSeconds = 1 });

        // GraphClientFactory wires the SDK's default middleware (redirect, retry on 429/503, ...) in front of the test server.
        var handlers = GraphClientFactory.CreateDefaultHandlers();
        using var http = GraphClientFactory.Create(handlers, finalHandler: Factory.Server.CreateHandler());
        var auth = new BaseBearerTokenAuthenticationProvider(new StaticTokenProvider("app"));
        var graph = new GraphServiceClient(new HttpClientRequestAdapter(auth, httpClient: http) { BaseUrl = TestSupport.BaseUrl });

        var watch = Stopwatch.StartNew();
        var users = await graph.Users.GetAsync();
        watch.Stop();

        Assert.Equal(5, users!.Value!.Count);
        Assert.True(watch.Elapsed >= TimeSpan.FromMilliseconds(800), $"The retry handler should wait for Retry-After (waited {watch.Elapsed}).");
        Assert.Equal(0, Factory.Services.GetRequiredService<ThrottleState>().Remaining);
    }
}