namespace MockGraph.Server.Fixtures;

public sealed class FixtureDto
{
    public TenantDto Tenant { get; set; } = new();

    public List<UserDto> Users { get; set; } = [];

    public List<GroupDto> Groups { get; set; } = [];

    public List<SiteDto> Sites { get; set; } = [];
}

public sealed class TenantDto
{
    public string Domain { get; set; } = "contoso-mock.onmicrosoft.com";
}

public sealed class UserDto
{
    public string Id { get; set; } = "";

    public string DisplayName { get; set; } = "";

    public string Upn { get; set; } = "";
}

public sealed class GroupDto
{
    public string Id { get; set; } = "";

    public string DisplayName { get; set; } = "";

    public List<string> Members { get; set; } = [];
}

public sealed class SiteDto
{
    public string Id { get; set; } = "";

    public string DisplayName { get; set; } = "";

    public List<DriveDto> Drives { get; set; } = [];
}

public sealed class DriveDto
{
    public string Id { get; set; } = "";

    public string Name { get; set; } = "";

    public List<ItemDto> Items { get; set; } = [];
}

public sealed class ItemDto
{
    public string Path { get; set; } = "";

    public bool BreakInheritance { get; set; }

    public List<PermissionDto> Permissions { get; set; } = [];

    public string? Content { get; set; }

    public string? Source { get; set; }

    /// <summary>Binary content inline as base64 (emitted by GET /_admin/state for non-text files).</summary>
    public string? ContentBase64 { get; set; }
}

public sealed class PermissionDto
{
    public string Principal { get; set; } = "";

    public string Role { get; set; } = "read";
}