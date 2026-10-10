using Microsoft.Graph.Models;

using MockGraph.Server.State;

namespace MockGraph.Server.Mapping;

public static class DirectoryMapper
{
    private static readonly DateTimeOffset CreatedAt = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public static User ToUser(UserRecord user, bool withType = false)
    {
        var (given, _, surname) = user.DisplayName.Partition(' ');
        return new User
        {
            Id = user.Id,
            OdataType = withType ? "#microsoft.graph.user" : null,
            DisplayName = user.DisplayName,
            GivenName = given,
            Surname = surname.Length == 0 ? null : surname,
            UserPrincipalName = user.UserPrincipalName,
            Mail = user.UserPrincipalName,
            BusinessPhones = [],
        };
    }

    public static Group ToGroup(GroupRecord group, bool withType = false) => new()
    {
        Id = group.Id,
        OdataType = withType ? "#microsoft.graph.group" : null,
        DisplayName = group.DisplayName,
        CreatedDateTime = CreatedAt,
        GroupTypes = [],
        MailEnabled = false,
        MailNickname = group.Key,
        SecurityEnabled = true,
    };

    public static DirectoryObject ToDirectoryObject(DirectoryEntry entry) => entry switch
    {
        UserRecord user => ToUser(user, withType: true),
        GroupRecord group => ToGroup(group, withType: true),
        _ => throw new ArgumentOutOfRangeException(nameof(entry)),
    };
}