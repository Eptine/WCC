using Wcc.Core.Macros;

namespace Wcc.App.ViewModels.Wcc;

public sealed class MacroTemplatedPathUsageVm : ViewModelBase
{
    public MacroTemplatedPathUsageVm(WccMacroTemplatedPathUsage usage)
    {
        Usage = usage;
    }

    public WccMacroTemplatedPathUsage Usage { get; }

    public string Title =>
        $"{Usage.AssetKind} · {Usage.OwnerBlockKind} · {Usage.SourceFieldKey}";

    public string RawValue => Usage.RawValue;
    public string? LookupPath => Usage.LookupPath;
    public string? SourceFilePath => Usage.SourceFilePath;
}