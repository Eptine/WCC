using Wcc.Core.Wml;

namespace Wcc.Core.Scenarios;

public enum WccScenarioEventSourceEditKind { AddLiteral, ReplaceLiteral, RemoveLiteral, RemoveMacroInvocation, ReplaceMacroInvocation, AddMacroInvocation }

/// <summary>Immutable authored event intent. The exact source and all-context review supply authority.</summary>
public sealed record WccScenarioEventSourceEdit(WccScenarioEventSourceEditKind Kind,
    WmlStaticSourceRange? Target = null, string? EventText = null)
{
    public WccCampaignLocalPresetEntry? Preset { get; init; }
    public WccScenarioEventGraphSeed? GraphSeed { get; init; }
    public WccScenarioEventGraphSeed? PreviousGraphSeed { get; init; }
    public bool AuthorizesPersistence => false;
}

public sealed record WccScenarioEventSourcePlan(WccScenarioFieldSourcePatch? Patch, string? Diagnostic, bool IsNoOp)
{
    public bool IsSuccess => Diagnostic is null;
    public bool AuthorizesPersistence => false;
}

/// <summary>Lossless source composition only; an invocation is not assumed to produce events.</summary>
public static class WccScenarioEventSourcePlanner
{
    public static WccScenarioEventSourcePlan Plan(WmlStaticSourceRange scenarioRange, WccScenarioEventSourceEdit edit)
    {
        ArgumentNullException.ThrowIfNull(scenarioRange);
        ArgumentNullException.ThrowIfNull(edit);
        var source = scenarioRange.Source;
        if (!source.IsAuthored || scenarioRange.StartOffset < 0 || scenarioRange.Length < 0 ||
            scenarioRange.StartOffset > source.Text.Length - scenarioRange.Length ||
            WmlSingleBlockValidator.Validate(scenarioRange.Text, "scenario") is { IsValid: false })
            return Refused("The event needs an exact complete authored scenario range.");
        if (!Enum.IsDefined(edit.Kind)) return Refused("The event operation is unsupported.");
        if (edit.GraphSeed is { } graphSeed)
        {
            if (edit.Kind is not (WccScenarioEventSourceEditKind.AddLiteral or WccScenarioEventSourceEditKind.ReplaceLiteral)
                || graphSeed.OriginalText != edit.EventText)
                return Refused("The structured event needs its exact captured replacement text.");
            if (edit.PreviousGraphSeed is { } previous && (edit.Kind != WccScenarioEventSourceEditKind.ReplaceLiteral
                    || edit.Target is null || previous.OriginalText != edit.Target.Text))
                return Refused("The structured event's previous graph no longer matches its exact authored source.");
        }
        else if (edit.PreviousGraphSeed is not null)
            return Refused("A previous structured event needs its exact replacement graph.");
        if (edit.Preset is not null && edit.Kind != WccScenarioEventSourceEditKind.AddMacroInvocation)
            return Refused("Only a new event preset invocation can carry a selected shared definition.");
        if (edit.Kind == WccScenarioEventSourceEditKind.AddMacroInvocation)
        {
            if (edit.Preset is not { IsSupported: true, Role: WccCampaignLocalPresetRole.Event, Payload.EventText: not null } preset
                || edit.Target is not null || edit.EventText != "{" + preset.Name + "}"
                || !WmlMacroInvocationSyntaxScanner.TryParseSingle(edit.EventText, out var invocation)
                || invocation!.Name != preset.Name || invocation.Arguments.Count != 0)
                return Refused("Choose one exact supported saved Event preset; its new invocation has no arguments or local source target.");
        }
        if (edit.Kind is WccScenarioEventSourceEditKind.AddLiteral or WccScenarioEventSourceEditKind.ReplaceLiteral)
        {
            if (edit.EventText is null || WmlSingleBlockValidator.Validate(edit.EventText, "event") is { IsValid: false })
                return Refused("An event draft must contain exactly one balanced literal [event] block.");
            if (HasUnsafeControls(edit.EventText))
                return Refused("Event drafts cannot change preprocessor definitions, translation domains or global directive state. Use full RAW source.");
            if (edit.Kind == WccScenarioEventSourceEditKind.AddLiteral)
            {
                var text = edit.EventText.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
                var values = WmlKeyValueExtractor.Extract(new WmlBlock("event", text),
                    new HashSet<string>(["name"], StringComparer.Ordinal));
                if (!values.Known.TryGetValue("name", out var name) || string.IsNullOrWhiteSpace(WmlDisplayStringNormalizer.Normalize(name)))
                    return Refused("A new event needs a non-empty direct name= trigger.");
            }
        }
        else if (edit.Kind == WccScenarioEventSourceEditKind.ReplaceMacroInvocation)
        {
            if (edit.EventText is null || !WmlMacroInvocationSyntaxScanner.TryParseSingle(edit.EventText.Trim(), out _))
                return Refused("An event invocation draft must contain one complete balanced macro invocation, without other root content.");
            if (HasUnsafeControls(edit.EventText))
                return Refused("Event invocation drafts cannot change global preprocessor state. Use full RAW source.");
        }
        else if (edit.EventText is not null && edit.Kind != WccScenarioEventSourceEditKind.AddMacroInvocation)
            return Refused("An event removal cannot carry replacement WML.");
        if (edit.Kind is WccScenarioEventSourceEditKind.AddLiteral or WccScenarioEventSourceEditKind.AddMacroInvocation)
        {
            if (edit.Target is not null) return Refused("A new event cannot replace an existing source target.");
            var structure = WmlAuthoredStructureScanner.Scan(scenarioRange.Text);
            var root = WmlProjectionTreeParser.Parse(structure.MaskedText).Roots.Single();
            var line = structure.Lines[root.EndLineExclusive - 1];
            if (structure.MaskedText.AsSpan(line.StartOffset, line.ContentEndOffset - line.StartOffset).Trim().ToString() != "[/scenario]")
                return Refused("The scenario has no separate literal closing line for an event insertion.");
            var newline = source.Text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : source.Text.Contains('\n') ? "\n" : "\r";
            return new(new(scenarioRange.StartOffset + line.StartOffset, 0, edit.EventText!.TrimEnd('\r', '\n') + newline), null, false);
        }
        if (edit.Target is not { } target || !WccScenarioFieldRouteAnalyzer.SameSource(source, target.Source) ||
            target.StartOffset < scenarioRange.StartOffset || target.Length <= 0 ||
            target.StartOffset > scenarioRange.StartOffset + scenarioRange.Length - target.Length)
            return Refused("The event target no longer belongs to the exact pristine scenario source.");
        var entries = ScenarioContentSourceInventory.Build(scenarioRange.Text).Where(entry =>
            scenarioRange.StartOffset + entry.StartOffset == target.StartOffset &&
            entry.EndOffsetExclusive - entry.StartOffset == target.Length && entry.Text == target.Text).ToArray();
        if (entries.Length != 1) return Refused("The event target is not one exact authored root fragment.");
        var entry = entries[0];
        if (edit.Kind is WccScenarioEventSourceEditKind.RemoveMacroInvocation or WccScenarioEventSourceEditKind.ReplaceMacroInvocation)
        {
            if (entry.Kind != ScenarioContentSourceKind.MacroInvocation)
                return Refused("Only a complete local root invocation can replace or remove a generated event group.");
        }
        else if (entry.Kind != ScenarioContentSourceKind.ChildTag || entry.Name != "event")
            return Refused("Only a direct literal [event], without amendments or generated ownership, can be replaced or removed.");
        if (HasUnsafeControls(target.Text))
            return Refused("This event carries global preprocessor state and needs full RAW source.");
        var replacement = edit.Kind is WccScenarioEventSourceEditKind.ReplaceLiteral or WccScenarioEventSourceEditKind.ReplaceMacroInvocation
            ? edit.EventText! : string.Empty;
        return new(new(target.StartOffset, target.Length, replacement), null, replacement == target.Text);
    }

    internal static bool HasUnsafeControls(string text, bool isCapturedMacroBody = false)
    {
        var state = new WmlStringScanState();
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
        var last = Array.FindLastIndex(lines, line => !string.IsNullOrWhiteSpace(line));
        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index];
            var hash = FirstExecutableEventHash(line, state);
            var value = hash < 0 ? ReadOnlySpan<char>.Empty : line.AsSpan(hash);
            if (!value.IsEmpty)
            {
                var end = 1;
                while (end < value.Length && char.IsLetter(value[end])) end++;
                if (end == value.Length || char.IsWhiteSpace(value[end]))
                {
                    var name = value[1..end].ToString().ToLowerInvariant();
                    var scoped = isCapturedMacroBody && (name is "arg" or "endarg" || name == "enddef" && index == last);
                    if (!scoped && name is "define" or "enddef" or "undef" or "textdomain" or "arg" or "endarg" or "error" or "warning" or "deprecated")
                        return true;
                }
            }
            WmlRawStringScanner.UpdateState(line, ref state);
        }
        return false;
    }

    private static int FirstExecutableEventHash(ReadOnlySpan<char> line, WmlStringScanState state)
    {
        for (var index = 0; index < line.Length; index++)
        {
            if (state.InsideRawString)
            {
                if (line[index] == '>' && index + 1 < line.Length && line[index + 1] == '>')
                { state.InsideRawString = false; index++; }
                continue;
            }
            if (state.QuotedStringDelimiter is { } quote)
            {
                if (line[index] == quote)
                {
                    if (index + 1 < line.Length && line[index + 1] == quote) index++;
                    else state.QuotedStringDelimiter = null;
                }
                continue;
            }
            if (line[index] == '#') return index;
            if (line[index] == '"') state.QuotedStringDelimiter = '"';
            else if (line[index] == '<' && index + 1 < line.Length && line[index + 1] == '<')
            { state.InsideRawString = true; index++; }
        }
        return -1;
    }

    private static WccScenarioEventSourcePlan Refused(string diagnostic) => new(null, diagnostic, false);
}
