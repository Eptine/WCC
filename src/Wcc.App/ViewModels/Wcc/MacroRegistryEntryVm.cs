using Wcc.Core.Macros;

namespace Wcc.App.ViewModels.Wcc;

public sealed class MacroRegistryEntryVm : ViewModelBase
{
    public MacroRegistryEntryVm(WccMacroRegistryEntry entry)
    {
        Entry = entry;
    }

    public WccMacroRegistryEntry Entry { get; }

    public string Name => Entry.Name;
    public string ScopeLabel => Entry.ScopeLabel;
    public int DefinitionCount => Entry.DefinitionCount;
    public int InvocationCount => Entry.InvocationCount;
    public int TemplatedPathUsageCount => Entry.TemplatedPathUsageCount;
    public bool IsTemplatedPathMacro => Entry.IsTemplatedPathMacro;

    public string DisplayName =>
        $"{Entry.Name} [{Entry.ScopeLabel}] defs:{Entry.DefinitionCount} uses:{Entry.InvocationCount} paths:{Entry.TemplatedPathUsageCount}";
}