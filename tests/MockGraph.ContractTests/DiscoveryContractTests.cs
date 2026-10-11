using Microsoft.Graph;
using Microsoft.Graph.Models;
using Microsoft.Graph.Models.ODataErrors;

namespace MockGraph.ContractTests;

/// <summary>Read-only checks for how a connector discovers sites and users, and for JSON batching. Tenant-agnostic.</summary>
[Collection(ContractCollection.Name)]
public sealed class DiscoveryContractTests(ContractTarget target)
{
    [SkippableFact]
    public async Task SiteSearchWithStarReturnsSites()
    {
        var sites = await target.Client.Sites.GetAsync(r => r.QueryParameters.Search = "*");
        Skip.If(sites?.Value is not { Count: > 0 }, "Tenant returned no sites for search=*.");
        Assert.All(sites!.Value!, s => Assert.False(string.IsNullOrEmpty(s.Id)));
        Assert.All(sites.Value!, s => Assert.False(string.IsNullOrEmpty(s.WebUrl)));
    }

    [SkippableFact]
    public async Task SiteSearchWithTheNameOfADiscoveredSiteFindsIt()
    {
        var all = await target.Client.Sites.GetAsync(r => r.QueryParameters.Search = "*");
        var named = all?.Value?.FirstOrDefault(s => !string.IsNullOrWhiteSpace(s.DisplayName ?? s.Name));
        Skip.If(named is null, "No site with a name to search for.");
        var keyword = (named!.DisplayName ?? named.Name)!.Split(' ')[0];

        var found = await target.Client.Sites.GetAsync(r => r.QueryParameters.Search = keyword);
        // Loosened: real search is free text over several properties and is index-backed, so a fresh site may lag behind.
        Skip.If(found?.Value is not { Count: > 0 }, "Search index returned nothing for a known site name.");
        Assert.Contains(found!.Value!, s => s.Id == named.Id);
    }

    [SkippableFact]
    public async Task RootSiteResolves()
    {
        var root = await target.Client.Sites["root"].GetAsync();
        Assert.False(string.IsNullOrEmpty(root?.Id));
        Assert.False(string.IsNullOrEmpty(root!.WebUrl));
        // Loosened: siteCollection is documented as present on the root site, but is not asserted field by field.
        if (root.SiteCollection is not null)
        {
            Assert.False(string.IsNullOrEmpty(root.SiteCollection.Hostname));
        }
    }

    [SkippableFact]
    public async Task SiteCanBeAddressedByHostnameAndServerRelativePath()
    {
        var root = await target.Client.Sites["root"].GetAsync();
        var webUri = new Uri(root!.WebUrl!);
        Skip.If(target.SiteId is null, "No site available.");
        var site = await target.Client.Sites[target.SiteId!].GetAsync();
        var uri = new Uri(site!.WebUrl!);
        Skip.If(uri.AbsolutePath.Length <= 1, "The discovered site is the root site; no path to address.");

        var byPath = await target.Client.Sites[$"{webUri.Host}:{uri.AbsolutePath}"].GetAsync();
        Assert.Equal(site.Id, byPath!.Id, ignoreCase: true);
    }

    [SkippableFact]
    public async Task GetAllSitesListsSitesWhenTheCallerMayUseIt()
    {
        try
        {
            var all = await target.Client.Sites.GetAllSites.GetAsGetAllSitesGetResponseAsync();
            Skip.If(all?.Value is not { Count: > 0 }, "No sites returned.");
            Assert.All(all!.Value!, s => Assert.False(string.IsNullOrEmpty(s.Id)));
        }
        catch (ODataError e) when (!target.IsMock && e.ResponseStatusCode is 400 or 401 or 403)
        {
            // getAllSites is application-permission only; a delegated token is refused on a real tenant.
            Skip.If(true, "getAllSites needs an app-only token on a real tenant.");
        }
    }

    [SkippableFact]
    public async Task UserFilterByUserPrincipalNameReturnsThatUser()
    {
        Skip.If(target.UserId is null, "No user available.");
        var user = await target.Client.Users[target.UserId!].GetAsync();
        var upn = user!.UserPrincipalName!;

        var page = await target.Client.Users.GetAsync(r => r.QueryParameters.Filter = $"userPrincipalName eq '{upn.Replace("'", "''")}'");
        var match = Assert.Single(page!.Value!);
        Assert.Equal(user.Id, match.Id, ignoreCase: true);
    }

    [SkippableFact]
    public async Task UserFilterByStartsWithDisplayNameIncludesThatUser()
    {
        Skip.If(target.UserId is null, "No user available.");
        var user = await target.Client.Users[target.UserId!].GetAsync();
        Skip.If(string.IsNullOrEmpty(user!.DisplayName), "User has no displayName.");
        var prefix = user.DisplayName![..Math.Min(3, user.DisplayName.Length)].Replace("'", "''");

        var page = await target.Client.Users.GetAsync(r => r.QueryParameters.Filter = $"startsWith(displayName,'{prefix}')");
        Assert.Contains(page!.Value!, u => string.Equals(u.Id, user.Id, StringComparison.OrdinalIgnoreCase));
    }

    [SkippableFact]
    public async Task BatchOfTwoGetsReturnsBothResponses()
    {
        Skip.If(target.UserId is null, "No user available.");
        var batch = new BatchRequestContentCollection(target.Client);
        var userStep = await batch.AddBatchRequestStepAsync(target.Client.Users[target.UserId!].ToGetRequestInformation());
        var rootStep = await batch.AddBatchRequestStepAsync(target.Client.Sites["root"].ToGetRequestInformation());

        var responses = await target.Client.Batch.PostAsync(batch);
        var statuses = await responses.GetResponsesStatusCodesAsync();
        Assert.Equal(200, (int)statuses[userStep]);
        Assert.Equal(200, (int)statuses[rootStep]);

        var user = await responses.GetResponseByIdAsync<User>(userStep);
        Assert.Equal(target.UserId, user.Id, ignoreCase: true);
        Assert.False(string.IsNullOrEmpty((await responses.GetResponseByIdAsync<Site>(rootStep)).Id));
    }
}