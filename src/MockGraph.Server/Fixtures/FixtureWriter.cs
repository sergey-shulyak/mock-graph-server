using System.Text;

using MockGraph.Server.State;

using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.EventEmitters;
using YamlDotNet.Serialization.NamingConventions;

namespace MockGraph.Server.Fixtures;

/// <summary>
/// Dumps the live model in the fixture format (what GET /_admin/state returns). Text files use <c>content</c>;
/// anything else (binary, CR characters, odd whitespace) uses <c>contentBase64</c>, which the loader also accepts.
/// Timestamps and etag versions are runtime state and are not part of the fixture format.
/// </summary>
public static class FixtureWriter
{
    private static readonly ISerializer Serializer = new SerializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitDefaults)
        .WithEventEmitter(next => new LiteralMultilineEmitter(next), loc => loc.OnTop())
        .Build();

    public static string Dump(GraphModel model) => Serializer.Serialize(ToDto(model));

    public static FixtureDto ToDto(GraphModel model) => new()
    {
        Tenant = new TenantDto { Domain = model.TenantDomain },
        Users = [.. model.Users.Select(u => new UserDto { Id = u.Key, DisplayName = u.DisplayName, Upn = u.UserPrincipalName })],
        Groups = [.. model.Groups.Select(g => new GroupDto
        {
            Id = g.Key,
            DisplayName = g.DisplayName,
            Members = [.. g.Members.Select(m => m.ToString())],
        })],
        Sites = [.. model.Sites.Select(s => new SiteDto
        {
            Id = s.Key,
            DisplayName = s.DisplayName,
            Drives = [.. model.Drives.Where(d => d.SiteKey == s.Key).Select(d => new DriveDto
            {
                Id = d.Key,
                Name = d.Name,
                Items = [.. model.Items.Where(i => i.DriveKey == d.Key).Select(ToItem)],
            })],
        })],
    };

    private static ItemDto ToItem(ItemRecord item)
    {
        var dto = new ItemDto
        {
            Path = item.Path,
            BreakInheritance = item.BreakInheritance,
            Permissions = [.. item.Grants.Select(g => new PermissionDto { Principal = g.Principal.ToString(), Role = g.Role.ToString().ToLowerInvariant() })],
        };

        if (item.Content is { } bytes)
        {
            var text = TryText(bytes);
            dto.Content = text;
            dto.ContentBase64 = text is null ? Convert.ToBase64String(bytes) : null;
        }

        return dto;
    }

    private static string? TryText(byte[] bytes)
    {
        string text;
        try
        {
            text = new UTF8Encoding(false, true).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return null;
        }

        var safe = text.All(c => c == '\n' || (!char.IsControl(c) && c != '\u0085' && c != '\u2028' && c != '\u2029'))
            && !text.StartsWith(' ') && !text.StartsWith('\n') && !text.Contains(" \n") && !text.EndsWith(' ')
            && !text.Contains("\n\n\n");
        return safe && text.Length > 0 ? text : null;
    }

    private sealed class LiteralMultilineEmitter(IEventEmitter next) : ChainedEventEmitter(next)
    {
        public override void Emit(ScalarEventInfo eventInfo, IEmitter emitter)
        {
            if (eventInfo.Source.Value is string s && s.Contains('\n'))
            {
                eventInfo = new ScalarEventInfo(eventInfo.Source)
                {
                    Style = ScalarStyle.Literal,
                };
            }

            base.Emit(eventInfo, emitter);
        }
    }
}