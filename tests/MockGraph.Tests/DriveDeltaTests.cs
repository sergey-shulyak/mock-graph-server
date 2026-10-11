using System.Net;

using Microsoft.Graph.Models.ODataErrors;

using MockGraph.Server.State;

namespace MockGraph.Tests;

public sealed class DriveDeltaTests : IsolatedServerTest
{
    private const string Onboarding = "/Shared/contractor-onboarding.md";

    [Fact]
    public async Task InitialEnumerationPagesToDeltaLinkAndContainsEveryItemExactlyOnce()
    {
        var result = await Drain(As("app"));

        var model = Store.Model;
        var expected = model.Items.Select(i => i.Id).Append(model.RootOf(model.Drives[0]).Id).Order().ToArray();
        Assert.Equal(expected, result.Ids.Order().ToArray());
        Assert.Equal(expected.Length, result.Ids.Distinct().Count());
        Assert.True(result.Pages > 1, "page size 5 should force several pages");
        Assert.NotEmpty(result.DeltaLink);
        Assert.Equal(model.RootOf(model.Drives[0]).Id, result.Ids[0]);
    }

    [Fact]
    public async Task UserInitialDeltaContainsOnlyReadableItems()
    {
        var result = await Drain(As("user:erin"));

        Assert.Equal(new[] { IdOf(Onboarding), Store.Model.RootOf(Store.Model.Drives[0]).Id }.Order(), result.Ids.Order());
    }

    [Fact]
    public async Task NoChangesYieldsEmptyValueAndFreshDeltaLink()
    {
        var client = As("app");
        var first = await Drain(client);
        var second = await Drain(client, first.DeltaLink);

        Assert.Empty(second.Items);
        Assert.NotEmpty(second.DeltaLink);
    }

    [Fact]
    public async Task TokenLatestReturnsNoItemsAndOnlyChangesAfterwards()
    {
        var client = As("app");
        var latest = await client.Drives[DriveId].Items["root"].Delta.WithUrl($"{TestSupport.BaseUrl}/drives/{DriveId}/items/root/delta?token=latest").GetAsDeltaGetResponseAsync();
        Assert.Empty(latest!.Value!);
        Assert.NotNull(latest.OdataDeltaLink);
        Assert.Null(latest.OdataNextLink);

        await PutFile("/General/new.md", "hello");
        var changes = await Drain(client, latest.OdataDeltaLink);

        Assert.Equal([IdOf("/General/new.md")], changes.Ids);
    }

    [Fact]
    public async Task TokenCanBePassedExplicitlyAsQueryOrAsDeltaWithTokenFunction()
    {
        var client = As("app");
        var baseline = await Drain(client);
        var token = new Uri(baseline.DeltaLink).Query.TrimStart('?').Split('&').Single(p => p.StartsWith("token=")).Split('=')[1];
        await PutFile("/General/new.md", "hello");

        var viaQuery = await client.Drives[DriveId].Items["root"].Delta.WithUrl($"{TestSupport.BaseUrl}/drives/{DriveId}/items/root/delta?token={token}").GetAsDeltaGetResponseAsync();
        var viaFunction = await client.Drives[DriveId].Items["root"].DeltaWithToken(token).GetAsDeltaWithTokenGetResponseAsync();

        Assert.Equal([IdOf("/General/new.md")], viaQuery!.Value!.Select(i => i.Id));
        Assert.Equal([IdOf("/General/new.md")], viaFunction!.Value!.Select(i => i.Id));
    }

    [Fact]
    public async Task RootDeltaPathAndFolderScopedDeltaWork()
    {
        var root = await RawGet("app", $"/v1.0/drives/{DriveId}/root/delta");
        Assert.NotEmpty(root["value"]!.AsArray());

        var scoped = await RawDrain("app", $"/v1.0/drives/{DriveId}/items/{IdOf("/Support")}/delta");
        Assert.Equal(
            new[] { IdOf("/Support"), IdOf("/Support/escalation-runbook.md") }.Order(),
            scoped.Items.Select(i => i["id"]!.GetValue<string>()).Order());
    }

    [Fact]
    public async Task FileAddedAppearsAsUpsertWithParents()
    {
        var client = As("app");
        var baseline = await Drain(client);

        await PutFile("/Projects/Alpha/spec.md", "# Spec");
        var changes = await Drain(client, baseline.DeltaLink);

        Assert.Equal(
            new[] { IdOf("/Projects"), IdOf("/Projects/Alpha"), IdOf("/Projects/Alpha/spec.md") }.Order(),
            changes.Ids.Order());
        var file = changes.Items.Single(i => i.Id == IdOf("/Projects/Alpha/spec.md"));
        Assert.Equal("spec.md", file.Name);
        Assert.Null(file.Deleted);
    }

    [Fact]
    public async Task FileReplacedChangesETagCTagLastModifiedAndContent()
    {
        var client = As("app");
        var id = IdOf("/General/vacation-policy.md");
        var before = await client.Drives[DriveId].Items[id].GetAsync();
        var baseline = await Drain(client);

        await PutFile("/General/vacation-policy.md", "# Vacation\nNow 30 days.", HttpStatusCode.OK);
        var changes = await Drain(client, baseline.DeltaLink);

        var after = Assert.Single(changes.Items);
        Assert.Equal(id, after.Id);
        Assert.NotEqual(before!.ETag, after.ETag);
        Assert.NotEqual(before.CTag, after.CTag);
        Assert.True(after.LastModifiedDateTime > before.LastModifiedDateTime);
        Assert.Equal("# Vacation\nNow 30 days.".Length, after.Size);
        await using var content = await client.Drives[DriveId].Items[id].Content.GetAsync();
        Assert.Equal("# Vacation\nNow 30 days.", await new StreamReader(content!).ReadToEndAsync());
    }

    [Fact]
    public async Task FileDeletedAppearsWithDeletedFacet()
    {
        var client = As("app");
        var baseline = await Drain(client);

        await DeleteFile("/General/office-wifi.md");
        var changes = await Drain(client, baseline.DeltaLink);

        var gone = Assert.Single(changes.Items);
        Assert.Equal(IdOf("/General/office-wifi.md"), gone.Id);
        Assert.NotNull(gone.Deleted);
    }

    [Fact]
    public async Task FolderDeletedReportsEveryDescendant()
    {
        var client = As("app");
        var baseline = await Drain(client);

        await DeleteFile("/Engineering/Backend");
        var changes = await Drain(client, baseline.DeltaLink);

        Assert.Equal(
            new[] { IdOf("/Engineering/Backend"), IdOf("/Engineering/Backend/db-migrations.md"), IdOf("/Engineering/Backend/prod-access.md") }.Order(),
            changes.Ids.Order());
        Assert.All(changes.Items, i => Assert.NotNull(i.Deleted));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PermissionGrantedAndRevokedAreReportedAndAnnotatedOnlyWithThePreferHeader(bool withHeader)
    {
        var client = As("app");
        var prefer = withHeader ? SharingPrefer : null;
        var baseline = await Drain(client);

        await Grant("/HR/salary-bands.md", "user:dave");
        var granted = await Drain(client, baseline.DeltaLink, prefer);
        await Revoke("/HR/salary-bands.md", "user:dave");
        var revoked = await Drain(client, granted.DeltaLink, prefer);

        foreach (var step in new[] { granted, revoked })
        {
            var item = Assert.Single(step.Items);
            Assert.Equal(IdOf("/HR/salary-bands.md"), item.Id);
            Assert.Equal(withHeader, IsShared(item));
        }
    }

    [Fact]
    public async Task WithoutPreferHeaderSharingChangeIsPresentOnTheWireWithoutAnnotation()
    {
        var baseline = await RawDrain("app", $"/v1.0/drives/{DriveId}/root/delta");
        await Revoke(Onboarding, "user:erin");

        var (items, _) = await RawDrain("app", baseline.DeltaLink);

        var item = Assert.Single(items);
        Assert.Equal(IdOf(Onboarding), item["id"]!.GetValue<string>());
        Assert.Null(item["@microsoft.graph.sharedChanged"]);
        Assert.Null(item["deleted"]);
    }

    [Fact]
    public async Task SelectAppliesToDriveDeltaAndKeepsIdAnnotationsAndDeletedFacet()
    {
        var baseline = await RawDrain("app", $"/v1.0/drives/{DriveId}/root/delta");
        await Revoke(Onboarding, "user:erin");
        await DeleteFile("/HR/salary-bands.md");

        var (items, _) = await RawDrain("app", baseline.DeltaLink.Replace("/delta?", "/delta?$select=name&"), SharingPrefer);

        var shared = ById(items, Onboarding)!;
        Assert.Equal("contractor-onboarding.md", shared["name"]!.GetValue<string>());
        Assert.Null(shared["size"]);
        Assert.Null(shared["file"]);
        Assert.Equal("True", shared["@microsoft.graph.sharedChanged"]!.GetValue<string>());
        Assert.NotNull(ById(items, "/HR/salary-bands.md")!["deleted"]);

        var first = await RawGet("app", $"/v1.0/drives/{DriveId}/root/delta?$select=name&$top=2");
        Assert.All(first["value"]!.AsArray(), n => Assert.Null(n!["size"]));
        Assert.Contains("$select=name", first["@odata.nextLink"]!.GetValue<string>());
    }

    [Fact]
    public async Task GrantOnFolderMarksFolderAndInheritingDescendantsButNotBrokenInheritance()
    {
        var baseline = await RawDrain("app", $"/v1.0/drives/{DriveId}/root/delta");

        await Grant("/Engineering", "user:carol");
        var (items, _) = await RawDrain("app", baseline.DeltaLink, SharingPrefer);

        Assert.Equal(
            new[] { IdOf("/Engineering"), IdOf("/Engineering/local-dev-setup.md") }.Order(),
            items.Select(i => i["id"]!.GetValue<string>()).Order());
        Assert.All(items, i => Assert.Equal("True", i["@microsoft.graph.sharedChanged"]!.GetValue<string>()));
    }

    [Fact]
    public async Task ErinRevocationScenario()
    {
        var app = As("app");
        var erin = As("user:erin");
        var appBaseline = await Drain(app);
        var erinBaseline = await Drain(erin);

        await Revoke(Onboarding, "user:erin");

        var appChanges = await Drain(app, appBaseline.DeltaLink, SharingPrefer);
        var sharedItem = Assert.Single(appChanges.Items);
        Assert.Equal(IdOf(Onboarding), sharedItem.Id);
        Assert.True(IsShared(sharedItem));
        Assert.Null(sharedItem.Deleted);

        var erinChanges = await Drain(erin, erinBaseline.DeltaLink, SharingPrefer);
        var deleted = Assert.Single(erinChanges.Items);
        Assert.Equal(IdOf(Onboarding), deleted.Id);
        Assert.NotNull(deleted.Deleted);
        Assert.False(IsShared(deleted));

        Assert.Empty(await OpenablePaths(erin, Onboarding));
        var erinTree = await erin.Drives[DriveId].Items["root"].Children.GetAsync();
        Assert.Empty(erinTree!.Value!);
    }

    [Fact]
    public async Task GrantToUserMakesItemAppearAsUpsertInTheirDelta()
    {
        var carol = As("user:carol");
        var baseline = await Drain(carol);
        Assert.DoesNotContain(IdOf(Onboarding), baseline.Ids);

        await Grant(Onboarding, "user:carol");
        var changes = await Drain(carol, baseline.DeltaLink);

        var item = Assert.Single(changes.Items);
        Assert.Equal(IdOf(Onboarding), item.Id);
        Assert.Null(item.Deleted);
    }

    [Fact]
    public async Task RemovingAliceFromBackendGroupHidesBackendDocsFromHerAndFromHerDelta()
    {
        var alice = As("user:alice");
        var baseline = await Drain(alice);
        Assert.Contains(IdOf("/Engineering/Backend/db-migrations.md"), baseline.Ids);

        await RemoveMember("eng-backend", "user:alice");

        var changes = await Drain(alice, baseline.DeltaLink);
        var deletedIds = changes.Items.Where(i => i.Deleted is not null).Select(i => i.Id).ToArray();
        Assert.Contains(IdOf("/Engineering/Backend"), deletedIds);
        Assert.Contains(IdOf("/Engineering/Backend/db-migrations.md"), deletedIds);
        Assert.DoesNotContain(IdOf("/Engineering/Backend/prod-access.md"), deletedIds); // alice has a direct grant

        Assert.Equal(
            ["/Engineering/Backend/prod-access.md"],
            await OpenablePaths(alice, "/Engineering/Backend", "/Engineering/Backend/db-migrations.md", "/Engineering/Backend/prod-access.md"));

        await AddMember("eng-backend", "user:alice");
        var restored = await Drain(alice, changes.DeltaLink);
        Assert.Contains(IdOf("/Engineering/Backend/db-migrations.md"), restored.Items.Where(i => i.Deleted is null).Select(i => i.Id));
    }

    [Fact]
    public async Task TokenFromBeforeResetGivesGoneResyncRequired()
    {
        var client = As("app");
        var baseline = await Drain(client);

        await Admin(HttpMethod.Post, "/_admin/reset");

        var error = await Assert.ThrowsAsync<ODataError>(() => Drain(client, baseline.DeltaLink));
        Assert.Equal(410, error.ResponseStatusCode);
        Assert.Equal("resyncRequired", error.Error!.Code);
    }

    [Fact]
    public async Task ResetRestoresFixtureAndStartsFreshEnumeration()
    {
        await DeleteFile("/HR");
        await Admin(HttpMethod.Post, "/_admin/reset");

        var result = await Drain(As("app"));
        Assert.Contains(IdOf("/HR/salary-bands.md"), result.Ids);
        Assert.Equal(0, Store.Sequence);
    }

    [Fact]
    public async Task MalformedTokenAndUnsupportedOptionsAreBadRequests()
    {
        await RawGet("app", $"/v1.0/drives/{DriveId}/root/delta?token=garbage", expected: HttpStatusCode.BadRequest);
        await RawGet("app", $"/v1.0/drives/{DriveId}/root/delta?$filter=x", expected: HttpStatusCode.BadRequest);
        await RawGet("app", $"/v1.0/drives/nope/root/delta", expected: HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task WireFormatForSharedChangedAndDeletedFacet()
    {
        var baseline = await RawDrain("app", $"/v1.0/drives/{DriveId}/root/delta");
        Assert.Contains("token=", baseline.DeltaLink);

        await Revoke(Onboarding, "user:erin");
        await DeleteFile("/HR/salary-bands.md");
        var (items, next) = await RawDrain("app", baseline.DeltaLink, "deltashowremovedasdeleted, deltatraversepermissiongaps, deltashowsharingchanges");

        var shared = ById(items, Onboarding)!;
        Assert.Equal("True", shared["@microsoft.graph.sharedChanged"]!.GetValue<string>()); // string "True", not a bool
        Assert.Null(shared["deleted"]);

        var deleted = ById(items, "/HR/salary-bands.md")!;
        Assert.NotNull(deleted["deleted"]);
        Assert.IsType<System.Text.Json.Nodes.JsonObject>(deleted["deleted"]);
        Assert.Null(deleted["@microsoft.graph.sharedChanged"]);

        var (plain, _) = await RawDrain("app", baseline.DeltaLink);
        Assert.Null(ById(plain, Onboarding)!["@microsoft.graph.sharedChanged"]); // header not sent: present, not annotated
        Assert.NotNull(ById(plain, "/HR/salary-bands.md")!["deleted"]);
        Assert.NotEmpty(next);
    }
}