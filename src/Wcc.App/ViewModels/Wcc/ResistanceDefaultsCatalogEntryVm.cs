using Wcc.Core.Presets;
using Wcc.Core.Projects;

namespace Wcc.App.ViewModels.Wcc;

public sealed class ResistanceDefaultsCatalogEntryVm : ViewModelBase
{
    public ResistanceDefaultsCatalogEntryVm(ResistanceDefaultsCatalogEntry entry)
    {
        Entry = entry;
    }

    public ResistanceDefaultsCatalogEntry Entry { get; }

    public string DisplayName => $"resistance_defaults #{Entry.OrdinalInFile}";
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