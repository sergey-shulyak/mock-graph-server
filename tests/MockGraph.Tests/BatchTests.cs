using System.Net.Http.Json;
using System.Text.Json.Nodes;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Graph;
using Microsoft.Graph.Models;
using Microsoft.Kiota.Abstractions;

using MockGraph.Server.State;

namespace MockGraph.Tests;

public sealed class BatchTests(MockGraphFactory factory) : IClassFixture<MockGraphFactory>
{
    private static readonly string DriveId = Ids.Drive("kb-docs");

    private HttpClient Http => factory.CreateClient();

    private static object Get(string id, string url, object? headers = null, object? dependsOn = null) =>
        new { id, method = "GET", url, headers, dependsOn };

    private Task<RawResponse> Batch(string token, params object[] requests) =>
        RawGraph.Post(Http, token, "/v1.0/$batch", new { requests });

    [Fact]
    public async Task Sdk_BatchRequestContentCollection_RoundTrips()
    {
        var graph = factory.Client("user:erin");
        var batch = new BatchRequestContentCollection(graph);
        var users = await batch.AddBatchRequestStepAsync(graph.Users.ToGetRequestInformation(r => r.QueryParameters.Top = 2));
        var group = await batch.AddBatchRequestStepAsync(graph.Groups["hr"].ToGetRequestInformation());
        var visible = await batch.AddBatchRequestStepAsync(
            graph.Drives[DriveId].Items[Ids.Item(DriveId, "/Shared/contractor-onboarding.md")].ToGetRequestInformation());
        var hidden = await batch.AddBatchRequestStepAsync(graph.Drives[DriveId].Items[Ids.Item(DriveId, "/HR")].ToGetRequestInformation());

        var responses = await graph.Batch.PostAsync(batch);
        var statuses = await responses.GetResponsesStatusCodesAsync();
        Assert.Equal(200, (int)statuses[users]);
        Assert.Equal(200, (int)statuses[group]);
        Assert.Equal(200, (int)statuses[visible]);
        Assert.Equal(404, (int)statuses[hidden]); // user context applies per sub-request

        var usersPage = await responses.GetResponseByIdAsync<UserCollectionResponse>(users);
        Assert.Equal(2, usersPage.Value!.Count);
        Assert.NotNull(usersPage.OdataNextLink);
        Assert.Equal("HR", (await responses.GetResponseByIdAsync<Group>(group)).DisplayName);
        Assert.Equal("contractor-onboarding.md", (await responses.GetResponseByIdAsync<DriveItem>(visible)).Name);
        await Assert.ThrowsAnyAsync<Exception>(() => responses.GetResponseByIdAsync<DriveItem>(hidden)); // the SDK surfaces the 404 as a ServiceException
    }

    [Fact]
    public async Task Sdk_Batch_MoreThanTwentyStepsIsSplitIntoSeveralBatches()
    {
        var graph = factory.Client("app");
        var batch = new BatchRequestContentCollection(graph);
        var ids = new List<string>();
        for (var i = 0; i < 25; i++)
        {
            ids.Add(await batch.AddBatchRequestStepAsync(graph.Users["alice"].ToGetRequestInformation()));
        }

        var responses = await graph.Batch.PostAsync(batch);
        foreach (var id in ids)
        {
            Assert.Equal("Alice Backend", (await responses.GetResponseByIdAsync<User>(id)).DisplayName);
        }
    }

    [Fact]
    public async Task WireShape_MatchesGraphsResponsesEnvelope()
    {
        var response = await Batch("app", Get("1", "/users?$top=1"), Get("two", "groups/hr"));
        Assert.Equal(200, response.Status);

        var responses = response.Body!["responses"]!.AsArray();
        Assert.Equal(["1", "two"], responses.Select(r => (string?)r!["id"]));
        var first = responses[0]!.AsObject();
        Assert.Equal(200, (int)first["status"]!);
        Assert.StartsWith("application/json", (string?)first["headers"]!["Content-Type"]);
        Assert.Single(first["body"]!["value"]!.AsArray());
        Assert.StartsWith("http", (string?)first["body"]!["@odata.nextLink"]);
        Assert.Equal("HR", (string?)responses[1]!["body"]!["displayName"]);
    }

    [Fact]
    public async Task SubRequestHeadersAndQueryAreHonoured()
    {
        var response = await Batch(
            "app", Get("c", "/users?$count=true&$filter=startsWith(displayName,'A')", new { ConsistencyLevel = "eventual" }));
        var body = response.Body!["responses"]![0]!["body"]!;
        Assert.Equal(1, (int)body["@odata.count"]!);
        Assert.Equal("Alice Backend", (string?)body["value"]![0]!["displayName"]);
    }

    [Fact]
    public async Task FailuresArePerResponse_AndTheBatchStaysA200()
    {
        var response = await Batch("app", Get("ok", "/users/alice"), Get("missing", "/users/nobody"), Get("nowhere", "/nothing/here"));
        Assert.Equal(200, response.Status);
        var byId = response.Body!["responses"]!.AsArray().ToDictionary(r => (string)r!["id"]!, r => r!);
        Assert.Equal(200, (int)byId["ok"]["status"]!);
        Assert.Equal(404, (int)byId["missing"]["status"]!);
        Assert.Equal("Request_ResourceNotFound", (string?)byId["missing"]["body"]!["error"]!["code"]);
        Assert.NotNull(byId["missing"]["body"]!["error"]!["innerError"]!["request-id"]);
        Assert.Equal(404, (int)byId["nowhere"]["status"]!);
    }

    [Fact]
    public async Task NonGetSubRequests_AreRejectedPerResponse_WithoutExecuting()
    {
        var response = await Batch(
            "app",
            new { id = "p", method = "POST", url = "/_admin/reset", body = new { }, headers = new { ContentType = "application/json" } },
            new { id = "d", method = "DELETE", url = "/groups/hr" },
            new { id = "f", method = "FROBNICATE", url = "/users" },
            Get("g", "/users/alice"));
        var byId = response.Body!["responses"]!.AsArray().ToDictionary(r => (string)r!["id"]!, r => r!);
        Assert.Equal(405, (int)byId["p"]["status"]!);
        Assert.Equal(405, (int)byId["d"]["status"]!);
        Assert.Equal(400, (int)byId["f"]["status"]!);
        Assert.Equal(200, (int)byId["g"]["status"]!);
        Assert.Contains("GET", (string?)byId["d"]["body"]!["error"]!["message"]);
    }

    [Fact]
    public async Task DependsOn_RunsInOrderAndFailsDependentsWith424()
    {
        var ok = await Batch("app", Get("2", "/users/bob", dependsOn: new[] { "1" }), Get("1", "/users/alice"));
        Assert.Equal([200, 200], ok.Body!["responses"]!.AsArray().Select(r => (int)r!["status"]!));

        var failed = await Batch("app", Get("a", "/users/nobody"), Get("b", "/users/alice", dependsOn: new[] { "a" }));
        var responses = failed.Body!["responses"]!.AsArray();
        Assert.Equal(404, (int)responses[0]!["status"]!);
        Assert.Equal(424, (int)responses[1]!["status"]!);

        var cycle = await Batch("app", Get("x", "/users", dependsOn: new[] { "y" }), Get("y", "/users", dependsOn: new[] { "x" }));
        Assert.Equal(400, cycle.Status);
    }

    [Fact]
    public async Task MalformedBatches_Are400()
    {
        var http = Http;
        Assert.Equal(400, (await RawGraph.Post(http, "app", "/v1.0/$batch", new { })).Status);
        Assert.Equal(400, (await RawGraph.Post(http, "app", "/v1.0/$batch", new { requests = new[] { new { id = "1", method = "GET" } } })).Status);
        Assert.Equal(400, (await Batch("app", Get("1", "/users"), Get("1", "/groups"))).Status); // duplicate ids

        var tooMany = await Batch("app", [.. Enumerable.Range(1, 21).Select(i => Get(i.ToString(), "/users"))]);
        Assert.Equal(400, tooMany.Status);
        Assert.Equal("BadRequest", (string?)tooMany.Body!["error"]!["code"]);
        Assert.Equal(200, (await Batch("app", [.. Enumerable.Range(1, 20).Select(i => Get(i.ToString(), "/users"))])).Status);

        using var garbage = await http.SendAsync(new HttpRequestMessage(HttpMethod.Post, "/v1.0/$batch")
        {
            Headers = { { "Authorization", "Bearer app" } },
            Content = new StringContent("{nope", System.Text.Encoding.UTF8, "application/json"),
        });
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, garbage.StatusCode);
    }

    [Fact]
    public async Task BatchNeedsAuthentication()
    {
        using var response = await Http.PostAsJsonAsync("/v1.0/$batch", new { requests = new[] { Get("1", "/users") } });
        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task FileContentRedirectIsReportedAsA302()
    {
        var item = Ids.Item(DriveId, "/General/office-wifi.md");
        var response = await Batch("app", Get("c", $"/drives/{DriveId}/items/{item}/content"));
        var sub = response.Body!["responses"]![0]!;
        Assert.Equal(302, (int)sub["status"]!);
        Assert.Contains("/_download/", (string?)sub["headers"]!["Location"]);
    }
}