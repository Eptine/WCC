using System.Globalization;
using Wcc.Core.Wml;

namespace Wcc.Core.Scenarios;

internal static class ScenarioScalarMergeSafetyAnalyzer
{
    public static string? FindUnsafeContribution(
        string normalizedWml,
        int selectedScenarioStart,
        ScenarioScalarSemanticTarget target)
    {
        return FindUnsafeContributionCore(
            normalizedWml,
            selectedScenarioStart,
            target);
    }

    private static string? FindUnsafeContributionCore(
        string wml,
        int scenarioStart,
        ScenarioScalarSemanticTarget target)
    {
        var found = FindScenarioFragments(wml, GetLineIndex(wml, scenarioStart));
        if (found.Selected is null)
            return "Modifying-tag safety could not identify the selected root scenario.";
        if (!HasExpectedClose(found.Selected) || found.Amendments.Any(x => !HasExpectedClose(x)))
            return "Modifying-tag safety could not prove the selected scenario amendment boundaries.";

        if (target.Field == ScenarioScalarSourceField.Turns)
        {
            return found.Amendments.Any(x => HasDirectKey(x, "turns"))
                ? "A [+scenario] modifying tag contributes direct 'turns' to the selected scenario."
                : null;
        }

        return FindUnsafeSideContribution(found.Selected, found.Amendments, target);
    }

    private static string? FindUnsafeSideContribution(
        WmlProjectionNode scenario,
        IReadOnlyList<WmlProjectionNode> amendments,
        ScenarioScalarSemanticTarget target)
    {
        var key = target.Field == ScenarioScalarSourceField.SideGold ? "gold" : "income";
        var wanted = target.SideNumber!.Value;
        var selected = scenario.Children.Where(x =>
            x.TagName.Equals("side", StringComparison.OrdinalIgnoreCase) &&
            !IsAmendment(x) &&
            TryGetDirectInteger(x, "side", out var number) &&
            number == wanted).ToArray();
        if (selected.Length != 1)
            return "Modifying-tag safety could not prove one selected direct side owner.";

        WmlProjectionNode? mostRecent = null;
        foreach (var fragment in Prepend(scenario, amendments))
        foreach (var side in fragment.Children.Where(x =>
                     x.TagName.Equals("side", StringComparison.OrdinalIgnoreCase)))
        {
            if (!HasExpectedClose(side))
                return "Modifying-tag safety could not prove a direct side amendment boundary.";
            var diagnostic = IsAmendment(side)
                ? CheckSideAmendment(side, mostRecent, selected[0], key, wanted)
                : CheckAppendedSide(side, fragment, scenario, key, wanted);
            if (diagnostic is not null)
                return diagnostic;
            if (!IsAmendment(side))
                mostRecent = side;
        }
        return null;
    }

    private static string? CheckSideAmendment(
        WmlProjectionNode amendment,
        WmlProjectionNode? owner,
        WmlProjectionNode selected,
        string key,
        int wanted)
    {
        var changesValue = HasDirectKey(amendment, key);
        var changesIdentity = HasDirectKey(amendment, "side");
        if (owner is null)
            return changesValue || changesIdentity
                ? "A [+side] modifying tag has unproven ownership of the addressed scalar."
                : null;
        if (ReferenceEquals(owner, selected))
        {
            if (changesIdentity)
                return "A [+side] modifying tag changes or obscures the selected side identity.";
            return changesValue
                ? $"A [+side] modifying tag contributes direct '{key}' to the selected side."
                : null;
        }
        if (changesIdentity &&
            (!TryGetDirectInteger(amendment, "side", out var amended) || amended == wanted))
        {
            return "A [+side] modifying tag makes ownership of the selected side number ambiguous.";
        }
        return null;
    }

    private static string? CheckAppendedSide(
        WmlProjectionNode side,
        WmlProjectionNode fragment,
        WmlProjectionNode scenario,
        string key,
        int wanted)
    {
        if (ReferenceEquals(fragment, scenario))
            return null;
        if (TryGetDirectInteger(side, "side", out var number))
            return number == wanted
                ? "A [+scenario] modifying tag appends another side with the selected number."
                : null;
        return HasDirectKey(side, key)
            ? "A [+scenario] modifying tag appends an addressed scalar with unproven side ownership."
            : null;
    }

    private static IEnumerable<WmlProjectionNode> Prepend(
        WmlProjectionNode first,
        IEnumerable<WmlProjectionNode> rest)
    {
        yield return first;
        foreach (var item in rest)
            yield return item;
    }

    private static bool HasDirectKey(WmlProjectionNode node, string key) =>
        Analyze(node, key).Occurrences.Count > 0;

    private static bool TryGetDirectInteger(WmlProjectionNode node, string key, out int value)
    {
        value = 0;
        var analysis = Analyze(node, key);
        return analysis.Kind == WmlTopLevelFieldKind.Plain &&
               analysis.Occurrences.Count == 1 &&
               int.TryParse(
                   analysis.Occurrences[0].Value,
                   NumberStyles.AllowLeadingSign,
                   CultureInfo.InvariantCulture,
                   out value);
    }

    private static WmlTopLevelFieldAnalysis Analyze(WmlProjectionNode node, string key)
    {
        var editable = WmlEditableBlockParser.Parse(
            new WmlBlock(node.TagName, node.RawText),
            EmptyKnownKeys);
        return WmlTopLevelFieldAnalyzer.Analyze(editable, key);
    }

    private static bool HasExpectedClose(WmlProjectionNode node)
    {
        if (node.EndLineExclusive <= node.StartLine ||
            node.EndLineExclusive > node.SourceLines.Count)
            return false;
        var code = WmlInlineCommentSplitter
            .Split(node.SourceLines[node.EndLineExclusive - 1]).Value.Trim();
        return code.Equals($"[/{node.TagName}]", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsAmendment(WmlProjectionNode node)
    {
        var code = WmlInlineCommentSplitter
            .Split(node.SourceLines[node.StartLine]).Value.Trim();
        return code.StartsWith("[+", StringComparison.Ordinal);
    }

    private static int GetLineIndex(string text, int offset)
    {
        if (offset < 0 || offset > text.Length)
            return -1;
        var line = 0;
        for (var index = 0; index < offset; index++)
            if (text[index] == '\n')
                line++;
        return line;
    }

    private static (WmlProjectionNode? Selected, List<WmlProjectionNode> Amendments)
        FindScenarioFragments(string wml, int selectedLine)
    {
        WmlProjectionNode? selected = null;
        WmlProjectionNode? mostRecent = null;
        var amendments = new List<WmlProjectionNode>();
        foreach (var root in WmlProjectionTreeParser.Parse(wml).Roots)
        {
            if (!root.TagName.Equals("scenario", StringComparison.OrdinalIgnoreCase))
                continue;
            if (!IsAmendment(root))
            {
                mostRecent = root;
                if (root.StartLine == selectedLine)
                    selected = root;
            }
            else if (mostRecent?.StartLine == selectedLine)
                amendments.Add(root);
        }
        return (selected, amendments);
    }

    private static readonly HashSet<string> EmptyKnownKeys =
        new(StringComparer.OrdinalIgnoreCase);
}
