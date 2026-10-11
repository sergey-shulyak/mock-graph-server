using Microsoft.Graph.Models;
using Microsoft.Graph.Models.ODataErrors;
using Microsoft.Kiota.Abstractions;

namespace MockGraph.ContractTests;

[Collection(ContractCollection.Name)]
public sealed class DirectoryContractTests(ContractTarget target)
{
    private const int MaxIterated = 60;

    [SkippableFact]
    public async Task UsersListHasIdentityProperties()
    {
        var page = await target.Client.Users.GetAsync();
        Skip.If(page?.Value is not { Count: > 0 }, "Tenant has no users.");
        foreach (var user in page!.Value!)
        {
            Assert.False(string.IsNullOrEmpty(user.Id));
            // Loosened: displayName may be null on exotic real accounts; UPN is required on every user.
            Assert.False(string.IsNullOrEmpty(user.UserPrincipalName));
        }
    }

    [SkippableFact]
    public async Task UserByIdAndByUpnReturnTheSameUser()
    {
        Skip.If(target.UserId is null, "No user available.");
        var byId = await target.Client.Users[target.UserId!].GetAsync();
        Assert.NotNull(byId);
        Assert.Equal(target.UserId, byId!.Id, ignoreCase: true);
        Assert.False(string.IsNullOrEmpty(byId.UserPrincipalName));

        var byUpn = await target.Client.Users[byId.UserPrincipalName!].GetAsync();
        Assert.Equal(byId.Id, byUpn!.Id, ignoreCase: true);
    }

    [SkippableFact]
    public async Task UserSelectReturnsOnlySelectedPropertiesPlusId()
    {
        Skip.If(target.UserId is null, "No user available.");
        var (status, body) = await target.RawGetAsync("/users?$top=1&$select=displayName,userPrincipalName");
        Assert.Equal(200, status);
        ContractAssert.OnlyProperties(body!["value"]![0]!.AsObject(), "id", "displayName", "userPrincipalName");
    }

    [SkippableFact]
    public async Task UsersHonourTopAndNextLinkIsFollowable()
    {
        var first = await target.Client.Users.GetAsync(r => r.QueryParameters.Top = 1);
        Skip.If(first?.OdataNextLink is null, "Tenant has fewer than 2 users, cannot test paging.");
        Assert.Single(first!.Value!); // Loosened to "at most $top" would also be legal Graph; 1 is what both servers return.
        await PagingAssert.FollowAsync<User, UserCollectionResponse>(target, first, MaxIterated, u => u.Id);
    }

    [SkippableFact]
    public async Task GroupByIdHasIdentityProperties()
    {
        Skip.If(target.GroupId is null, "No group available.");
        var group = await target.Client.Groups[target.GroupId!].GetAsync();
        Assert.NotNull(group);
        Assert.Equal(target.GroupId, group!.Id, ignoreCase: true);
        Assert.False(string.IsNullOrEmpty(group.DisplayName));
    }

    [SkippableFact]
    public async Task GroupSelectReturnsOnlySelectedPropertiesPlusId()
    {
        Skip.If(target.GroupId is null, "No group available.");
        var (status, body) = await target.RawGetAsync("/groups?$top=1&$select=displayName");
        Assert.Equal(200, status);
        ContractAssert.OnlyProperties(body!["value"]![0]!.AsObject(), "id", "displayName");
    }

    [SkippableFact]
    public async Task GroupsHonourTopAndNextLinkIsFollowable()
    {
        var first = await target.Client.Groups.GetAsync(r => r.QueryParameters.Top = 1);
        Skip.If(first?.OdataNextLink is null, "Tenant has fewer than 2 groups, cannot test paging.");
        Assert.Single(first!.Value!);
        await PagingAssert.FollowAsync<Group, GroupCollectionResponse>(target, first, MaxIterated, g => g.Id);
    }

    [SkippableFact]
    public async Task GroupMembersAreTypedDirectoryObjects()
    {
        var groups = await target.Client.Groups.GetAsync(r => r.QueryParameters.Top = 20);
        foreach (var group in groups?.Value ?? [])
        {
            var members = await target.Client.Groups[group.Id!].Members.GetAsync();
            if (members?.Value is not { Count: > 0 })
            {
                continue;
            }

            foreach (var member in members.Value)
            {
                AssertTypedDirectoryObject(member);
            }

            return;
        }

        Skip.If(true, "No group with members among the first 20 groups.");
    }

    [SkippableFact]
    public async Task TransitiveMembersAreTypedDirectoryObjects()
    {
        var groups = await target.Client.Groups.GetAsync(r => r.QueryParameters.Top = 20);
        foreach (var group in groups?.Value ?? [])
        {
            var members = await target.Client.Groups[group.Id!].TransitiveMembers.GetAsync();
            if (members?.Value is not { Count: > 0 })
            {
                continue;
            }

            foreach (var member in members.Value)
            {
                AssertTypedDirectoryObject(member);
            }

            return;
        }

        Skip.If(true, "No group with members among the first 20 groups.");
    }

    [SkippableFact]
    public async Task UserMemberOfReturnsTypedGroups()
    {
        var users = await target.Client.Users.GetAsync(r => r.QueryParameters.Top = 20);
        foreach (var user in users?.Value ?? [])
        {
            var memberOf = await target.Client.Users[user.Id!].MemberOf.GetAsync();
            if (memberOf?.Value is not { Count: > 0 })
            {
                continue;
            }

            foreach (var entry in memberOf.Value)
            {
                AssertTypedDirectoryObject(entry);
            }

            // Loosened: real tenants may also return directoryRoles / administrativeUnits here, so only require that groups, when present, deserialize as Group.
            Assert.All(memberOf.Value.Where(e => e.OdataType == "#microsoft.graph.group"), e => Assert.IsType<Group>(e));
            return;
        }

        Skip.If(true, "No user with memberOf entries among the first 20 users.");
    }

    [SkippableFact]
    public async Task UnknownUserIsResourceNotFound()
    {
        var ex = await Assert.ThrowsAsync<ODataError>(() => target.Client.Users[Guid.NewGuid().ToString()].GetAsync());
        ContractAssert.GraphError(ex, 404, "Request_ResourceNotFound");
    }

    [SkippableFact]
    public async Task UnknownGroupIsResourceNotFound()
    {
        var ex = await Assert.ThrowsAsync<ODataError>(() => target.Client.Groups[Guid.NewGuid().ToString()].GetAsync());
        ContractAssert.GraphError(ex, 404, "Request_ResourceNotFound");
    }

    [SkippableFact]
    public async Task GroupsDeltaEnumeratesToADeltaLink()
    {
        var page = await target.Client.Groups.Delta.GetAsDeltaGetResponseAsync();
        for (var i = 0; i < 500 && page?.OdataNextLink is { } next; i++)
        {
            Assert.All(page.Value!, g => Assert.False(string.IsNullOrEmpty(g.Id)));
            page = await target.Client.Groups.Delta.WithUrl(next).GetAsDeltaGetResponseAsync();
        }

        Assert.NotNull(page);
        Assert.Null(page!.OdataNextLink);
        Assert.False(string.IsNullOrEmpty(page.OdataDeltaLink));
        Assert.All(page.Value ?? [], g => Assert.False(string.IsNullOrEmpty(g.Id)));
    }

    [SkippableFact]
    public async Task GroupsDeltaMembersDeltaIsATypedArrayWhenPresent()
    {
        var (status, body) = await target.RawGetAsync("/groups/delta");
        Assert.Equal(200, status);
        var withMembers = body!["value"]!.AsArray().Select(n => n!.AsObject()).FirstOrDefault(g => g.ContainsKey("members@delta"));
        // Loosened: real Graph only emits members@delta for groups that have members/changes in this page.
        Skip.If(withMembers is null, "No group with members@delta on the first page.");
        foreach (var member in withMembers!["members@delta"]!.AsArray())
        {
            Assert.False(string.IsNullOrEmpty(member!["id"]?.GetValue<string>()));
        }
    }

    private static void AssertTypedDirectoryObject(DirectoryObject entry)
    {
        Assert.False(string.IsNullOrEmpty(entry.Id));
        Assert.False(string.IsNullOrEmpty(entry.OdataType));
        Assert.StartsWith("#microsoft.graph.", entry.OdataType);
        // The point of @odata.type: the SDK picks the derived model instead of a bare DirectoryObject.
        // Loosened: other directoryObject kinds (device, servicePrincipal, orgContact) are legal in a real tenant.
        switch (entry.OdataType)
        {
            case "#microsoft.graph.user":
                Assert.IsType<User>(entry);
                break;
            case "#microsoft.graph.group":
                Assert.IsType<Group>(entry);
                break;
        }
    }
}