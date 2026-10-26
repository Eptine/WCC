using System;
using System.Collections.Generic;
using System.Linq;
using Wcc.Core.Wml;

namespace Wcc.App.ViewModels;

/// <summary>
/// Lexical uses of one name in an exact source snapshot. The name and kind do not
/// establish a resolved definition, ownership, or permission to edit anything.
/// </summary>
public sealed record WmlEditorReferenceGroup(
    WmlEditorReferenceKind Kind,
    string Name,
    IReadOnlyList<WmlEditorReference> Occurrences)
{
    public int Count => Occurrences.Count;
}

/// <summary>Pure UTF-16 navigation for an editor buffer; never evaluates or changes WML.</summary>
public static class WmlEditorNavigation
{
    /// <summary>Finds literal, non-overlapping, ordinal-ignore-case matches in the unchanged text.</summary>
    public static IReadOnlyList<int> FindMatches(string text, string query)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(query);
        if (query.Length == 0 || query.Length > text.Length)
            return Array.Empty<int>();

        var matches = new List<int>();
        for (var searchFrom = 0; searchFrom <= text.Length - query.Length;)
        {
            var match = text.IndexOf(query, searchFrom, StringComparison.OrdinalIgnoreCase);
            if (match < 0)
                break;
            matches.Add(match);
            searchFrom = match + query.Length;
        }
        return matches.AsReadOnly();
    }

    /// <summary>
    /// Selects the first match at or after an anchor, or the last strictly before it
    /// when moving backwards. Wraps at the ends; returns -1 when there are no matches.
    /// Matches must be in source order, as returned by <see cref="FindMatches"/>.
    /// </summary>
    public static int FindMatchIndex(IReadOnlyList<int> matches, int anchor, int direction = 1)
    {
        ArgumentNullException.ThrowIfNull(matches);
        if (matches.Count == 0)
            return -1;
        anchor = Math.Max(0, anchor);
        if (direction < 0)
        {
            for (var index = matches.Count - 1; index >= 0; index--)
                if (matches[index] < anchor)
                    return index;
            return matches.Count - 1;
        }
        for (var index = 0; index < matches.Count; index++)
            if (matches[index] >= anchor)
                return index;
        return 0;
    }

    /// <summary>Advances one match from a selected index, or starts from the anchor if no index is selected.</summary>
    public static int MoveMatchIndex(
        IReadOnlyList<int> matches, int currentIndex, int anchor, int direction = 1)
    {
        ArgumentNullException.ThrowIfNull(matches);
        if (currentIndex < 0 || currentIndex >= matches.Count)
            return FindMatchIndex(matches, anchor, direction);
        return direction < 0
            ? currentIndex == 0 ? matches.Count - 1 : currentIndex - 1
            : currentIndex == matches.Count - 1 ? 0 : currentIndex + 1;
    }

    /// <summary>
    /// Groups valid exact occurrences by case-sensitive name and kind, in source order.
    /// Out-of-range, duplicate or changed-text hints cannot become selectable references.
    /// The host must also fence publication by buffer version; matching spans alone do not
    /// prove that a complete analysis belongs to the current buffer.
    /// </summary>
    public static IReadOnlyList<WmlEditorReferenceGroup> GroupReferences(
        string source, WmlEditorAnalysis analysis)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(analysis);
        var references = analysis.References
            .Where(reference => IsExactOccurrence(source, reference))
            .OrderBy(reference => reference.StartOffset)
            .ThenByDescending(reference => reference.Length)
            .GroupBy(reference => (reference.Kind, reference.Name))
            .Select(group => new WmlEditorReferenceGroup(group.Key.Kind, group.Key.Name,
                Array.AsReadOnly(group.DistinctBy(reference => (reference.StartOffset, reference.Length)).ToArray())))
            .ToArray();
        return Array.AsReadOnly(references);
    }

    /// <summary>Tests exact selection of one invocation, rather than merely touching its boundary.</summary>
    public static bool IsSelectionOf(WmlEditorReference reference, int selectionStart, int selectionLength)
    {
        ArgumentNullException.ThrowIfNull(reference);
        return reference.StartOffset >= 0 && reference.Length > 0 &&
               selectionStart == reference.StartOffset && selectionLength == reference.Length;
    }

    /// <summary>
    /// Cycles after an exactly selected use. A caret inside a use selects that use first;
    /// otherwise navigation starts at the source anchor and wraps. Nested uses prefer the
    /// smallest containing invocation. Groups must come from <see cref="GroupReferences"/>.
    /// </summary>
    public static int FindNextReferenceIndex(
        WmlEditorReferenceGroup group, int selectionStart, int selectionLength, int direction = 1)
    {
        ArgumentNullException.ThrowIfNull(group);
        var occurrences = group.Occurrences;
        if (occurrences.Count == 0)
            return -1;
        for (var index = 0; index < occurrences.Count; index++)
        {
            if (!IsSelectionOf(occurrences[index], selectionStart, selectionLength))
                continue;
            return direction < 0
                ? index == 0 ? occurrences.Count - 1 : index - 1
                : index == occurrences.Count - 1 ? 0 : index + 1;
        }
        if (selectionLength == 0)
        {
            var containing = -1;
            for (var index = 0; index < occurrences.Count; index++)
            {
                var occurrence = occurrences[index];
                if (selectionStart >= occurrence.StartOffset &&
                    selectionStart - occurrence.StartOffset < occurrence.Length &&
                    (containing < 0 || occurrence.Length < occurrences[containing].Length))
                    containing = index;
            }
            if (containing >= 0)
                return containing;
        }
        selectionStart = Math.Max(0, selectionStart);
        if (direction < 0)
        {
            for (var index = occurrences.Count - 1; index >= 0; index--)
                if (occurrences[index].StartOffset < selectionStart)
                    return index;
            return occurrences.Count - 1;
        }
        for (var index = 0; index < occurrences.Count; index++)
            if (occurrences[index].StartOffset >= selectionStart)
                return index;
        return 0;
    }

    private static bool IsExactOccurrence(string source, WmlEditorReference reference)
        => reference.StartOffset >= 0 && reference.StartOffset <= source.Length &&
           reference.Length > 0 && reference.Length <= source.Length - reference.StartOffset &&
           reference.RawText.Length == reference.Length &&
           source.AsSpan(reference.StartOffset, reference.Length).SequenceEqual(reference.RawText.AsSpan());
}
