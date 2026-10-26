using System;
using System.Collections.Generic;
using System.Linq;

namespace Wcc.App.ViewModels;

public sealed record WmlEditorReplacementChange(int StartOffset, int Length, string ReplacementText);

/// <summary>
/// Literal changes in one immutable UTF-16 buffer snapshot. A host must check
/// that its buffer still equals SourceText before applying these coordinates.
/// This plan has no file, parser, reference or persistence authority.
/// </summary>
public sealed class WmlEditorReplacementPlan
{
    internal WmlEditorReplacementPlan(string sourceText, IEnumerable<WmlEditorReplacementChange> changes,
        int? targetStartOffset = null, int targetLength = 0)
    {
        SourceText = sourceText;
        Changes = Array.AsReadOnly(changes.ToArray());
        TargetStartOffset = targetStartOffset;
        TargetLength = targetLength;
    }

    public string SourceText { get; }
    public IReadOnlyList<WmlEditorReplacementChange> Changes { get; }
    public int ChangedCount => Changes.Count;
    /// <summary>The match chosen by BuildOne, including an unchanged match.</summary>
    public int? TargetStartOffset { get; }
    public int TargetLength { get; }
}

/// <summary>Pure literal Find/Replace planning; replacement text is never interpreted.</summary>
public static class WmlEditorReplacement
{
    public static WmlEditorReplacementPlan BuildAll(string source, string query, string replacement)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(replacement);
        var matches = WmlEditorNavigation.FindMatches(source, query);
        return new WmlEditorReplacementPlan(source, matches
            .Where(offset => !IsUnchanged(source, offset, query.Length, replacement))
            .Select(offset => new WmlEditorReplacementChange(offset, query.Length, replacement)));
    }

    public static WmlEditorReplacementPlan BuildOne(string source, string query, string replacement,
        int selectionStart, int selectionLength)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(replacement);
        if (selectionStart < 0 || selectionStart > source.Length)
            throw new ArgumentOutOfRangeException(nameof(selectionStart));
        if (selectionLength < 0 || selectionLength > source.Length - selectionStart)
            throw new ArgumentOutOfRangeException(nameof(selectionLength));
        if (query.Length == 0) return new WmlEditorReplacementPlan(source, []);

        int offset;
        if (selectionLength == query.Length && source.AsSpan(selectionStart, selectionLength)
                .Equals(query.AsSpan(), StringComparison.OrdinalIgnoreCase))
        {
            offset = selectionStart;
        }
        else
        {
            var matches = WmlEditorNavigation.FindMatches(source, query);
            var matchIndex = WmlEditorNavigation.FindMatchIndex(matches, selectionStart);
            if (matchIndex < 0) return new WmlEditorReplacementPlan(source, []);
            offset = matches[matchIndex];
        }

        return new WmlEditorReplacementPlan(source,
            IsUnchanged(source, offset, query.Length, replacement)
                ? [] : [new WmlEditorReplacementChange(offset, query.Length, replacement)],
            offset, query.Length);
    }

    private static bool IsUnchanged(string source, int start, int length, string replacement) =>
        length == replacement.Length && source.AsSpan(start, length).SequenceEqual(replacement.AsSpan());
}
