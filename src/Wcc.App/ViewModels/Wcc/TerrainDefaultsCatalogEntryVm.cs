using Wcc.Core.Presets;
using Wcc.Core.Projects;

namespace Wcc.App.ViewModels.Wcc;

public sealed class TerrainDefaultsCatalogEntryVm : ViewModelBase
{
    public TerrainDefaultsCatalogEntryVm(TerrainDefaultsCatalogEntry entry)
    {
        Entry = entry;
    }

    public TerrainDefaultsCatalogEntry Entry { get; }

    public string DisplayName => $"terrain_defaults #{Entry.OrdinalInFile}";
    public string ChildTagSummary => string.Join(", ", Entry.DirectChildTagNames);
    public string? SourcePath => Entry.SourcePath;
    public string SourceFileName => Entry.SourceFileName;

    public string OriginLabel => Entry.Origin switch
    {
        WccSourceOrigin.CoreInstall => "core",
        WccSourceOrigin.WccGenerated => "wcc",
        _ => "imported"
    };
}