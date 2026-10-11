using System.Net;

using MockGraph.Server.Fixtures;
using MockGraph.Server.State;

namespace MockGraph.Tests;

public sealed class SiteGroupTests : IsolatedServerTest
{
    private const string Members = "/_admin/sites/kb/siteGroups/kb-members/members";

    [Fact]
    public async Task PermissionsListing_ShowsTheSiteGroupInTheDocumentedShape()
    {
        var response = await RawGraph.Get(Http, "app", $"/v1.0/drives/{DriveId}/items/{IdOf("/General")}/permissions");
        Assert.Equal(200, response.Status);
        var permissions = response.Body!["value"]!.AsArray();
        Assert.Equal(2, permissions.Count); // group:all-staff and the site group

        var entry = permissions.Single(p => p!["grantedToV2"]!["siteGroup"] is not null)!;
        var granted = entry["grantedToV2"]!.AsObject();
        Assert.Equal(["siteGroup"], granted.Select(p => p.Key));
        Assert.Equal("3", (string?)granted["siteGroup"]!["id"]);
        Assert.Equal("Knowledge Base Members", (string?)granted["siteGroup"]!["displayName"]);
        Assert.Equal("Knowledge Base Members", (string?)granted["siteGroup"]!["loginName"]);
        Assert.Equal(["read"], entry["roles"]!.AsArray().Select(r => (string?)r));
        Assert.Null(entry["inheritedFrom"]);
        Assert.Null(entry["grantedTo"]);

        // The Entra group entry is untouched.
        Assert.Single(permissions, p => p!["grantedToV2"]!["group"] is not null);
    }

    [Fact]
    public async Task Children_InheritTheSiteGroupPermission()
    {
        var permissions = await As("app").Drives[DriveId].Items[IdOf("/General/office-wifi.md")].Permissions.GetAsync();
        var inherited = Assert.Single(permissions!.Value!, p => p.GrantedToV2?.SiteGroup is not null);
        Assert.Equal("3", inherited.GrantedToV2!.SiteGroup!.Id);
        Assert.Equal("Knowledge Base Members", inherited.GrantedToV2.SiteGroup.LoginName);
        Assert.Null(inherited.GrantedToV2.User);
        Assert.Null(inherited.GrantedToV2.Group);
        Assert.Equal(IdOf("/General"), inherited.InheritedFrom!.Id);
        Assert.Equal(DriveId, inherited.InheritedFrom.DriveId);
    }

    [Fact]
    public async Task SiteGroupGrant_GivesAccessThroughItsMembersAndNestedGroups()
    {
        const string runbook = "/Support/escalation-runbook.md";
        await Grant(runbook, "siteGroup:kb-members", "read");

        // kb-members contains group:all-staff -> engineering -> eng-backend/eng-frontend, plus carol and dave directly.
        foreach (var user in new[] { "alice", "bob", "carol", "dave" })
        {
            Assert.Equal([runbook], await OpenablePaths(As($"user:{user}"), runbook));
        }

        Assert.Empty(await OpenablePaths(As("user:erin"), runbook));

        await AddSiteGroupMember("user:erin");
        Assert.Equal([runbook], await OpenablePaths(As("user:erin"), runbook));

        await RemoveSiteGroupMember("user:erin");
        Assert.Empty(await OpenablePaths(As("user:erin"), runbook));
    }

    [Fact]
    public async Task SiteGroupAlone_CanCarryTheAccess()
    {
        // With the direct group grant gone, /General is readable only through the site group's members.
        await Revoke("/General", "group:all-staff");
        Assert.Equal(["/General/vacation-policy.md"], await OpenablePaths(As("user:carol"), "/General/vacation-policy.md"));

        await RemoveSiteGroupMember("group:all-staff");
        Assert.Empty(await OpenablePaths(As("user:carol"), "/General/vacation-policy.md"));

        await AddSiteGroupMember("group:support");
        Assert.Equal(["/General/vacation-policy.md"], await OpenablePaths(As("user:dave"), "/General/vacation-policy.md"));
        Assert.Empty(await OpenablePaths(As("user:carol"), "/General/vacation-policy.md"));
    }

    [Fact]
    public async Task MembershipChange_ShowsUpAsSharingChangesInDriveDelta()
    {
        var app = As("app");
        var first = await Drain(app);
        var deltaLink = first.DeltaLink;

        var added = await AddSiteGroupMember("user:erin");
        Assert.True(added["changed"]!.GetValue<bool>());

        var sync = await Drain(app, deltaLink, SharingPrefer);
        // Every item that gives the site group access: /General and its three children.
        Assert.Equal(
            new[] { "/General", "/General/floor-plan.png", "/General/office-wifi.md", "/General/vacation-policy.md" }.Select(IdOf).Order(),
            sync.Ids.Order());
        Assert.All(sync.Items, i => Assert.True(IsShared(i)));

        var again = await AddSiteGroupMember("user:erin");
        Assert.False(again["changed"]!.GetValue<bool>());
    }

    [Fact]
    public async Task MembershipChange_IsVisibleToTheAffectedUserInTheirOwnDelta()
    {
        var erin = As("user:erin");
        var baseline = await Drain(erin);
        Assert.DoesNotContain(IdOf("/General/vacation-policy.md"), baseline.Ids);

        await AddSiteGroupMember("user:erin");
        var gained = await Drain(erin, baseline.DeltaLink);
        Assert.Contains(IdOf("/General/vacation-policy.md"), gained.Ids);
        Assert.All(gained.Items.Where(i => i.Name != "root"), i => Assert.Null(i.Deleted));

        await RemoveSiteGroupMember("user:erin");
        var lost = await Drain(erin, gained.DeltaLink);
        Assert.Contains(lost.Items, i => i.Id == IdOf("/General/vacation-policy.md") && i.Deleted is not null);
    }

    [Fact]
    public async Task MembershipChange_BumpsTheSequenceEvenWhenNoItemIsAffected()
    {
        var before = Store.Sequence;
        await AddSiteGroupMember("user:erin");
        Assert.True(Store.Sequence > before);
        Assert.Contains(Store.Model.SiteGroups, g => g.Key == "kb-members" && g.Members.Contains(new PrincipalRef(PrincipalKind.User, "erin")));
    }

    public static TheoryData<HttpMethod, string, object?, HttpStatusCode> BadRequests => new()
    {
        { HttpMethod.Post, "/_admin/permissions", new { itemPath = "/HR", principal = "siteGroup:nope", role = "read" }, HttpStatusCode.NotFound },
        { HttpMethod.Post, "/_admin/permissions", new { itemPath = "/HR", principal = "siteGroup:", role = "read" }, HttpStatusCode.BadRequest },
        { HttpMethod.Post, "/_admin/sites/kb/siteGroups/kb-members/members", new { member = "siteGroup:kb-members" }, HttpStatusCode.BadRequest },
        { HttpMethod.Post, "/_admin/sites/kb/siteGroups/kb-members/members", new { member = "user:zed" }, HttpStatusCode.NotFound },
        { HttpMethod.Post, "/_admin/sites/kb/siteGroups/kb-members/members", new { }, HttpStatusCode.BadRequest },
        { HttpMethod.Post, "/_admin/sites/kb/siteGroups/nope/members", new { member = "user:erin" }, HttpStatusCode.NotFound },
        { HttpMethod.Post, "/_admin/sites/nope/siteGroups/kb-members/members", new { member = "user:erin" }, HttpStatusCode.NotFound },
        { HttpMethod.Delete, "/_admin/sites/kb/siteGroups/kb-members/members", new { member = "user:erin" }, HttpStatusCode.NotFound },
        { HttpMethod.Post, "/_admin/groups/hr/members", new { member = "siteGroup:kb-members" }, HttpStatusCode.BadRequest },
    };

    [Theory]
    [MemberData(nameof(BadRequests))]
    public async Task BadSiteGroupInput_GivesClearJsonErrors(HttpMethod method, string url, object? body, HttpStatusCode expected)
    {
        var error = await Admin(method, url, body, expected);
        Assert.False(string.IsNullOrWhiteSpace(error["error"]!["message"]!.GetValue<string>()));
    }

    [Fact]
    public async Task SiteGroupAddressedByItsNumericIdOrTheSiteId_Works()
    {
        var siteId = Ids.Site("contoso-mock.onmicrosoft.com", "kb");
        await Admin(HttpMethod.Post, $"/_admin/sites/{Uri.EscapeDataString(siteId)}/siteGroups/3/members", new { member = "user:erin" });
        Assert.Contains(Store.Model.SiteGroups.Single().Members, m => m.Key == "erin");
    }

    [Fact]
    public async Task StateRoundTripsSiteGroupsAndPrincipals()
    {
        await AddSiteGroupMember("user:erin");
        await Grant("/HR/salary-bands.md", "siteGroup:kb-members", "write");

        using var response = await Send(HttpMethod.Get, "/_admin/state");
        var yaml = await response.Content.ReadAsStringAsync();
        Assert.Contains("siteGroups:", yaml);
        Assert.Contains("principal: siteGroup:kb-members", yaml);
        Assert.DoesNotContain("id: root", yaml); // the implicit root site is not part of the fixture

        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var path = Path.Combine(dir.FullName, "state.yaml");
            await File.WriteAllTextAsync(path, yaml);
            var reloaded = FixtureLoader.Load(path);
            Assert.Equal(Store.Model.SiteGroups, reloaded.SiteGroups, (a, b) => a.Key == b.Key && a.WireId == b.WireId && a.Members.SequenceEqual(b.Members));
            Assert.Equal(Store.Model.Sites, reloaded.Sites);
            Assert.Equal(
                Store.Model.Items.SelectMany(i => i.Grants.Select(g => (i.Path, g))),
                reloaded.Items.SelectMany(i => i.Grants.Select(g => (i.Path, g))));
        }
        finally
        {
            dir.Delete(true);
        }
    }

    [Fact]
    public async Task Reset_RestoresTheFixtureSiteGroup()
    {
        await AddSiteGroupMember("user:erin");
        await Admin(HttpMethod.Post, "/_admin/reset");
        Assert.DoesNotContain(Store.Model.SiteGroups.Single().Members, m => m.Key == "erin");
    }

    private Task<System.Text.Json.Nodes.JsonNode> AddSiteGroupMember(string member) =>
        Admin(HttpMethod.Post, Members, new { member });

    private Task<System.Text.Json.Nodes.JsonNode> RemoveSiteGroupMember(string member) =>
        Admin(HttpMethod.Delete, Members, new { member });
}