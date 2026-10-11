using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Graph;
using Microsoft.Graph.Models;
using Microsoft.Graph.Models.ODataErrors;

using MockGraph.Server.State;

namespace MockGraph.Tests;

public sealed class DirectoryFilterTests(MockGraphFactory factory) : IClassFixture<MockGraphFactory>
{
    private const string Eventual = "ConsistencyLevel";

    private GraphServiceClient As(string token) => factory.Client(token);

    private async Task<string[]> UserKeys(string filter)
    {
        var page = await As("app").Users.GetAsync(r => r.QueryParameters.Filter = filter);
        return [.. page!.Value!.Select(u => u.UserPrincipalName![..u.UserPrincipalName!.IndexOf('@')])];
    }

    private async Task<string[]> GroupNames(string filter)
    {
        var page = await As("app").Groups.GetAsync(r => r.QueryParameters.Filter = filter);
        return [.. page!.Value!.Select(g => g.DisplayName!)];
    }

    [Theory]
    [InlineData("mail eq 'alice@contoso-mock.onmicrosoft.com'", "alice")]
    [InlineData("mail eq 'ALICE@CONTOSO-MOCK.onmicrosoft.com'", "alice")]
    [InlineData("userPrincipalName eq 'bob@contoso-mock.onmicrosoft.com'", "bob")]
    [InlineData("displayName eq 'carol hr'", "carol")]
    [InlineData("startsWith(displayName,'D')", "dave")]
    [InlineData("startswith(userPrincipalName, 'er')", "erin")]
    [InlineData("displayName eq 'Alice Backend' or displayName eq 'Bob Frontend'", "alice,bob")]
    [InlineData("startsWith(displayName,'A') and mail eq 'alice@contoso-mock.onmicrosoft.com'", "alice")]
    [InlineData("startsWith(displayName,'A') and mail eq 'bob@contoso-mock.onmicrosoft.com'", "")]
    [InlineData("displayName eq 'X' or (startsWith(displayName,'C') and startsWith(mail,'c'))", "carol")]
    [InlineData("displayName eq 'it''s'", "")]
    public async Task UserFilter_EqAndStartsWith(string filter, string expected) =>
        Assert.Equal(expected.Length == 0 ? [] : expected.Split(','), await UserKeys(filter));

    [Fact]
    public async Task UserFilter_ById()
    {
        Assert.Equal(["bob"], await UserKeys($"id eq '{Ids.User("bob")}'"));
        Assert.Empty(await UserKeys("id eq 'bob'")); // the wire id only, as on Graph
    }

    [Fact]
    public async Task GroupFilter()
    {
        Assert.Equal(["HR"], await GroupNames("displayName eq 'hr'"));
        Assert.Equal(["Engineering", "Engineering - Backend", "Engineering - Frontend"], await GroupNames("startsWith(displayName,'engineering')"));
        Assert.Equal(["Support"], await GroupNames($"id eq '{Ids.Group("support")}'"));
        Assert.Empty(await GroupNames("mail eq 'hr@contoso-mock.onmicrosoft.com'")); // security groups carry no mail
    }

    [Theory]
    [InlineData("displayName ne 'x'", "ne")]
    [InlineData("endsWith(displayName,'x')", "endsWith")]
    [InlineData("not startsWith(displayName,'x')", "not")]
    [InlineData("jobTitle eq 'x'", "jobTitle")]
    [InlineData("displayName eq", "Incomplete")]
    [InlineData("displayName eq 5", "quoted string")]
    [InlineData("displayName eq 'x", "Unterminated")]
    [InlineData("displayName gt 'x'", "gt")]
    public async Task UnsupportedFilter_Is400BadRequestNamingTheProblem(string filter, string mention)
    {
        var ex = await Assert.ThrowsAsync<ODataError>(() => As("app").Users.GetAsync(r => r.QueryParameters.Filter = filter));
        Assert.Equal(400, ex.ResponseStatusCode);
        Assert.Equal("BadRequest", ex.Error!.Code);
        Assert.Contains(mention, ex.Error.Message);
    }

    [Fact]
    public async Task Filter_IsStillRejectedOnOtherDirectoryCollections()
    {
        var ex = await Assert.ThrowsAsync<ODataError>(() =>
            As("app").Groups["hr"].Members.GetAsync(r => r.QueryParameters.Filter = "displayName eq 'x'"));
        Assert.Equal(400, ex.ResponseStatusCode);
    }

    [Fact]
    public async Task Filter_SurvivesPaging()
    {
        var client = factory.WithPageSize(2).Client("app");
        var first = await client.Groups.GetAsync(r => r.QueryParameters.Filter = "startsWith(displayName,'Engineering')");
        Assert.Equal(2, first!.Value!.Count);
        Assert.Contains("filter", first.OdataNextLink);

        var names = new List<string>();
        await PageIterator<Group, GroupCollectionResponse>.CreatePageIterator(client, first, g =>
        {
            names.Add(g.DisplayName!);
            return true;
        }).IterateAsync();
        Assert.Equal(3, names.Count);
    }

    [Fact]
    public async Task Count_WithConsistencyLevel_EmitsTheTotalNotThePageSize()
    {
        var client = factory.WithPageSize(2).Client("app");
        var users = await client.Users.GetAsync(r =>
        {
            r.QueryParameters.Count = true;
            r.Headers.Add(Eventual, "eventual");
        });
        Assert.Equal(5, users!.OdataCount);
        Assert.Equal(2, users.Value!.Count);

        var filtered = await client.Groups.GetAsync(r =>
        {
            r.QueryParameters.Count = true;
            r.QueryParameters.Filter = "startsWith(displayName,'Engineering')";
            r.Headers.Add(Eventual, "eventual");
        });
        Assert.Equal(3, filtered!.OdataCount);
    }

    [Fact]
    public async Task Count_OnTheWire_NeedsTheHeader_AndIsIgnoredWithoutIt()
    {
        var http = factory.CreateClient();
        var with = await RawGraph.Get(http, "app", "/v1.0/users?$count=true", (Eventual, "eventual"));
        Assert.Equal(5, (int)with.Body!["@odata.count"]!);

        // Graph silently ignores $count=true when ConsistencyLevel is missing.
        var without = await RawGraph.Get(http, "app", "/v1.0/users?$count=true");
        Assert.Equal(200, without.Status);
        Assert.Null(without.Body!["@odata.count"]);

        var bad = await RawGraph.Get(http, "app", "/v1.0/users?$count=maybe", (Eventual, "eventual"));
        Assert.Equal(400, bad.Status);
    }

    [Fact]
    public async Task OrderBy_DisplayName_AscendingAndDescending()
    {
        var asc = await As("app").Users.GetAsync(r => r.QueryParameters.Orderby = ["displayName"]);
        Assert.Equal(["Alice Backend", "Bob Frontend", "Carol HR", "Dave Support", "Erin Contractor"], asc!.Value!.Select(u => u.DisplayName));

        var desc = await As("app").Groups.GetAsync(r => r.QueryParameters.Orderby = ["displayName desc"]);
        Assert.Equal("Support", desc!.Value![0].DisplayName);

        var bad = await Assert.ThrowsAsync<ODataError>(() => As("app").Users.GetAsync(r => r.QueryParameters.Orderby = ["jobTitle"]));
        Assert.Equal(400, bad.ResponseStatusCode);
    }

    [Fact]
    public async Task FilterWithOrderBy_RequiresAdvancedQuery()
    {
        var ex = await Assert.ThrowsAsync<ODataError>(() => As("app").Users.GetAsync(r =>
        {
            r.QueryParameters.Filter = "startsWith(displayName,'A')";
            r.QueryParameters.Orderby = ["displayName"];
        }));
        Assert.Equal(400, ex.ResponseStatusCode);
        Assert.Equal("Request_UnsupportedQuery", ex.Error!.Code);

        var ok = await As("app").Users.GetAsync(r =>
        {
            r.QueryParameters.Filter = "startsWith(displayName,'A') or startsWith(displayName,'B')";
            r.QueryParameters.Orderby = ["displayName desc"];
            r.QueryParameters.Count = true;
            r.Headers.Add(Eventual, "eventual");
        });
        Assert.Equal(["Bob Frontend", "Alice Backend"], ok!.Value!.Select(u => u.DisplayName));
        Assert.Equal(2, ok.OdataCount);
    }
}