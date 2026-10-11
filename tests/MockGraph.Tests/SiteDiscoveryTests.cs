using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Graph;
using Microsoft.Graph.Models;
using Microsoft.Graph.Models.ODataErrors;
using Microsoft.Graph.Sites.GetAllSites;

using MockGraph.Server.Fixtures;
using MockGraph.Server.State;

namespace MockGraph.Tests;

public sealed class SiteDiscoveryTests(MockGraphFactory factory) : IClassFixture<MockGraphFactory>
{
    private const string Host = "contoso-mock.sharepoint.com";

    private GraphServiceClient As(string token) => factory.Client(token);

    [Fact]
    public async Task SearchStar_ReturnsEverySiteIncludingTheRoot()
    {
        var sites = await As("app").Sites.GetAsync(r => r.QueryParameters.Search = "*");
        Assert.Equal(["Knowledge Base", "contoso-mock"], sites!.Value!.Select(s => s.DisplayName).Order(StringComparer.Ordinal));
    }

    [Theory]
    [InlineData("knowledge", 1)]
    [InlineData("KB", 1)] // matches the site name (its fixture id)
    [InlineData("base", 1)]
    [InlineData("contoso", 1)]
    [InlineData("nothing-like-this", 0)]
    public async Task SearchKeyword_MatchesNameOrDisplayNameCaseInsensitively(string keyword, int expected)
    {
        var sites = await As("user:erin").Sites.GetAsync(r => r.QueryParameters.Search = keyword);
        Assert.Equal(expected, sites!.Value!.Count);
    }

    [Fact]
    public async Task SearchPaging_KeepsTheSearchTerm()
    {
        var client = factory.WithPageSize(1).Client("app");
        var first = await client.Sites.GetAsync(r => r.QueryParameters.Search = "*");
        Assert.Single(first!.Value!);
        Assert.Contains("search=", first.OdataNextLink);

        var ids = new List<string>();
        await PageIterator<Site, SiteCollectionResponse>.CreatePageIterator(client, first, s =>
        {
            ids.Add(s.Id!);
            return true;
        }).IterateAsync();
        Assert.Equal(2, ids.Distinct().Count());
    }

    [Fact]
    public async Task GetAllSites_ListsAllSitesAndPages()
    {
        var all = await As("app").Sites.GetAllSites.GetAsGetAllSitesGetResponseAsync();
        Assert.Equal(2, all!.Value!.Count);

        var client = factory.WithPageSize(1).Client("app");
        var first = await client.Sites.GetAllSites.GetAsGetAllSitesGetResponseAsync();
        Assert.Single(first!.Value!);
        Assert.Contains("/sites/getAllSites", first.OdataNextLink);

        var seen = new List<string>();
        await PageIterator<Site, GetAllSitesGetResponse>.CreatePageIterator(client, first, s =>
        {
            seen.Add(s.Id!);
            return true;
        }).IterateAsync();
        Assert.Equal(2, seen.Distinct().Count());
    }

    [Fact]
    public async Task PlainSitesListing_IsUnchanged()
    {
        var sites = await As("app").Sites.GetAsync();
        Assert.Equal("Knowledge Base", Assert.Single(sites!.Value!).DisplayName);
    }

    [Fact]
    public async Task RootSite_HasSiteCollectionAndRootFacet()
    {
        var root = await As("app").Sites["root"].GetAsync();
        Assert.Equal(Host, root!.SiteCollection!.Hostname);
        Assert.NotNull(root.Root);
        Assert.Equal($"https://{Host}", root.WebUrl);
        Assert.Equal("contoso-mock", root.DisplayName);
        Assert.StartsWith($"{Host},", root.Id);

        // The tenant host on its own addresses the root site too.
        Assert.Equal(root.Id, (await As("app").Sites[Host].GetAsync())!.Id);

        var raw = await RawGraph.Get(factory.CreateClient(), "app", "/v1.0/sites/root");
        Assert.Equal(200, raw.Status);
        Assert.Equal(Host, (string?)raw.Body!["siteCollection"]!["hostname"]);
        Assert.False((bool)raw.Body["isPersonalSite"]!);
        Assert.Empty((await As("app").Sites["root"].Drives.GetAsync())!.Value!);
    }

    [Theory]
    [InlineData("contoso-mock.sharepoint.com:/sites/kb")]
    [InlineData("contoso-mock.sharepoint.com:/sites/kb/")]
    [InlineData("CONTOSO-MOCK.sharepoint.com:/Sites/KB")]
    public async Task PathAddressing_ThroughTheSdk_FindsTheSite(string reference)
    {
        var kb = Ids.Site("contoso-mock.onmicrosoft.com", "kb");
        var site = await As("user:erin").Sites[reference].GetAsync();
        Assert.Equal(kb, site!.Id);
        Assert.Equal($"https://{Host}/sites/kb", site.WebUrl);

        var drive = await As("user:erin").Sites[reference].Drive.GetAsync();
        Assert.Equal(Ids.Drive("kb-docs"), drive!.Id);
        var drives = await As("user:erin").Sites[reference].Drives.GetAsync();
        Assert.Equal(Ids.Drive("kb-docs"), Assert.Single(drives!.Value!).Id);
    }

    [Fact]
    public async Task PathAddressing_OnTheWire_AcceptsTheDocumentedForms()
    {
        var http = factory.CreateClient();
        var site = await RawGraph.Get(http, "app", $"/v1.0/sites/{Host}:/sites/kb");
        Assert.Equal(200, site.Status);
        Assert.Equal("Knowledge Base", (string?)site.Body!["displayName"]);

        var drives = await RawGraph.Get(http, "app", $"/v1.0/sites/{Host}:/sites/kb:/drives");
        Assert.Equal(Ids.Drive("kb-docs"), (string?)drives.Body!["value"]![0]!["id"]);
        Assert.Equal(200, (await RawGraph.Get(http, "app", $"/v1.0/sites/{Host}:/sites/kb:/drive")).Status);

        var root = await RawGraph.Get(http, "app", $"/v1.0/sites/{Host}:/");
        Assert.Equal(200, root.Status);
        Assert.NotNull(root.Body!["siteCollection"]);

        foreach (var url in new[] { $"/v1.0/sites/{Host}:/sites/nope", "/v1.0/sites/other.sharepoint.com:/sites/kb", "/v1.0/sites/a/b/c" })
        {
            var missing = await RawGraph.Get(http, "app", url);
            Assert.Equal(404, missing.Status);
            Assert.NotNull(missing.Body!["error"]!["code"]);
        }
    }

    [Fact]
    public async Task UnknownSite_Is404()
    {
        var ex = await Assert.ThrowsAsync<ODataError>(() => As("app").Sites[$"{Host}:/sites/missing"].GetAsync());
        Assert.Equal("itemNotFound", ex.Error!.Code);
    }

    [Fact]
    public async Task FixtureCanMarkItsOwnRootSiteAndOverrideThePath()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var path = Path.Combine(dir.FullName, "f.yaml");
            await File.WriteAllTextAsync(path, """
                tenant: { domain: acme.onmicrosoft.com }
                sites:
                  - { id: home, displayName: Acme Home, root: true }
                  - { id: team, displayName: Team, path: /teams/team }
                """);

            using var custom = factory.WithWebHostBuilder(b => b.UseSetting("MockGraph:FixturePath", path));
            var client = custom.Client("app");
            var root = await client.Sites["root"].GetAsync();
            Assert.Equal("Acme Home", root!.DisplayName);
            Assert.Equal("https://acme.sharepoint.com", root.WebUrl);
            Assert.Equal("Team", (await client.Sites["acme.sharepoint.com:/teams/team"].GetAsync())!.DisplayName);

            var all = await client.Sites.GetAllSites.GetAsGetAllSitesGetResponseAsync();
            Assert.Equal(2, all!.Value!.Count); // no extra implicit root when the fixture has one
        }
        finally
        {
            dir.Delete(true);
        }
    }

    [Fact]
    public void FixtureLoader_RejectsTwoRootsAndForeignSiteGroups()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            string Write(string yaml)
            {
                var path = Path.Combine(dir.FullName, Guid.NewGuid() + ".yaml");
                File.WriteAllText(path, yaml);
                return path;
            }

            Assert.Throws<InvalidDataException>(() => FixtureLoader.Load(Write("""
                sites:
                  - { id: a, root: true }
                  - { id: b, root: true }
                """)));

            var foreign = Write("""
                users: [{ id: u, displayName: U, upn: u@x.onmicrosoft.com }]
                sites:
                  - id: a
                    siteGroups: [{ id: ga, displayName: GA, members: [user:u] }]
                  - id: b
                    drives:
                      - id: db
                        name: Docs
                        items:
                          - { path: /F, permissions: [{ principal: siteGroup:ga, role: read }] }
                """);
            Assert.Contains("site", Assert.Throws<InvalidDataException>(() => FixtureLoader.Load(foreign)).Message);
        }
        finally
        {
            dir.Delete(true);
        }
    }
}