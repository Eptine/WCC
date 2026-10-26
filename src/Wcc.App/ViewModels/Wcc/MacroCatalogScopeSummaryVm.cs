using Wcc.Core.Macros;

namespace Wcc.App.ViewModels.Wcc;

public sealed class MacroCatalogScopeSummaryVm : ViewModelBase
{
    public MacroCatalogScopeSummaryVm(WccMacroRegistryEntry entry)
    {
        Entry = entry;
    }

    public WccMacroRegistryEntry Entry { get; }

    public string ScopeLabel => Entry.ScopeLabel;
    public int DefinitionCount => Entry.DefinitionCount;
    public int InvocationCount => Entry.InvocationCount;
    public int TemplatedPathUsageCount => Entry.TemplatedPathUsageCount;

    public string DisplayName =>
        $"{Entry.ScopeLabel} defs:{Entry.DefinitionCount} uses:{Entry.InvocationCount} paths:{Entry.TemplatedPathUsageCount}";
}