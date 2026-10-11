using System.Net;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Graph;
using Microsoft.Graph.Models;
using Microsoft.Graph.Models.ODataErrors;

using MockGraph.Server.Fixtures;
using MockGraph.Server.State;

using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace MockGraph.Tests;

/// <summary>A server that serves the rich Contoso fixture instead of the small sample.</summary>
public sealed class CompanyFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder) =>
        builder.UseSetting("MockGraph:FixturePath", "fixtures/company.yaml");

    public GraphServiceClient Client(string token) => TestSupport.CreateClient(CreateClient(), token);
}

/// <summary>
/// Contract of fixtures/company.yaml: it is a superset of the sample, it loads and round-trips, delta enumerates it,
/// every binary source downloads intact, and every principal sees exactly what fixtures/company.visibility.yaml says.
/// The oracle is computed by tools/fixture-visibility/compute_visibility.py, which shares no code with the server.
/// </summary>
public sealed class CompanyFixtureTests(CompanyFactory factory) : IClassFixture<CompanyFactory>
{
    private static readonly Lazy<string> FixturePath = new(() => FixtureLoader.ResolvePath("fixtures/company.yaml", AppContext.BaseDirectory));
    private static readonly Lazy<GraphModel> LazyModel = new(() => FixtureLoader.Load(FixturePath.Value));
    private static readonly Lazy<Oracle> LazyOracle = new(() =>
    {
        var path = FixtureLoader.ResolvePath("fixtures/company.visibility.yaml", AppContext.BaseDirectory);
        return YamlReader().Deserialize<Oracle>(File.ReadAllText(path));
    });

    private static GraphModel Model => LazyModel.Value;

    private static IDeserializer YamlReader() => new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    public static TheoryData<string> Principals
    {
        get
        {
            var data = new TheoryData<string>();
            foreach (var user in LazyOracle.Value.Users.Keys)
            {
                data.Add(user);
            }

            data.Add("app");
            return data;
        }
    }

    private static string Label(string driveKey, string path) => $"{driveKey}:{path}";

    private static OracleEntry ExpectedFor(string who) => who == "app" ? LazyOracle.Value.App : LazyOracle.Value.Users[who];

    // ---- RBAC oracle ----

    [Theory]
    [MemberData(nameof(Principals))]
    public async Task PrincipalSeesExactlyWhatTheOracleSays(string who)
    {
        var expected = ExpectedFor(who);
        var client = factory.Client(who == "app" ? "app" : $"user:{who}");
        var files = new List<string>();
        var folders = new List<string>();
        var walked = new List<string>();

        foreach (var drive in Model.Drives)
        {
            walked.AddRange(await Walk(client, drive));

            // Open every item by id: a readable item under an unreadable folder is not reachable by walking, but it is readable.
            foreach (var item in Model.Items.Where(i => i.DriveKey == drive.Key))
            {
                DriveItem? opened = null;
                try
                {
                    opened = await client.Drives[drive.Id].Items[item.Id].GetAsync();
                }
                catch (ODataError e)
                {
                    Assert.Equal(404, e.ResponseStatusCode);
                    Assert.Equal("itemNotFound", e.Error!.Code);
                }

                if (opened is null)
                {
                    continue;
                }

                Assert.Equal(item.IsFolder, opened.Folder is not null);
                (item.IsFolder ? folders : files).Add(Label(drive.Key, item.Path));
            }
        }

        Assert.Equal(expected.Files.Order(StringComparer.Ordinal), files.Order(StringComparer.Ordinal));
        Assert.Equal(expected.Folders.Order(StringComparer.Ordinal), folders.Order(StringComparer.Ordinal));

        // Walking from the root reaches a readable item only when every folder above it is readable too.
        var readableFolders = expected.Folders.ToHashSet(StringComparer.Ordinal);
        var reachable = expected.Files.Concat(expected.Folders).Where(l => AncestorsReadable(l, readableFolders));
        Assert.Equal(reachable.Order(StringComparer.Ordinal), walked.Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task TransitiveMembershipMatchesTheOracle()
    {
        var app = factory.Client("app");
        foreach (var (user, expected) in LazyOracle.Value.Users)
        {
            var page = await app.Users[user].TransitiveMemberOf.GetAsync();
            var ids = new List<string>();
            await PageIterator<DirectoryObject, DirectoryObjectCollectionResponse>
                .CreatePageIterator(app, page!, o =>
                {
                    ids.Add(o.Id!);
                    return true;
                }).IterateAsync();

            Assert.Equal(expected.Groups.Select(Ids.Group).Order(StringComparer.Ordinal), ids.Order(StringComparer.Ordinal));
        }
    }

    private static bool AncestorsReadable(string label, HashSet<string> readableFolders)
    {
        var separator = label.IndexOf(':');
        var drive = label[..separator];
        var path = label[(separator + 1)..];
        for (var parent = ItemPaths.ParentOf(path); parent != "/"; parent = ItemPaths.ParentOf(parent))
        {
            if (!readableFolders.Contains(Label(drive, parent)))
            {
                return false;
            }
        }

        return true;
    }

    private static async Task<List<string>> Walk(GraphServiceClient client, DriveRecord drive)
    {
        var found = new List<string>();
        var pending = new Queue<(string Id, string Path)>();
        pending.Enqueue(("root", ""));
        while (pending.TryDequeue(out var folder))
        {
            var page = await client.Drives[drive.Id].Items[folder.Id].Children.GetAsync();
            var children = new List<DriveItem>();
            await PageIterator<DriveItem, DriveItemCollectionResponse>
                .CreatePageIterator(client, page!, c =>
                {
                    children.Add(c);
                    return true;
                }).IterateAsync();

            foreach (var child in children)
            {
                var path = $"{folder.Path}/{child.Name}";
                found.Add(Label(drive.Key, path));
                if (child.Folder is not null)
                {
                    pending.Enqueue((child.Id!, path));
                }
            }
        }

        return found;
    }

    // ---- superset of sample.yaml ----

    [Fact]
    public void CompanyIsASupersetOfTheSample()
    {
        var sample = FixtureLoader.Load(FixtureLoader.ResolvePath("fixtures/sample.yaml", AppContext.BaseDirectory));

        Assert.Equal(sample.TenantDomain, Model.TenantDomain);
        Assert.All(sample.Users, u => Assert.Contains(u, Model.Users));
        foreach (var group in sample.Groups)
        {
            var rich = Model.Groups.Single(g => g.Key == group.Key);
            Assert.Equal(group.Id, rich.Id);
            Assert.Equal(group.DisplayName, rich.DisplayName);
            Assert.All(group.Members, m => Assert.Contains(m, rich.Members));
        }

        Assert.Contains(sample.Sites.Single(s => s.Key == "kb"), Model.Sites);
        Assert.Contains(sample.Drives.Single(), Model.Drives);
        var siteGroup = sample.SiteGroups.Single();
        var richSiteGroup = Model.SiteGroups.Single(g => g.Key == siteGroup.Key);
        Assert.Equal(siteGroup.Members, richSiteGroup.Members);

        foreach (var item in sample.Items)
        {
            var rich = Model.Items.Single(i => i.DriveKey == item.DriveKey && i.Path == item.Path);
            Assert.Equal(item.Id, rich.Id);
            Assert.Equal(item.IsFolder, rich.IsFolder);
            Assert.Equal(item.BreakInheritance, rich.BreakInheritance);
            Assert.Equal(item.Grants, rich.Grants);
            Assert.Equal(item.Content, rich.Content);
            Assert.Equal(item.ContentType, rich.ContentType);
        }
    }

    [Theory]
    [InlineData("alice", new[] { "/General", "/General/vacation-policy.md", "/General/office-wifi.md", "/General/floor-plan.png", "/Engineering", "/Engineering/local-dev-setup.md", "/Engineering/Backend", "/Engineering/Backend/db-migrations.md", "/Engineering/Backend/prod-access.md", "/Shared/contractor-onboarding.md" })]
    [InlineData("bob", new[] { "/General", "/General/vacation-policy.md", "/General/office-wifi.md", "/General/floor-plan.png", "/Engineering", "/Engineering/local-dev-setup.md", "/Shared/contractor-onboarding.md" })]
    [InlineData("carol", new[] { "/General", "/General/vacation-policy.md", "/General/office-wifi.md", "/General/floor-plan.png", "/HR", "/HR/salary-bands.md" })]
    [InlineData("dave", new[] { "/General", "/General/vacation-policy.md", "/General/office-wifi.md", "/General/floor-plan.png", "/Support", "/Support/escalation-runbook.md" })]
    [InlineData("erin", new[] { "/Shared/contractor-onboarding.md" })]
    public void TheDocumentedSampleVisibilityStaysTrue(string user, string[] expectedSamplePaths)
    {
        var samplePaths = FixtureLoader.Load(FixtureLoader.ResolvePath("fixtures/sample.yaml", AppContext.BaseDirectory))
            .Items.Select(i => Label("kb-docs", i.Path)).ToHashSet(StringComparer.Ordinal);
        var entry = LazyOracle.Value.Users[user];
        var visibleSamplePaths = entry.Files.Concat(entry.Folders).Where(samplePaths.Contains).Order(StringComparer.Ordinal);

        Assert.Equal(expectedSamplePaths.Select(p => Label("kb-docs", p)).Order(StringComparer.Ordinal), visibleSamplePaths);
    }

    // ---- shape, state round trip, delta, binary sources ----

    [Fact]
    public void FixtureHasTheDocumentedShape()
    {
        Assert.InRange(Model.Users.Count, 12, 17);
        Assert.InRange(Model.Groups.Count, 12, 17);
        Assert.Equal(4, Model.Sites.Count(s => !s.IsImplicit));
        Assert.True(Model.Drives.GroupBy(d => d.SiteKey).Any(g => g.Count() >= 2), "one site should have two drives");
        Assert.InRange(Model.Items.Count(i => !i.IsFolder), 60, 80);
        Assert.Contains(Model.Groups, g => g.Members.Count == 0);
        Assert.Contains(Model.Groups, g => Model.Groups.Count(p => p.Members.Contains(g.Ref)) >= 2);
        Assert.True(Depth("all-staff") >= 4, "group nesting should be at least 3 levels below all-staff");
        Assert.Contains(Model.Users, u => !Model.Groups.Any(g => g.Members.Contains(u.Ref)));
        Assert.Contains(Model.Items, i => i.Path.Count(c => c == '/') >= 5);
    }

    private static int Depth(string groupKey)
    {
        var group = Model.Groups.Single(g => g.Key == groupKey);
        return 1 + group.Members.Where(m => m.Kind == PrincipalKind.Group).Select(m => Depth(m.Key)).DefaultIfEmpty(0).Max();
    }

    [Fact]
    public async Task StateEndpointRoundTripsTheFixture()
    {
        using var response = await factory.CreateClient().GetAsync("/_admin/state");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var yaml = await response.Content.ReadAsStringAsync();

        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var path = Path.Combine(dir.FullName, "state.yaml");
            await File.WriteAllTextAsync(path, yaml);
            var reloaded = FixtureLoader.Load(path);

            Assert.Equal(Model.TenantDomain, reloaded.TenantDomain);
            Assert.Equal(Model.Users, reloaded.Users);
            Assert.Equal(Model.Sites, reloaded.Sites);
            Assert.Equal(Model.Drives, reloaded.Drives);
            Assert.Equal(Model.SiteGroups.Select(g => (g.Key, g.SiteKey, g.Members)), reloaded.SiteGroups.Select(g => (g.Key, g.SiteKey, g.Members)), (a, b) =>
                a.Key == b.Key && a.SiteKey == b.SiteKey && a.Members.SequenceEqual(b.Members));
            Assert.Equal(Model.Groups.Select(g => (g.Key, g.Members)), reloaded.Groups.Select(g => (g.Key, g.Members)), (a, b) =>
                a.Key == b.Key && a.Members.SequenceEqual(b.Members));

            Assert.Equal(Model.Items.Count, reloaded.Items.Count);
            foreach (var (e, a) in Model.Items.Zip(reloaded.Items))
            {
                Assert.Equal(e.Path, a.Path);
                Assert.Equal(e.Id, a.Id);
                Assert.Equal(e.IsFolder, a.IsFolder);
                Assert.Equal(e.BreakInheritance, a.BreakInheritance);
                Assert.Equal(e.Grants, a.Grants);
                Assert.Equal(e.Content, a.Content);
                Assert.Equal(e.ContentType, a.ContentType);
            }
        }
        finally
        {
            dir.Delete(true);
        }
    }

    [Fact]
    public async Task DriveDeltaEnumeratesEveryItemOfEveryDrive()
    {
        var app = factory.Client("app");
        foreach (var drive in Model.Drives)
        {
            var ids = new List<string>();
            var builder = app.Drives[drive.Id].Items["root"].Delta;
            while (true)
            {
                var page = await builder.GetAsDeltaGetResponseAsync();
                ids.AddRange(page!.Value!.Select(i => i.Id!));
                if (page.OdataDeltaLink is not null)
                {
                    break;
                }

                builder = app.Drives[drive.Id].Items["root"].Delta.WithUrl(page.OdataNextLink!);
            }

            var expected = Model.Items.Where(i => i.DriveKey == drive.Key).Select(i => i.Id).ToList();
            Assert.Empty(expected.Except(ids));
            Assert.InRange(ids.Count - expected.Count, 0, 1); // the root item may be included
            Assert.Equal(ids.Count, ids.Distinct().Count());
        }
    }

    private static readonly Dictionary<string, string> ExpectedTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".docx"] = "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        [".xlsx"] = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        [".pptx"] = "application/vnd.openxmlformats-officedocument.presentationml.presentation",
        [".pdf"] = "application/pdf",
        [".png"] = "image/png",
        [".md"] = "text/markdown",
        [".txt"] = "text/plain",
    };

    [Fact]
    public async Task EverySourceFileDownloadsIntactWithTheRightContentType()
    {
        var fixtureDir = Path.GetDirectoryName(FixturePath.Value)!;
        var dto = YamlReader().Deserialize<FixtureDto>(await File.ReadAllTextAsync(FixturePath.Value));
        var sources = dto.Sites.SelectMany(s => s.Drives).SelectMany(d => d.Items.Where(i => i.Source is not null).Select(i => (Drive: d, Item: i))).ToList();
        Assert.True(sources.Count(s => s.Item.Source!.EndsWith(".docx", StringComparison.OrdinalIgnoreCase)) >= 6);
        Assert.True(sources.Count(s => s.Item.Source!.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)) >= 6);
        Assert.True(sources.Count(s => s.Item.Source!.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase)) >= 2);
        Assert.True(sources.Count(s => s.Item.Source!.EndsWith(".pptx", StringComparison.OrdinalIgnoreCase)) >= 1);

        using var http = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var sdk = factory.Client("app");
        foreach (var (drive, item) in sources)
        {
            var file = new[] { Path.Combine(fixtureDir, item.Source!), Path.Combine(fixtureDir, "files", item.Source!) }.First(File.Exists);
            var declared = new FileInfo(file).Length;
            Assert.True(declared > 0, item.Path);
            var driveId = Ids.Drive(drive.Id);
            var itemId = Ids.Item(driveId, FixtureLoader.NormalizePath(item.Path));

            var meta = await sdk.Drives[driveId].Items[itemId].GetAsync();
            Assert.Equal(declared, meta!.Size);
            var extension = Path.GetExtension(item.Path);
            Assert.Equal(ExpectedTypes[extension], meta.File!.MimeType);

            await using var stream = (await sdk.Drives[driveId].Items[itemId].Content.GetAsync())!;
            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer);
            Assert.Equal(declared, buffer.Length);
            Assert.Equal(await File.ReadAllBytesAsync(file), buffer.ToArray());

            using var request = new HttpRequestMessage(HttpMethod.Get, $"{TestSupport.BaseUrl}/drives/{driveId}/items/{itemId}/content");
            request.Headers.Authorization = new("Bearer", "app");
            using var redirect = await http.SendAsync(request);
            Assert.Equal(HttpStatusCode.Found, redirect.StatusCode);
            using var download = await http.GetAsync(redirect.Headers.Location);
            Assert.Equal(ExpectedTypes[extension], download.Content.Headers.ContentType!.MediaType);
            Assert.Equal(declared, download.Content.Headers.ContentLength);
        }
    }

    [Theory]
    [InlineData("/General/IT/Device Policy.txt", "text/plain")]
    [InlineData("/General/Графік свят 2026.md", "text/markdown")]
    public async Task TextFilesWithSpacesAndNonAsciiNamesAreServed(string path, string mediaType)
    {
        var driveId = Ids.Drive("kb-docs");
        var app = factory.Client("app");
        var item = await app.Drives[driveId].Items[Ids.Item(driveId, path)].GetAsync();
        Assert.Equal(path[(path.LastIndexOf('/') + 1)..], item!.Name);
        Assert.Equal(mediaType, item.File!.MimeType);

        var byPath = await app.Drives[driveId].Root.ItemWithPath(path.TrimStart('/')).GetAsync();
        Assert.Equal(item.Id, byPath!.Id);
    }

    internal sealed class Oracle
    {
        public Dictionary<string, OracleEntry> Users { get; set; } = [];

        public OracleEntry App { get; set; } = new();
    }

    internal sealed class OracleEntry
    {
        public List<string> Groups { get; set; } = [];

        public List<string> Files { get; set; } = [];

        public List<string> Folders { get; set; } = [];
    }
}