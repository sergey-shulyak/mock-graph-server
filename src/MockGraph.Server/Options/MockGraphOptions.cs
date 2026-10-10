namespace MockGraph.Server.Options;

public sealed class MockGraphOptions
{
    public const string SectionName = "MockGraph";

    public string FixturePath { get; set; } = "fixtures/sample.yaml";

    public int DefaultPageSize { get; set; } = 5;

    public int MaxPageSize { get; set; } = 999;
}