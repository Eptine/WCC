using Wcc.Core.Scenarios;
using Wcc.Core.Wml;

namespace Wcc.Core.Projects;

public sealed class ScenarioFileAsset
{
    public string InternalId { get; init; } = global::Wcc.Core.WccInternalId.New("scenariofile");

    public required string FileName { get; init; }

    /// <summary>
    /// Optional original source path of the imported file.
    /// Useful for project/workspace bookkeeping; not used for export path decisions.
    /// </summary>
    public string? SourcePath { get; init; }

    /// <summary>
    /// Exact text loaded from the project file. Used to detect external changes
    /// before an in-place WorkingCopy save.
    /// </summary>
    public string? RawTextSnapshot { get; set; }

    public List<ScenarioDocument> Documents { get; } = new();
    public List<WmlMacroDefinition> MacroDefinitions { get; } = new();
}
