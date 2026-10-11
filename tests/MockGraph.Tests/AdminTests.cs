using System.Net;

using MockGraph.Server.Fixtures;
using MockGraph.Server.State;

namespace MockGraph.Tests;

public sealed class AdminTests : IsolatedServerTest
{
    [Fact]
    public async Task GroupMembershipCycleIsRejected()
    {
        // engineering contains eng-backend; adding engineering into eng-backend would loop.
        var error = await Admin(HttpMethod.Post, "/_admin/groups/eng-backend/members", new { member = "group:engineering" }, HttpStatusCode.BadRequest);
        Assert.Contains("cycle", error["error"]!["message"]!.GetValue<string>());

        // transitive: all-staff -> engineering -> eng-backend
        await Admin(HttpMethod.Post, "/_admin/groups/eng-backend/members", new { member = "group:all-staff" }, HttpStatusCode.BadRequest);
        await Admin(HttpMethod.Post, "/_admin/groups/hr/members", new { member = "group:hr" }, HttpStatusCode.BadRequest);

        // a legitimate nested add still works and is idempotent
        var added = await Admin(HttpMethod.Post, "/_admin/groups/hr/members", new { member = "group:support" });
        Assert.True(added["changed"]!.GetValue<bool>());
        var again = await Admin(HttpMethod.Post, "/_admin/groups/hr/members", new { member = "group:support" });
        Assert.False(again["changed"]!.GetValue<bool>());
    }

    [Fact]
    public async Task MembersAreReflectedByGraphDirectoryEndpoints()
    {
        await AddMember("hr", "user:erin");

        var members = await As("app").Groups["hr"].Members.GetAsync();
        Assert.Contains(Ids.User("erin"), members!.Value!.Select(m => m.Id));
        Assert.Single(await As("user:erin").Me.MemberOf.GetAsync() is { Value: { } v } ? v : []);
    }

    public static TheoryData<HttpMethod, string, object?, HttpStatusCode> BadRequests => new()
    {
        { HttpMethod.Post, "/_admin/permissions", new { itemPath = "/General", role = "read" }, HttpStatusCode.BadRequest },
        { HttpMethod.Post, "/_admin/permissions", new { itemPath = "/General", principal = "user:alice", role = "admin" }, HttpStatusCode.BadRequest },
        { HttpMethod.Post, "/_admin/permissions", new { itemPath = "/General", principal = "alice", role = "read" }, HttpStatusCode.BadRequest },
        { HttpMethod.Post, "/_admin/permissions", new { itemPath = "/General", itemId = "x", principal = "user:alice", role = "read" }, HttpStatusCode.BadRequest },
        { HttpMethod.Post, "/_admin/permissions", new { principal = "user:alice", role = "read" }, HttpStatusCode.BadRequest },
        { HttpMethod.Post, "/_admin/permissions", new { itemPath = "/", principal = "user:alice", role = "read" }, HttpStatusCode.BadRequest },
        { HttpMethod.Post, "/_admin/permissions", new { itemPath = "/Nope", principal = "user:alice", role = "read" }, HttpStatusCode.NotFound },
        { HttpMethod.Post, "/_admin/permissions", new { itemPath = "/General", principal = "user:zed", role = "read" }, HttpStatusCode.NotFound },
        { HttpMethod.Post, "/_admin/permissions", new { itemPath = "/General", principal = "user:alice", role = "read", driveId = "nope" }, HttpStatusCode.NotFound },
        { HttpMethod.Delete, "/_admin/permissions", new { itemPath = "/General", principal = "user:erin" }, HttpStatusCode.NotFound },
        { HttpMethod.Post, "/_admin/groups/nope/members", new { member = "user:alice" }, HttpStatusCode.NotFound },
        { HttpMethod.Post, "/_admin/groups/hr/members", new { member = "user:zed" }, HttpStatusCode.NotFound },
        { HttpMethod.Post, "/_admin/groups/hr/members", new { }, HttpStatusCode.BadRequest },
        { HttpMethod.Delete, "/_admin/groups/hr/members", new { member = "user:erin" }, HttpStatusCode.NotFound },
        { HttpMethod.Delete, "/_admin/files?path=/Nope.md", null, HttpStatusCode.NotFound },
        { HttpMethod.Delete, "/_admin/files", null, HttpStatusCode.BadRequest },
        { HttpMethod.Delete, "/_admin/files?path=/", null, HttpStatusCode.BadRequest },
    };

    [Theory]
    [MemberData(nameof(BadRequests))]
    public async Task BadInputGivesClearJsonErrors(HttpMethod method, string url, object? body, HttpStatusCode expected)
    {
        var error = await Admin(method, url, body, expected);
        Assert.False(string.IsNullOrWhiteSpace(error["error"]!["message"]!.GetValue<string>()));
        Assert.False(string.IsNullOrWhiteSpace(error["error"]!["code"]!.GetValue<string>()));
    }

    [Fact]
    public async Task MalformedJsonAndNonMultipartAreBadRequests()
    {
        using var notMultipart = await Send(HttpMethod.Post, "/_admin/files", new { path = "/x" });
        Assert.Equal(HttpStatusCode.BadRequest, notMultipart.StatusCode);

        using var malformed = new HttpRequestMessage(HttpMethod.Post, "/_admin/permissions")
        {
            Content = new StringContent("{not json", System.Text.Encoding.UTF8, "application/json"),
        };
        using var response = await Http.SendAsync(malformed);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task GrantAndRevokeChangeWhatTheUserSees()
    {
        var carol = As("user:carol");
        Assert.DoesNotContain("/Support/escalation-runbook.md", await OpenablePaths(carol, "/Support/escalation-runbook.md"));

        await Admin(HttpMethod.Post, "/_admin/permissions", new { itemId = IdOf("/Support/escalation-runbook.md"), principal = "user:carol", role = "write" });
        Assert.Equal(["/Support/escalation-runbook.md"], await OpenablePaths(carol, "/Support/escalation-runbook.md"));

        // changing the role of an existing grant replaces it; revoking without a role removes it entirely
        await Grant("/Support/escalation-runbook.md", "user:carol", "owner");
        var permissions = await As("app").Drives[DriveId].Items[IdOf("/Support/escalation-runbook.md")].Permissions.GetAsync();
        Assert.Single(permissions!.Value!, p => p.Roles!.Contains("owner") && p.GrantedToV2?.User?.Id == Ids.User("carol"));

        await Revoke("/Support/escalation-runbook.md", "user:carol");
        Assert.DoesNotContain("/Support/escalation-runbook.md", await OpenablePaths(carol, "/Support/escalation-runbook.md"));
    }

    [Fact]
    public async Task FilesCanBeAddedReplacedAndDeletedAndServeTheirContent()
    {
        var created = await PutFile("/Projects/Alpha/notes.md", "v1");
        Assert.Equal("/Projects/Alpha/notes.md", created["path"]!.GetValue<string>());
        Assert.True(created["created"]!.GetValue<bool>());

        await PutFile("/Projects/Alpha/notes.md", "version two", HttpStatusCode.OK);
        await using var content = await As("app").Drives[DriveId].Items[IdOf("/Projects/Alpha/notes.md")].Content.GetAsync();
        Assert.Equal("version two", await new StreamReader(content!).ReadToEndAsync());

        var folder = await As("app").Drives[DriveId].Items[IdOf("/Projects")].GetAsync();
        Assert.NotNull(folder!.Folder);

        await Admin(HttpMethod.Post, "/_admin/files", null, HttpStatusCode.BadRequest);
        await PutFile("/Projects/Alpha/notes.md/child.md", "x", HttpStatusCode.BadRequest);
        await PutFile("/Projects", "x", HttpStatusCode.BadRequest);
        await DeleteFile("/Projects");
        Assert.DoesNotContain(Store.Model.Items, i => i.Path.StartsWith("/Projects"));
    }

    [Fact]
    public async Task EveryMutationAdvancesTheSequenceAndNoOpsDoNot()
    {
        var before = Store.Sequence;
        await Grant("/HR/salary-bands.md", "user:dave");
        var afterGrant = Store.Sequence;
        Assert.True(afterGrant > before);

        var again = await Admin(HttpMethod.Post, "/_admin/permissions", new { itemPath = "/HR/salary-bands.md", principal = "user:dave", role = "read" });
        Assert.False(again["changed"]!.GetValue<bool>());
        Assert.Equal(afterGrant, Store.Sequence);
    }

    [Fact]
    public async Task StateIsYamlThatReloadsAsAnEqualFixture()
    {
        await Grant("/HR/salary-bands.md", "user:dave", "write");
        await AddMember("hr", "group:support");
        await RemoveMember("eng-backend", "user:alice");
        await PutFile("/Projects/Alpha/notes.md", "# Notes\n\nline two\n");
        await PutFile("/Projects/trailing.md", "has trailing space \nand more");
        await DeleteFile("/General/office-wifi.md");
        await PutBytes("/Projects/blob.bin", [0, 1, 2, 255, 254]);

        var yaml = await StateYaml();
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var path = Path.Combine(dir.FullName, "state.yaml");
            await File.WriteAllTextAsync(path, yaml);
            var reloaded = FixtureLoader.Load(path);
            AssertEqual(Store.Model, reloaded);
        }
        finally
        {
            dir.Delete(true);
        }

        Assert.Contains("breakInheritance: true", yaml);
        Assert.Contains("# Vacation policy", yaml);
    }

    [Fact]
    public async Task StateOfAFreshServerRoundTripsIncludingBinaryFile()
    {
        var yaml = await StateYaml();
        Assert.Contains("contentBase64", yaml); // floor-plan.png

        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var path = Path.Combine(dir.FullName, "state.yaml");
            await File.WriteAllTextAsync(path, yaml);
            AssertEqual(Store.Model, FixtureLoader.Load(path));
        }
        finally
        {
            dir.Delete(true);
        }
    }

    private async Task<string> StateYaml()
    {
        using var response = await Send(HttpMethod.Get, "/_admin/state");
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        Assert.StartsWith("text/yaml", response.Content.Headers.ContentType!.ToString());
        return await response.Content.ReadAsStringAsync();
    }

    private static void AssertEqual(GraphModel expected, GraphModel actual)
    {
        Assert.Equal(expected.TenantDomain, actual.TenantDomain);
        Assert.Equal(expected.Users, actual.Users);
        Assert.Equal(expected.Sites, actual.Sites);
        Assert.Equal(expected.Drives, actual.Drives);
        Assert.Equal(expected.Groups.Select(g => (g.Key, g.Members)), actual.Groups.Select(g => (g.Key, g.Members)), (a, b) =>
            a.Key == b.Key && a.Members.SequenceEqual(b.Members));

        Assert.Equal(expected.Items.Count, actual.Items.Count);
        foreach (var (e, a) in expected.Items.Zip(actual.Items))
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
}