using System;
using System.IO;
using System.Linq;
using Wcc.Core.Wml;

namespace Wcc.App.ViewModels;

/// <summary>Navigation in one exact displayed buffer; never a source edit capability.</summary>
public sealed class WmlEditorInitialSelection
{
    private readonly string _text;
    private readonly int _start;
    private readonly int _length;

    private WmlEditorInitialSelection(string text, int start, int length)
    { _text = text; _start = start; _length = length; }

    /// <summary>The containing draft/fragment starts here when no exact field is known.</summary>
    public static WmlEditorInitialSelection Owner(string displayedText) => new(displayedText, 0, 0);

    /// <summary>Uses lexical field tokens only inside the existing host-selected RAW fragment.</summary>
    public static WmlEditorInitialSelection FieldFragment(string displayedText, string key)
    {
        ArgumentNullException.ThrowIfNull(displayedText);
        ArgumentNullException.ThrowIfNull(key);
        var analysis = WmlEditorAnalysis.Build(displayedText);
        // Incomplete child boundaries cannot establish the fragment's direct attributes.
        if (analysis.TagOccurrences.Any(tag => !tag.IsComplete || tag.IsComputedName || tag.MatchingTagIndex is null))
            return Owner(displayedText);
        var matches = analysis.Tokens.Where(token => token.Kind == WmlEditorTokenKind.AttributeName &&
            displayedText.AsSpan(token.StartOffset, token.Length).Equals(key.AsSpan(), StringComparison.OrdinalIgnoreCase) &&
            !analysis.TagOccurrences.Any(tag => !tag.IsClosing && tag.StartOffset < token.StartOffset &&
                tag.MatchingTagIndex is { } partner && analysis.TagOccurrences[partner].StartOffset > token.StartOffset) &&
            !analysis.Folds.Any(fold => fold.Label.StartsWith("#define", StringComparison.Ordinal) &&
                fold.StartOffset <= token.StartOffset && token.StartOffset < fold.EndOffsetExclusive)).Take(2).ToArray();
        return matches.Length == 1 ? new(displayedText, matches[0].StartOffset, matches[0].Length) : Owner(displayedText);
    }

    /// <summary>
    /// Rebases an existing captured location into its exact displayed source slice.
    /// A different file, stale text or exterior range falls back to that displayed owner.
    /// </summary>
    public static WmlEditorInitialSelection CapturedRange(string displayedText,
        string displayedSourcePath, string sourceText, int displayedSourceStart,
        string? locationSourcePath, string? locationSourceText, int locationStart, int locationLength)
    {
        ArgumentNullException.ThrowIfNull(displayedText);
        ArgumentNullException.ThrowIfNull(sourceText);
        if (!SamePath(displayedSourcePath, locationSourcePath) ||
            !string.Equals(sourceText, locationSourceText, StringComparison.Ordinal) ||
            !Inside(displayedSourceStart, displayedText.Length, sourceText.Length) ||
            !sourceText.AsSpan(displayedSourceStart, displayedText.Length).SequenceEqual(displayedText.AsSpan()) ||
            !Inside(locationStart, locationLength, sourceText.Length) ||
            locationStart < displayedSourceStart || locationLength > displayedText.Length - (locationStart - displayedSourceStart))
            return Owner(displayedText);
        return new(displayedText, locationStart - displayedSourceStart, locationLength);
    }

    public bool TryGetSelection(string currentText, out int start, out int length)
    {
        start = 0;
        length = 0;
        if (!string.Equals(currentText, _text, StringComparison.Ordinal)) return false;
        start = _start;
        length = _length;
        return true;
    }

    private static bool Inside(int start, int length, int total) =>
        start >= 0 && length >= 0 && start <= total && length <= total - start;

    private static bool SamePath(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right)) return false;
        try
        {
            return string.Equals(Path.GetFullPath(left), Path.GetFullPath(right),
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        }
        catch (Exception failure) when (failure is ArgumentException or NotSupportedException or PathTooLongException)
        { return false; }
    }
}
