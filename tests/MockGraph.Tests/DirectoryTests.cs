using System.Net;
using System.Text.Json;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Graph;
using Microsoft.Graph.Models;
using Microsoft.Graph.Models.ODataErrors;

using MockGraph.Server.State;

namespace MockGraph.Tests;

public sealed class DirectoryTests(MockGraphFactory factory) : IClassFixture<MockGraphFactory>
{
    private static readonly string[] AllUsers = ["alice", "bob", "carol", "dave", "erin"];

    private GraphServiceClient As(string token) => factory.Client(token);

    private static List<string> Names(IEnumerable<DirectoryObject>? objects) =>
        [.. objects!.Select(o => ((Group)o).DisplayName!)];

    [Fact]
    public async Task ListUsers_PageIterator_WalksAllPages()
    {
        var client = factory.WithPageSize(2).Client("app");
        var first = await client.Users.GetAsync();
        Assert.Equal(2, first!.Value!.Count);
        Assert.NotNull(first.OdataNextLink);

        var seen = new List<string>();
        var pages = 0;
        var iterator = PageIterator<User, UserCollectionResponse>.CreatePageIterator(client, first, u =>
        {
            seen.Add(u.UserPrincipalName!);
            return true;
        }, req =>
        {
            pages++;
            return req;
        });
        await iterator.IterateAsync();

        Assert.Equal(AllUsers.Select(u => $"{u}@contoso-mock.onmicrosoft.com"), seen);
        Assert.Equal(2, pages);
    }

    [Fact]
    public async Task ListUsers_TopParameter_Pages()
    {
        var client = As("app");
        var page = await client.Users.GetAsync(rc => rc.QueryParameters.Top = 3);
        Assert.Equal(3, page!.Value!.Count);
        Assert.Contains("$top=3", page.OdataNextLink);
    }

    [Fact]
    public async Task ListGroups_DefaultPageSizeIsFive()
    {
        var page = await As("app").Groups.GetAsync();
        Assert.Equal(5, page!.Value!.Count);
        Assert.NotNull(page.OdataNextLink);
    }

    [Theory]
    [InlineData("guid")]
    [InlineData("upn")]
    [InlineData("alice")]
    public async Task GetUser_ByGuidUpnOrFixtureId(string form)
    {
        var id = form switch
        {
            "guid" => Ids.User("alice"),
            "upn" => "alice@contoso-mock.onmicrosoft.com",
            _ => "alice",
        };

        var user = await As("app").Users[id].GetAsync();

        Assert.Equal(Ids.User("alice"), user!.Id);
        Assert.Equal("Alice Backend", user.DisplayName);
        Assert.Equal("alice@contoso-mock.onmicrosoft.com", user.UserPrincipalName);
    }

    [Fact]
    public async Task Me_AsUser_ReturnsCaller()
    {
        var me = await As("user:bob").Me.GetAsync();
        Assert.Equal("Bob Frontend", me!.DisplayName);
    }

    [Fact]
    public async Task Me_AsApp_Is400()
    {
        var ex = await Assert.ThrowsAsync<ODataError>(() => As("app").Me.GetAsync());
        Assert.Equal(400, ex.ResponseStatusCode);
        Assert.Equal("BadRequest", ex.Error!.Code);
    }

    [Fact]
    public async Task MemberOf_IsDirectOnly_TransitiveIncludesAncestors()
    {
        var client = As("app");

        var direct = await client.Users["alice"].MemberOf.GetAsync();
        var transitive = await client.Users["alice"].TransitiveMemberOf.GetAsync();

        Assert.All(direct!.Value!, o => Assert.IsType<Group>(o));
        Assert.Equal(["Engineering - Backend"], Names(direct.Value));
        Assert.Equal(["Engineering - Backend", "Engineering", "All Staff"], Names(transitive!.Value));
    }

    [Fact]
    public async Task Members_AreDirectOnly_TransitiveMembersFlattenNestedGroups()
    {
        var client = As("app");

        var direct = await client.Groups["all-staff"].Members.GetAsync();
        var transitive = await client.Groups["all-staff"].TransitiveMembers.GetAsync(rc => rc.QueryParameters.Top = 50);

        Assert.Equal(3, direct!.Value!.Count);
        Assert.IsType<Group>(direct.Value[0]);
        Assert.Equal(["carol", "dave"], direct.Value.OfType<User>().Select(u => u.Id).Select(id => id == Ids.User("carol") ? "carol" : "dave"));

        Assert.Equal(7, transitive!.Value!.Count);
        Assert.Equal(4, transitive.Value.OfType<User>().Count());
        Assert.Equal(3, transitive.Value.OfType<Group>().Count());
        Assert.Contains(transitive.Value.OfType<User>(), u => u.Id == Ids.User("alice"));
    }

    [Fact]
    public async Task Select_OnUsers_ReturnsOnlySelectedProperties()
    {
        var page = await As("app").Users.GetAsync(rc => rc.QueryParameters.Select = ["displayName"]);

        Assert.All(page!.Value!, u =>
        {
            Assert.NotNull(u.DisplayName);
            Assert.Null(u.UserPrincipalName);
            Assert.Null(u.Mail);
        });
    }

    [Theory]
    [InlineData("")]
    [InlineData("garbage")]
    public async Task BadToken_Is401WithGraphError(string token)
    {
        var ex = await Assert.ThrowsAsync<ODataError>(() => As(token).Users.GetAsync());
        Assert.Equal(401, ex.ResponseStatusCode);
        Assert.Equal("InvalidAuthenticationToken", ex.Error!.Code);
        Assert.NotNull(ex.Error.InnerError!.RequestId);
    }

    [Fact]
    public async Task UnknownUserInToken_Is401()
    {
        var ex = await Assert.ThrowsAsync<ODataError>(() => As("user:nobody").Users.GetAsync());
        Assert.Equal(401, ex.ResponseStatusCode);
    }

    [Fact]
    public async Task MissingHeader_Is401_OnTheWire()
    {
        var response = await factory.CreateClient().GetAsync("/v1.0/users");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("InvalidAuthenticationToken", doc.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task UnknownUser_Is404ResourceNotFound()
    {
        var ex = await Assert.ThrowsAsync<ODataError>(() => As("app").Users["nobody"].GetAsync());
        Assert.Equal(404, ex.ResponseStatusCode);
        Assert.Equal("Request_ResourceNotFound", ex.Error!.Code);
    }

    [Fact]
    public async Task UnknownGroup_Is404ResourceNotFound()
    {
        var ex = await Assert.ThrowsAsync<ODataError>(() => As("app").Groups["nope"].Members.GetAsync());
        Assert.Equal("Request_ResourceNotFound", ex.Error!.Code);
    }

    [Fact]
    public async Task UnsupportedFilter_Is400()
    {
        var ex = await Assert.ThrowsAsync<ODataError>(() =>
            As("app").Users.GetAsync(rc => rc.QueryParameters.Filter = "endsWith(displayName,'A')"));
        Assert.Equal(400, ex.ResponseStatusCode);
        Assert.Equal("BadRequest", ex.Error!.Code);
        Assert.Contains("$filter", ex.Error.Message);
    }

    [Fact]
    public async Task Collection_WireFormat()
    {
        var http = factory.CreateClient();
        http.DefaultRequestHeaders.Authorization = new("Bearer", "user:alice");

        var response = await http.GetAsync("/v1.0/users?$top=2");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(
            "application/json;odata.metadata=minimal;odata.streaming=true;IEEE754Compatible=false;charset=utf-8",
            response.Content.Headers.GetValues("Content-Type").Single().Replace(" ", ""));
        Assert.True(response.Headers.Contains("request-id"));

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = doc.RootElement;
        Assert.Equal("http://localhost/v1.0/$metadata#users", root.GetProperty("@odata.context").GetString());
        var next = root.GetProperty("@odata.nextLink").GetString()!;
        Assert.StartsWith("http://localhost/v1.0/users?", next);
        Assert.Contains("$skiptoken=", next);
        Assert.Equal(2, root.GetProperty("value").GetArrayLength());
    }

    [Fact]
    public async Task MemberOf_WireFormat_CarriesODataType()
    {
        var http = factory.CreateClient();
        http.DefaultRequestHeaders.Authorization = new("Bearer", "app");

        using var doc = JsonDocument.Parse(await http.GetStringAsync("/v1.0/users/alice/memberOf"));

        Assert.Equal("#microsoft.graph.group", doc.RootElement.GetProperty("value")[0].GetProperty("@odata.type").GetString());
    }

    [Fact]
    public async Task UnknownRoute_And_WriteMethod_AreGraphShaped()
    {
        var http = factory.CreateClient();
        http.DefaultRequestHeaders.Authorization = new("Bearer", "app");

        var missing = await http.GetAsync("/v1.0/nothing");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Contains("\"error\"", await missing.Content.ReadAsStringAsync());

        var post = await http.PostAsync("/v1.0/users", new StringContent("{}"));
        Assert.Equal(HttpStatusCode.MethodNotAllowed, post.StatusCode);
        Assert.Contains("\"error\"", await post.Content.ReadAsStringAsync());
    }
}