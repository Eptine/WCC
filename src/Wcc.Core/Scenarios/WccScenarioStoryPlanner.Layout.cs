using System.Text;
using System.Text.RegularExpressions;
using Wcc.Core.Wml;

namespace Wcc.Core.Scenarios;

public static partial class WccScenarioStoryPlanner
{
    // Only compact the detached story being rendered. A macro can change its lexical
    // domain, and conditional branches must agree before a later switch is redundant.
    private static string CompactRenderedDomains(string text, string? openingDomain)
    {
        var analysis = WmlEditorAnalysis.Build(text);
        var opaque = analysis.TagOccurrences.Where(tag => !tag.IsClosing && tag.MatchingTagIndex is not null
                && tag.Name is not ("story" or "part" or "image" or "background_layer"))
            .Select(tag => (Start: StoryTagIndentStart(text, tag.StartOffset),
                End: analysis.TagOccurrences[tag.MatchingTagIndex!.Value].StartOffset + analysis.TagOccurrences[tag.MatchingTagIndex.Value].Length)).ToArray();
        var removals = new List<WccScenarioFieldSourcePatch>();
        var branches = new Stack<(string? Entry, string? Then, bool HasElse)>();
        var domain = openingDomain;
        foreach (var token in analysis.Tokens.OrderBy(token => token.StartOffset))
        {
            if (token.Kind is WmlEditorTokenKind.MacroInvocation or WmlEditorTokenKind.RawCode)
            { domain = null; continue; }
            if (token.Kind != WmlEditorTokenKind.Directive) continue;
            var raw = text.Substring(token.StartOffset, token.Length).Trim();
            var name = raw.Split([' ', '\t', '\r', '\n'], 2)[0];
            if (name is "#ifdef" or "#ifndef" or "#ifhave" or "#ifnhave" or "#ifver" or "#ifnver")
            { branches.Push((domain, null, false)); continue; }
            if (name == "#else" && branches.Count > 0)
            { var branch = branches.Pop(); branches.Push((branch.Entry, domain, true)); domain = branch.Entry; continue; }
            if (name == "#endif" && branches.Count > 0)
            {
                var branch = branches.Pop();
                var other = branch.HasElse ? branch.Then : branch.Entry;
                domain = domain == other ? domain : null;
                continue;
            }
            var match = Regex.Match(raw, @"^#textdomain[ \t]+([A-Za-z0-9_.-]+)[ \t]*$", RegexOptions.CultureInvariant);
            if (!match.Success) { domain = null; continue; }
            var next = match.Groups[1].Value;
            if (next == domain && !opaque.Any(range => token.StartOffset >= range.Start && token.StartOffset < range.End))
            {
                var start = token.StartOffset;
                while (start > 0 && text[start - 1] is ' ' or '\t') start--;
                var end = token.StartOffset + token.Length;
                while (end < text.Length && text[end] is ' ' or '\t') end++;
                if (end < text.Length && text[end] == '\r') end++;
                if (end < text.Length && text[end] == '\n') end++;
                removals.Add(new(start, end - start, ""));
            }
            domain = next;
        }
        return ApplyLocal(text, removals);
    }

    private static string FormatRenderedStory(WccScenarioStorySnapshot snapshot, int start, int length, string replacement)
    {
        var candidate = snapshot.Source.Text[..start] + replacement + snapshot.Source.Text[(start + length)..];
        var analysis = WmlEditorAnalysis.Build(replacement);
        var opaque = analysis.TagOccurrences.Where(tag => !tag.IsClosing && tag.MatchingTagIndex is not null
                && tag.Name is not ("story" or "part" or "image" or "background_layer"))
            .Select(tag => (Start: StoryTagIndentStart(replacement, tag.StartOffset),
                End: analysis.TagOccurrences[tag.MatchingTagIndex!.Value].StartOffset + analysis.TagOccurrences[tag.MatchingTagIndex.Value].Length)).ToArray();
        var changes = WmlFormatting.Build(candidate).Changes.Where(change => change.StartOffset >= start
            && change.StartOffset + change.Length <= start + replacement.Length
            && !opaque.Any(range => change.StartOffset - start >= range.Start && change.StartOffset - start < range.End));
        var builder = new StringBuilder(replacement);
        foreach (var change in changes.OrderByDescending(change => change.StartOffset))
            builder.Remove(change.StartOffset - start, change.Length).Insert(change.StartOffset - start, change.ReplacementText);
        return builder.ToString();
    }

    private static int StoryTagIndentStart(string text, int offset)
    {
        while (offset > 0 && text[offset - 1] is ' ' or '\t') offset--;
        return offset;
    }
}
