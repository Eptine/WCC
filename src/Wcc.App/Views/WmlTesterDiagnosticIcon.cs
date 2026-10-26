using System;
using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Wcc.Core.Wml.Checking;

namespace Wcc.App.Views;

public sealed record WmlTesterDiagnosticIconPresentation(string Glyph, IBrush Foreground, string ToolTip);

/// <summary>Presentation only: severity and confidence in the report are never changed.</summary>
public static class WmlTesterDiagnosticIcons
{
    // The same warning/error colors used by the shell status indicator.
    private static readonly IBrush Warning = new ImmutableSolidColorBrush(Color.Parse("#D6AB55"));
    private static readonly IBrush Error = new ImmutableSolidColorBrush(Color.Parse("#D57166"));

    public static WmlTesterDiagnosticIconPresentation Describe(WmlCheckDiagnostic diagnostic)
    {
        ArgumentNullException.ThrowIfNull(diagnostic);
        var isError = diagnostic.Severity == WmlCheckSeverity.Error;
        var needsSourceContext = !isError && diagnostic.Module == WmlCheckModuleId.References &&
            diagnostic.Confidence == WmlCheckConfidence.NeedsContext && diagnostic.Code is
                "refs.macro.not-found" or "refs.macro.ambiguous" or "refs.include.needs-context" or "refs.computed.needs-context" or
                "refs.campaign.context" or "refs.campaign.coverage" or "refs.campaign.target-unvisited";
        var isWarning = !isError && !needsSourceContext && diagnostic.Severity == WmlCheckSeverity.Warning;
        var heading = isError ? "Source error" : needsSourceContext ? "More source context needed" :
            isWarning ? "Source warning" : "Information";
        var explanation = isError ? "Review this source problem at its reported location." :
            needsSourceContext ? "This reference needs additional source context before its declaration or activation can be checked." :
            isWarning ? "Review this potential source issue." : "Information about the checked source.";
        var confidence = diagnostic.Confidence == WmlCheckConfidence.NeedsContext
            ? "Includes, branches or helper expansion still need context."
            : "The finding is supported by the captured authored source.";
        return new(needsSourceContext ? "..." : "!", isError ? Error : isWarning ? Warning : Brushes.White,
            $"{heading}\nSeverity: {diagnostic.Severity}\nConfidence: {diagnostic.Confidence}\n\n{explanation}\n{confidence}");
    }
}

/// <summary>One-way access to the same presentation for glyph, brush and tooltip bindings.</summary>
public sealed class WmlTesterDiagnosticIconConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not WmlCheckDiagnostic diagnostic) return AvaloniaProperty.UnsetValue;
        var presentation = WmlTesterDiagnosticIcons.Describe(diagnostic);
        return (parameter as string) switch
        {
            "Glyph" => presentation.Glyph,
            "Foreground" => presentation.Foreground,
            "ToolTip" => presentation.ToolTip,
            _ => AvaloniaProperty.UnsetValue
        };
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException("Diagnostic icons do not change report data.");
}
