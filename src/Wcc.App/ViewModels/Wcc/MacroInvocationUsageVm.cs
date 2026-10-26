using Wcc.Core.Macros;

namespace Wcc.App.ViewModels.Wcc;

public sealed class MacroInvocationUsageVm : ViewModelBase
{
    public MacroInvocationUsageVm(WccMacroInvocationUsage usage)
    {
        Usage = usage;
    }

    public WccMacroInvocationUsage Usage { get; }

    public string Title =>
        $"{Usage.SourceFileName}  (line {Usage.LineIndex + 1})";

    public string RawInvocation => Usage.RawInvocation;
    public string RawLine => Usage.RawLine;
    public string? SourceFilePath => Usage.SourceFilePath;
}