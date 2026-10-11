using System.Net;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Graph;
using Microsoft.Graph.Models;
using Microsoft.Graph.Models.ODataErrors;

using MockGraph.Server.State;

namespace MockGraph.Tests;

public sealed class FilesTests(MockGraphFactory factory) : IClassFixture<MockGraphFactory>
{
    private static readonly string DriveId = Ids.Drive("kb-docs");

    private static readonly string[] General =
        ["/General", "/General/vacation-policy.md", "/General/office-wifi.md", "/General/floor-plan.png"];

    private static readonly string[] BackendDocs =
        ["/Engineering/Backend", "/Engineering/Backend/db-migrations.md", "/Engineering/Backend/prod-access.md"];

    private const string Onboarding = "/Shared/contractor-onboarding.md";

    // The oracle: the expected-visibility table at the bottom of fixtures/sample.yaml (folders included).
    public static TheoryData<string, string[]> Visibility => new()
    {
        { "alice", [.. General, "/Engineering", "/Engineering/local-dev-setup.md", .. BackendDocs, Onboarding] },
        { "bob", [.. General, "/Engineering", "/Engineering/local-dev-setup.md", Onboarding] },
        { "carol", [.. General, "/HR", "/HR/salary-bands.md"] },
        { "dave", [.. General, "/Support", "/Support/escalation-runbook.md"] },
        { "erin", [Onboarding] },
    };

    private static string IdOf(string path) => Ids.Item(DriveId, path);

    private GraphServiceClient As(string token) => factory.Client(token);

    private static string[] AllPaths(Dictionary<string, string> tree) => [.. tree.Values.Order()];

    private static async Task<Dictionary<string, string>> Walk(GraphServiceClient client, string folderId, string folderPath)
    {
        var found = new Dictionary<string, string>();
        var page = await client.Drives[DriveId].Items[folderId].Children.GetAsync();
        var children = new List<DriveItem>();
        var iterator = PageIterator<DriveItem, DriveItemCollectionResponse>.CreatePageIterator(client, page!, c =>
        {
            children.Add(c);
            return true;
        });
        await iterator.IterateAsync();

        foreach (var child in children)
        {
            var path = (folderPath == "/" ? "" : folderPath) + "/" + child.Name;
            found[child.Id!] = path;
            if (child.Folder is not null)
            {
                foreach (var (id, p) in await Walk(client, child.Id!, path))
                {
                    found[id] = p;
                }
            }
        }

        return found;
    }

    [Theory]
    [MemberData(nameof(Visibility))]
    public async Task UserCanOpenExactlyTheVisibleItemsById(string user, string[] expected)
    {
        var client = As($"user:{user}");
        var all = AllPaths(await Walk(As("app"), "root", "/"));
        var opened = new List<string>();
        foreach (var path in all)
        {
            try
            {
                await client.Drives[DriveId].Items[IdOf(path)].GetAsync();
                opened.Add(path);
            }
            catch (ODataError e)
            {
                Assert.Equal(404, e.ResponseStatusCode);
                Assert.Equal("itemNotFound", e.Error!.Code);
            }
        }

        Assert.Equal(expected.Order(), opened.Order());
    }

    [Theory]
    [MemberData(nameof(Visibility))]
    public async Task WalkingTheTreeShowsOnlyReachableVisibleItems(string user, string[] expected)
    {
        var walked = AllPaths(await Walk(As($"user:{user}"), "root", "/"));

        // Intermediate folders are not exposed by a readable descendant, so /Shared/... is never reachable by walking.
        var reachable = expected.Where(p => !p.StartsWith("/Shared", StringComparison.Ordinal)).Order();
        Assert.Equal(reachable, walked);
    }

    [Fact]
    public async Task App_SeesEverything()
    {
        var walked = AllPaths(await Walk(As("app"), "root", "/"));
        Assert.Contains("/Shared", walked);
        Assert.Contains(Onboarding, walked);
        Assert.Contains("/HR/salary-bands.md", walked);
        Assert.Equal(15, walked.Length);
    }

    [Fact]
    public async Task Erin_RootIsEmptyButOnboardingIsOpenableById()
    {
        var client = As("user:erin");
        var root = await client.Drives[DriveId].Root.GetAsync();
        Assert.NotNull(root!.Root);
        Assert.Equal(0, root.Folder!.ChildCount);

        var children = await client.Drives[DriveId].Items["root"].Children.GetAsync();
        Assert.Empty(children!.Value!);

        var item = await client.Drives[DriveId].Items[IdOf(Onboarding)].GetAsync();
        Assert.Equal("contractor-onboarding.md", item!.Name);
    }

    [Fact]
    public async Task GetById_Unreadable_Returns404ItemNotFound()
    {
        var ex = await Assert.ThrowsAsync<ODataError>(() =>
            As("user:bob").Drives[DriveId].Items[IdOf("/HR/salary-bands.md")].GetAsync());
        Assert.Equal(404, ex.ResponseStatusCode);
        Assert.Equal("itemNotFound", ex.Error!.Code);

        await Assert.ThrowsAsync<ODataError>(() => As("user:bob").Drives[DriveId].Items[IdOf("/HR")].Children.GetAsync());
        await Assert.ThrowsAsync<ODataError>(() => As("user:bob").Drives[DriveId].Items[IdOf("/HR/salary-bands.md")].Permissions.GetAsync());
        await Assert.ThrowsAsync<ODataError>(() => As("user:bob").Drives[DriveId].Items[IdOf("/HR/salary-bands.md")].Content.GetAsync());
    }

    [Fact]
    public async Task ChildCount_CountsOnlyVisibleChildren()
    {
        var bob = await As("user:bob").Drives[DriveId].Items[IdOf("/Engineering")].GetAsync();
        var alice = await As("user:alice").Drives[DriveId].Items[IdOf("/Engineering")].GetAsync();
        var app = await As("app").Drives[DriveId].Items[IdOf("/Engineering")].GetAsync();
        Assert.Equal(1, bob!.Folder!.ChildCount);
        Assert.Equal(2, alice!.Folder!.ChildCount);
        Assert.Equal(2, app!.Folder!.ChildCount);
    }

    [Fact]
    public async Task DriveItem_FileFacetsAndReferences()
    {
        var item = await As("user:alice").Drives[DriveId].Items[IdOf("/General/vacation-policy.md")].GetAsync();
        Assert.Equal("vacation-policy.md", item!.Name);
        Assert.Equal("text/markdown", item.File!.MimeType);
        Assert.NotEmpty(item.File.Hashes!.Sha1Hash!);
        Assert.True(item.Size > 0);
        Assert.NotEmpty(item.ETag!);
        Assert.NotEmpty(item.CTag!);
        Assert.NotNull(item.CreatedDateTime);
        Assert.NotNull(item.LastModifiedDateTime);
        Assert.Null(item.Folder);
        Assert.Equal(DriveId, item.ParentReference!.DriveId);
        Assert.Equal(IdOf("/General"), item.ParentReference.Id);
        Assert.Equal($"/drives/{DriveId}/root:/General", item.ParentReference.Path);
        Assert.StartsWith("https://", item.WebUrl);
        Assert.Contains("@microsoft.graph.downloadUrl", item.AdditionalData.Keys);
    }

    [Fact]
    public async Task Select_OnDriveItems_KeepsOnlySelectedProperties()
    {
        var page = await As("app").Drives[DriveId].Items["root"].Children.GetAsync(rc => rc.QueryParameters.Select = ["name"]);
        Assert.All(page!.Value!, i =>
        {
            Assert.NotNull(i.Id);
            Assert.NotNull(i.Name);
            Assert.Null(i.Size);
            Assert.Null(i.WebUrl);
            Assert.Null(i.Folder);
        });
    }

    [Fact]
    public async Task Children_PageWithNextLink()
    {
        var page = await factory.WithPageSize(2).Client("app").Drives[DriveId].Items["root"].Children.GetAsync();
        Assert.Equal(2, page!.Value!.Count);
        Assert.NotNull(page.OdataNextLink);
    }

    [Fact]
    public async Task Content_ThroughSdk_ReturnsFixtureText()
    {
        await using var stream = (await As("user:alice").Drives[DriveId].Items[IdOf("/General/vacation-policy.md")].Content.GetAsync())!;
        var text = await new StreamReader(stream).ReadToEndAsync();
        Assert.Contains("24 working days of paid vacation", text);
    }

    [Fact]
    public async Task Content_BinarySourceFile_RoundTripsBytes()
    {
        await using var stream = (await As("user:dave").Drives[DriveId].Items[IdOf("/General/floor-plan.png")].Content.GetAsync())!;
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer);
        Assert.Equal([0x89, (byte)'P', (byte)'N', (byte)'G'], buffer.ToArray()[..4]);

        var item = await As("app").Drives[DriveId].Items[IdOf("/General/floor-plan.png")].GetAsync();
        Assert.Equal("image/png", item!.File!.MimeType);
        Assert.Equal(buffer.Length, item.Size);
    }

    [Fact]
    public async Task Content_Raw_Answers302ToUnauthenticatedDownload()
    {
        using var http = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{TestSupport.BaseUrl}/drives/{DriveId}/items/{IdOf("/General/office-wifi.md")}/content");
        request.Headers.Authorization = new("Bearer", "user:carol");
        using var response = await http.SendAsync(request);

        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        var location = response.Headers.Location!;
        Assert.Contains("/_download/", location.ToString());

        var download = await http.GetAsync(location);
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        Assert.Equal("text/markdown", download.Content.Headers.ContentType!.MediaType);
        Assert.Contains("ContosoStaff", await download.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Download_UnknownToken_Is404()
    {
        var response = await factory.CreateClient().GetAsync("/_download/garbage");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task PathAddressing_ItemWithPath_Works()
    {
        var client = As("user:alice");
        var item = await client.Drives[DriveId].Root.ItemWithPath("Engineering/Backend/db-migrations.md").GetAsync();
        Assert.Equal(IdOf("/Engineering/Backend/db-migrations.md"), item!.Id);

        await using var stream = (await client.Drives[DriveId].Root.ItemWithPath("Engineering/Backend/db-migrations.md").Content.GetAsync())!;
        Assert.Contains("Flyway", await new StreamReader(stream).ReadToEndAsync());

        var children = await client.Drives[DriveId].Root.ItemWithPath("Engineering/Backend").Children.GetAsync();
        Assert.Equal(2, children!.Value!.Count);

        await Assert.ThrowsAsync<ODataError>(() => As("user:bob").Drives[DriveId].Root.ItemWithPath("HR/salary-bands.md").GetAsync());
    }

    [Fact]
    public async Task Permissions_Inherited_HasInheritedFromAndGroupPrincipal()
    {
        var page = await As("app").Drives[DriveId].Items[IdOf("/Engineering/local-dev-setup.md")].Permissions.GetAsync();
        var permission = Assert.Single(page!.Value!);
        Assert.Equal(["read"], permission.Roles);
        var group = permission.GrantedToV2!.Group!;
        Assert.Equal(Ids.Group("engineering"), group.Id);
        Assert.Equal("Engineering", group.DisplayName);
        Assert.Equal("engineering@contoso-mock.onmicrosoft.com", group.AdditionalData["email"]);
        Assert.Null(permission.GrantedToV2.User);
        Assert.Equal(IdOf("/Engineering"), permission.InheritedFrom!.Id);
        Assert.Equal(DriveId, permission.InheritedFrom.DriveId);
        Assert.Equal($"/drives/{DriveId}/root:/Engineering", permission.InheritedFrom.Path);
    }

    [Fact]
    public async Task Permissions_BreakInheritance_OnlyExplicitGrants()
    {
        var page = await As("app").Drives[DriveId].Items[IdOf("/Engineering/Backend/db-migrations.md")].Permissions.GetAsync();
        var permission = Assert.Single(page!.Value!);
        Assert.Equal(Ids.Group("eng-backend"), permission.GrantedToV2!.Group!.Id);
        Assert.Equal(["write"], permission.Roles);
        Assert.Equal(IdOf("/Engineering/Backend"), permission.InheritedFrom!.Id);

        var backend = await As("app").Drives[DriveId].Items[IdOf("/Engineering/Backend")].Permissions.GetAsync();
        var own = Assert.Single(backend!.Value!);
        Assert.Null(own.InheritedFrom);
    }

    [Fact]
    public async Task Permissions_DirectUserGrant_OnBrokenFile()
    {
        var page = await As("user:alice").Drives[DriveId].Items[IdOf("/Engineering/Backend/prod-access.md")].Permissions.GetAsync();
        var permission = Assert.Single(page!.Value!);
        var user = permission.GrantedToV2!.User!;
        Assert.Equal(Ids.User("alice"), user.Id);
        Assert.Equal("Alice Backend", user.DisplayName);
        Assert.Equal("alice@contoso-mock.onmicrosoft.com", user.AdditionalData["email"]);
        Assert.Null(permission.GrantedToV2.Group);
        Assert.Null(permission.InheritedFrom);
        Assert.Equal(["read"], permission.Roles);
    }

    [Fact]
    public async Task Sites_AndDrives_AreListed()
    {
        var client = As("user:erin");
        var sites = await client.Sites.GetAsync();
        var site = Assert.Single(sites!.Value!);
        Assert.Equal("Knowledge Base", site.DisplayName);
        Assert.Equal(Ids.Site("contoso-mock.onmicrosoft.com", "kb"), site.Id);

        var byId = await client.Sites[site.Id].GetAsync();
        Assert.Equal(site.Id, byId!.Id);

        var drives = await client.Sites[site.Id].Drives.GetAsync();
        var drive = Assert.Single(drives!.Value!);
        Assert.Equal(DriveId, drive.Id);
        Assert.Equal("documentLibrary", drive.DriveType);

        var single = await client.Drives[DriveId].GetAsync();
        Assert.Equal("Documents", single!.Name);
    }

    [Fact]
    public async Task UnknownDriveOrSite_Is404()
    {
        var ex = await Assert.ThrowsAsync<ODataError>(() => As("app").Drives["nope"].GetAsync());
        Assert.Equal("itemNotFound", ex.Error!.Code);
        await Assert.ThrowsAsync<ODataError>(() => As("app").Sites["nope"].GetAsync());
    }

    [Fact]
    public async Task UnsupportedQueryOnItems_Is400()
    {
        var ex = await Assert.ThrowsAsync<ODataError>(() =>
            As("app").Drives[DriveId].Items["root"].Children.GetAsync(rc => rc.QueryParameters.Filter = "name eq 'x'"));
        Assert.Equal(400, ex.ResponseStatusCode);
    }
}