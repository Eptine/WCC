using Wcc.Core.Macros;

namespace Wcc.App.ViewModels.Wcc;

public sealed class MacroCatalogEntryVm : ViewModelBase
{
    public MacroCatalogEntryVm(WccMacroCatalogEntry entry)
    {
        Entry = entry;
    }

    public WccMacroCatalogEntry Entry { get; }

    public string Name => Entry.Name;
    public string PurposeLabel => Entry.PurposeLabel;
    public string PurposeConfidenceLabel => Entry.PurposeConfidenceLabel;
    public string PurposeEvidenceSummary => Entry.PurposeEvidenceSummary;
    public string ScopeSummary => Entry.ScopeSummary;
    public int DefinitionCount => Entry.TotalDefinitionCount;
    public int InvocationCount => Entry.TotalInvocationCount;
    public int TemplatedPathUsageCount => Entry.TotalTemplatedPathUsageCount;
    public bool IsPickerEligible => Entry.IsPickerEligible;
    public string? PreferredDefinitionScopeLabel => Entry.PreferredDefinitionScopeLabel;

    public string DisplayName =>
        $"{Entry.Name} [{Entry.PurposeLabel}/{Entry.PurposeConfidenceLabel}] defs:{Entry.TotalDefinitionCount} uses:{Entry.TotalInvocationCount} paths:{Entry.TotalTemplatedPathUsageCount}";
}