using System.Net;
using System.Text.Json.Nodes;

using MockGraph.Server.State;

namespace MockGraph.Tests;

public sealed class GroupsDeltaTests : IsolatedServerTest
{
    private const string Url = "/v1.0/groups/delta";

    private async Task<(List<JsonObject> Groups, string DeltaLink, int Pages)> Drain(string url = Url, string token = "app")
    {
        var groups = new List<JsonObject>();
        for (var pages = 1; pages < 50; pages++)
        {
            var page = await RawGet(token, url);
            groups.AddRange(page["value"]!.AsArray().Select(n => n!.AsObject()));
            if (page["@odata.deltaLink"] is { } done)
            {
                return (groups, done.GetValue<string>(), pages);
            }

            url = page["@odata.nextLink"]!.GetValue<string>();
        }

        throw new InvalidOperationException("Too many pages.");
    }

    private static JsonObject Group(IEnumerable<JsonObject> groups, string key) =>
        groups.Single(g => g["id"]!.GetValue<string>() == Ids.Group(key));

    private static (string Type, string Id, bool Removed)[] Members(JsonObject group) =>
        [.. group["members@delta"]!.AsArray().Select(m => (
            m!["@odata.type"]!.GetValue<string>(),
            m["id"]!.GetValue<string>(),
            m["@removed"] is not null))];

    [Fact]
    public async Task InitialSyncReturnsEveryGroupWithTypedMembersPaged()
    {
        var (groups, deltaLink, pages) = await Drain();

        Assert.Equal(6, groups.Count);
        Assert.True(pages > 1);
        Assert.Contains("$deltatoken=", deltaLink);
        Assert.Equal(
            [("#microsoft.graph.group", Ids.Group("engineering"), false), ("#microsoft.graph.user", Ids.User("carol"), false), ("#microsoft.graph.user", Ids.User("dave"), false)],
            Members(Group(groups, "all-staff")).OrderBy(m => m.Type == "#microsoft.graph.group" ? 0 : 1).ThenBy(m => m.Id));
        Assert.Equal([("#microsoft.graph.user", Ids.User("alice"), false)], Members(Group(groups, "eng-backend")));
        Assert.Equal("Engineering - Backend", Group(groups, "eng-backend")["displayName"]!.GetValue<string>());
    }

    [Fact]
    public async Task SelectWithoutMembersOmitsMembersDeltaAndSelectWithMembersKeepsIt()
    {
        var (without, _, _) = await Drain($"{Url}?$select=displayName");
        Assert.All(without, g => Assert.Null(g["members@delta"]));

        var (with, _, _) = await Drain($"{Url}?$select=displayName,members");
        Assert.All(with, g => Assert.NotNull(g["members@delta"]));
        Assert.All(with, g => Assert.Null(g["mailNickname"]));
    }

    [Fact]
    public async Task NoChangesYieldsEmptyValue()
    {
        var (_, deltaLink, _) = await Drain();
        var (groups, next, _) = await Drain(deltaLink);

        Assert.Empty(groups);
        Assert.NotEmpty(next);
    }

    [Fact]
    public async Task IncrementalAddAndRemoveShowAddedMembersAndRemovedMarkers()
    {
        var (_, baseline, _) = await Drain();

        await AddMember("eng-frontend", "user:erin");
        await AddMember("hr", "group:support");
        await RemoveMember("eng-backend", "user:alice");
        var (groups, _, _) = await Drain(baseline);

        Assert.Equal(3, groups.Count);
        Assert.Equal([("#microsoft.graph.user", Ids.User("erin"), false)], Members(Group(groups, "eng-frontend")));
        Assert.Equal([("#microsoft.graph.group", Ids.Group("support"), false)], Members(Group(groups, "hr")));
        Assert.Equal([("#microsoft.graph.user", Ids.User("alice"), true)], Members(Group(groups, "eng-backend")));

        var removed = Group(groups, "eng-backend")["members@delta"]![0]!["@removed"]!.AsObject();
        Assert.Equal("deleted", removed["reason"]!.GetValue<string>());
    }

    [Fact]
    public async Task AddThenRemoveInTheSameWindowCancelsOut()
    {
        var (_, baseline, _) = await Drain();

        await AddMember("hr", "user:erin");
        await RemoveMember("hr", "user:erin");

        Assert.Empty((await Drain(baseline)).Groups);
    }

    [Fact]
    public async Task TokenLatestAndResetBehaveLikeDriveDelta()
    {
        var latest = await RawGet("app", $"{Url}?$deltatoken=latest");
        Assert.Empty(latest["value"]!.AsArray());
        var link = latest["@odata.deltaLink"]!.GetValue<string>();

        await AddMember("hr", "user:erin");
        Assert.Single((await Drain(link)).Groups);

        await Admin(HttpMethod.Post, "/_admin/reset");
        var gone = await RawGet("app", link, expected: HttpStatusCode.Gone);
        Assert.Equal("resyncRequired", gone["error"]!["code"]!.GetValue<string>());
    }

    [Fact]
    public async Task GroupsDeltaIsNotConfusedWithGroupById()
    {
        await RawGet("app", "/v1.0/groups/delta");
        await RawGet("app", $"/v1.0/groups/{Ids.Group("hr")}");
    }

    [Fact]
    public async Task SdkGroupsDeltaPagesWithOdataLinks()
    {
        var client = As("app");
        var page = await client.Groups.Delta.GetAsDeltaGetResponseAsync();
        var seen = new List<string>(page!.Value!.Select(g => g.Id!));
        while (page.OdataNextLink is { } next)
        {
            page = await client.Groups.Delta.WithUrl(next).GetAsDeltaGetResponseAsync();
            seen.AddRange(page!.Value!.Select(g => g.Id!));
        }

        Assert.Equal(6, seen.Distinct().Count());
        Assert.NotNull(page.OdataDeltaLink);
    }
}