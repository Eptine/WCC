using Wcc.Core.Macros;

namespace Wcc.App.ViewModels.Wcc;

public sealed class MacroDefinitionOccurrenceVm : ViewModelBase
{
    public MacroDefinitionOccurrenceVm(WccMacroDefinitionOccurrence occurrence)
    {
        Occurrence = occurrence;
    }

    public WccMacroDefinitionOccurrence Occurrence { get; }

    public string Title =>
        $"{Occurrence.SourceFileName}  (lines {Occurrence.StartLineIndex + 1}-{Occurrence.EndLineIndexExclusive})";

    public string RawText => Occurrence.RawText;
    public string? SourceFilePath => Occurrence.SourceFilePath;
}