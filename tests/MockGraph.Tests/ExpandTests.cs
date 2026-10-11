using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Graph;
using Microsoft.Graph.Models.ODataErrors;

using MockGraph.Server.State;

namespace MockGraph.Tests;

public sealed class ExpandTests(MockGraphFactory factory) : IClassFixture<MockGraphFactory>
{
    private static readonly string DriveId = Ids.Drive("kb-docs");

    private GraphServiceClient As(string token) => factory.Client(token);

    private static string IdOf(string path) => Ids.Item(DriveId, path);

    private static string[] Names(Microsoft.Graph.Models.DriveItem item) => [.. (item.Children ?? []).Select(c => c.Name!).Order()];

    [Fact]
    public async Task ExpandChildren_OnAnItem_ReturnsChildren()
    {
        var item = await As("user:alice").Drives[DriveId].Items[IdOf("/Engineering")].GetAsync(r => r.QueryParameters.Expand = ["children"]);
        Assert.Equal(["Backend", "local-dev-setup.md"], Names(item!));
        Assert.All(item!.Children!, c => Assert.NotNull(c.ParentReference));
    }

    [Fact]
    public async Task ExpandedChildren_RespectVisibility()
    {
        var bob = await As("user:bob").Drives[DriveId].Items[IdOf("/Engineering")].GetAsync(r => r.QueryParameters.Expand = ["children"]);
        Assert.Equal(["local-dev-setup.md"], Names(bob!));
        Assert.Equal(1, bob!.Folder!.ChildCount);
    }

    [Fact]
    public async Task ExpandChildren_OnTheRootAndByPath()
    {
        var root = await As("user:dave").Drives[DriveId].Root.GetAsync(r => r.QueryParameters.Expand = ["children"]);
        Assert.Equal(["General", "Support"], Names(root!));

        var byPath = await As("app").Drives[DriveId].Root.ItemWithPath("/General").GetAsync(r => r.QueryParameters.Expand = ["children"]);
        Assert.Equal(["floor-plan.png", "office-wifi.md", "vacation-policy.md"], Names(byPath!));
    }

    [Fact]
    public async Task ExpandChildren_OnAFile_AddsNothing()
    {
        var file = await As("app").Drives[DriveId].Items[IdOf("/General/office-wifi.md")].GetAsync(r => r.QueryParameters.Expand = ["children"]);
        Assert.Null(file!.Children);
        Assert.NotNull(file.File);
    }

    [Fact]
    public async Task ExpandChildren_WithSelect_StillReturnsTheChildren()
    {
        var response = await RawGraph.Get(
            factory.CreateClient(), "app", $"/v1.0/drives/{DriveId}/items/{IdOf("/General")}?$select=id,name&$expand=children");
        Assert.Equal(200, response.Status);
        Assert.Equal(3, response.Body!["children"]!.AsArray().Count);
        Assert.Null(response.Body["size"]);
    }

    [Theory]
    [InlineData("permissions", "can't be expanded")] // Graph documents that driveItem permissions cannot be expanded
    [InlineData("thumbnails", "not supported")]
    [InlineData("children,permissions", "can't be expanded")]
    public async Task OtherExpandValues_Are400(string expand, string message)
    {
        var ex = await Assert.ThrowsAsync<ODataError>(() =>
            As("app").Drives[DriveId].Items[IdOf("/General")].GetAsync(r => r.QueryParameters.Expand = [.. expand.Split(',')]));
        Assert.Equal(400, ex.ResponseStatusCode);
        Assert.Equal("BadRequest", ex.Error!.Code);
        Assert.Contains(message, ex.Error.Message);
    }

    [Fact]
    public async Task ExpandOnChildrenListing_IsRejected()
    {
        var ex = await Assert.ThrowsAsync<ODataError>(() =>
            As("app").Drives[DriveId].Items[IdOf("/General")].Children.GetAsync(r => r.QueryParameters.Expand = ["children"]));
        Assert.Equal(400, ex.ResponseStatusCode);
    }

    [Fact]
    public async Task ExpandOnAnInvisibleItem_StaysA404()
    {
        var ex = await Assert.ThrowsAsync<ODataError>(() =>
            As("user:erin").Drives[DriveId].Items[IdOf("/HR")].GetAsync(r => r.QueryParameters.Expand = ["children"]));
        Assert.Equal(404, ex.ResponseStatusCode);
    }
}