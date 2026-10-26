using System.Globalization;
using Wcc.Core.Scenarios;
using Wcc.Core.Wml;

namespace Wcc.App.ViewModels.Wcc;

/// <summary>
/// Read-only UI projection of one exact value and its closest verified authored
/// source route. It intentionally carries no edit command.
/// </summary>
public sealed class ScenarioScalarSourceBreadcrumbVm : ViewModelBase
{
    public required ScenarioScalarSourceRoute Route { get; init; }
    public required string AuthoredScenarioWml { get; init; }
    public string? SourcePath { get; init; }
    public int HostGeneration { get; init; }
    public long SessionGeneration { get; init; }

    public WmlFieldPresentation Presentation
    {
        get
        {
            var hasSourceLocation = Route.SourceSpan is { StartOffset: >= 0 } span &&
                span.EndOffsetExclusive > span.StartOffset;
            return WmlFieldPresentationDeriver.Derive(new WmlFieldEvidence
            {
                ValueAuthority = Route.EffectiveValue is null
                    ? WmlFieldValueAuthority.Unknown : WmlFieldValueAuthority.PreprocessedInitial,
                AuthoredPresence = Route.Kind is ScenarioScalarSourceRouteKind.DirectScalarToken or
                    ScenarioScalarSourceRouteKind.ContextSelectedConditionalToken && hasSourceLocation
                        ? WmlFieldAuthoredPresence.Present : WmlFieldAuthoredPresence.Unknown,
                Provenance = !hasSourceLocation ? WmlFieldSourceProvenance.None : Route.Kind switch
                {
                    ScenarioScalarSourceRouteKind.DirectScalarToken or
                        ScenarioScalarSourceRouteKind.ContextSelectedConditionalToken => WmlFieldSourceProvenance.Token,
                    ScenarioScalarSourceRouteKind.KnownDifficultyArgument => WmlFieldSourceProvenance.Contribution,
                    _ => WmlFieldSourceProvenance.Owner
                },
                HasSourceLocation = hasSourceLocation,
                CollectionCompleteness = WmlFieldCollectionCompleteness.NotApplicable,
                SessionAccess = WmlFieldSessionAccess.ReadOnly
            });
        }
    }
    public string ModeLabel => Presentation.Mode == WmlFieldPresentationMode.RawOnly
        ? "Raw source" : "Source-guided value";

    public string ValueLabel
    {
        get
        {
            var fieldLabel = Route.Field switch
            {
                ScenarioScalarSourceField.Turns => "Scenario turns",
                ScenarioScalarSourceField.SideGold => $"Side {FormatSideNumber()} gold",
                ScenarioScalarSourceField.SideIncome => $"Side {FormatSideNumber()} income",
                _ => Route.Field.ToString()
            };
            return Route.EffectiveValue is { } value
                ? $"{fieldLabel} = {value.ToString(CultureInfo.InvariantCulture)}"
                : fieldLabel;
        }
    }

    public string RouteLabel => Route.Kind switch
    {
        ScenarioScalarSourceRouteKind.DirectScalarToken => "direct authored integer",
        ScenarioScalarSourceRouteKind.ContextSelectedConditionalToken =>
            "selected conditional integer (read-only)",
        ScenarioScalarSourceRouteKind.KnownDifficultyArgument =>
            $"{Route.HelperMacroName} argument {(Route.HelperArgumentIndex ?? 0) + 1} (syntactic route)",
        _ => "raw WML only"
    };

    public string LocationLabel
    {
        get
        {
            var file = string.IsNullOrWhiteSpace(SourcePath)
                ? "current scenario"
                : SourcePath;
            return Route.SourceLineIndex is { } line
                ? $"{file}, scenario-block line {line + 1}"
                : file;
        }
    }

    public string Explanation => Route.Explanation;
    public bool CanOpenSource =>
        Route.SourceSpan is { } span &&
        span.StartOffset >= 0 &&
        span.EndOffsetExclusive >= span.StartOffset &&
        span.EndOffsetExclusive <= AuthoredScenarioWml.Length;
    public int SelectionStart => Route.SourceSpan?.StartOffset ?? 0;
    public int SelectionLength => Route.SourceSpan?.Length ?? 0;

    private string FormatSideNumber() =>
        Route.SideNumber?.ToString(CultureInfo.InvariantCulture) ?? "?";
}
