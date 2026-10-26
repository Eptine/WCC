namespace Wcc.Core.Projects;

public sealed class WccProjectExportResult
{
    public string RootDirectory { get; init; } = "";
    public List<string> WrittenFiles { get; } = new();
}