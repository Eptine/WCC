using Wcc.Core.Wml;

namespace Wcc.Core.Scenarios;

/// <summary>A source-bound whole flag invocation proposal, never write authority.</summary>
public sealed record WccScenarioSideFlagPresetEdit(WmlStaticScalarOwner SideOwner,
    string InvocationText, WmlMacroExpansionDefinition Definition)
{
    public bool AuthorizesPersistence => false;
}

public sealed record WccSideFlagPresetSourcePlan(WccScenarioFieldSourcePatch? Patch,
    WmlStaticSourceRange? BaselineInvocation, string? Diagnostic, bool IsNoOp)
{
    public bool IsSuccess => Diagnostic is null;
    public bool AuthorizesPersistence => false;
}

/// <summary>Bounded source composition for the two exact Core flag templates.</summary>
public static class WccSideFlagPresetSourcePlanner
{
    public static WccSideFlagPresetSourcePlan Plan(WmlStaticSource source, WccScenarioSideFlagPresetEdit edit)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(edit);
        if (edit.SideOwner is null || edit.Definition is null ||
            !TryReadInvocation(edit.InvocationText, out var name, out var variant) ||
            edit.Definition.Name != name || !IsSupportedDefinition(edit.Definition))
            return Refused("The flag preset is not an exact supported flag invocation and definition.");
        var owner = edit.SideOwner;
        if (!IsLiteralSide(owner, source) || owner.OpeningOrigin is not { } opening || owner.ClosingOrigin is not { } closing)
            return Refused("A flag preset needs one exact unamended literal side in the pristine source.");
        if (FindWinningInvocation(owner, source) is { } current)
        {
            TryReadInvocation(current.Text, out var oldName, out var oldVariant);
            var unchanged = oldName == name && oldVariant == variant;
            return new(new(current.StartOffset, current.Length, unchanged ? current.Text : edit.InvocationText.Trim()),
                current, null, unchanged);
        }
        if (owner.ResolveField("flag").State != WmlStaticScalarResolutionState.ProvenAbsent ||
            owner.ResolveField("flag_icon").State != WmlStaticScalarResolutionState.ProvenAbsent)
            return Refused("Custom, shared or uncertain flag values need RAW source; only a winning local preset or an absent flag pair can be staged.");
        var structure = WmlAuthoredStructureScanner.Scan(source.Text);
        var line = structure.Lines.Single(line => closing.StartOffset >= line.StartOffset && closing.StartOffset < line.ContentEndOffset);
        if (!source.Text.AsSpan(line.StartOffset, closing.StartOffset - line.StartOffset).Trim().IsEmpty)
            return Refused("The side closing tag has no separate safe insertion line.");
        var indentation = source.Text[line.StartOffset..closing.StartOffset];
        var newline = source.Text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n"
            : source.Text.Contains('\n') ? "\n" : "\r";
        return new(new(line.StartOffset, 0, indentation + "    " + edit.InvocationText.Trim() + newline), null, null, false);
    }

    public static bool IsLiteralSide(WmlStaticScalarOwner owner, WmlStaticSource source)
    {
        if (owner.TagName != "side" || owner.Parent?.TagName != "scenario" || owner.IsAmended ||
            owner.OpeningOutputOffset is null || owner.ClosingOutputOffset is null ||
            owner.OpeningOrigin is not { } opening || owner.ClosingOrigin is not { } closing ||
            !WccScenarioFieldRouteAnalyzer.SameSource(opening.Source, source) ||
            !WccScenarioFieldRouteAnalyzer.SameSource(closing.Source, source) ||
            opening.Text != "[side]" || closing.Text != "[/side]" || closing.StartOffset <= opening.StartOffset)
            return false;
        var structure = WmlAuthoredStructureScanner.Scan(source.Text);
        return structure.Problem is null && structure.MaskedText.Substring(opening.StartOffset, opening.Length) == opening.Text &&
            structure.MaskedText.Substring(closing.StartOffset, closing.Length) == closing.Text;
    }

    public static WmlStaticSourceRange? FindWinningInvocation(WmlStaticScalarOwner owner, WmlStaticSource source)
    {
        if (!IsLiteralSide(owner, source) || !owner.Fields.TryGetValue("flag", out var flag) ||
            !owner.Fields.TryGetValue("flag_icon", out var icon) || flag.WinningContribution is not { } flagValue ||
            icon.WinningContribution is not { } iconValue) return null;
        var structure = WmlAuthoredStructureScanner.Scan(source.Text);
        foreach (var frame in flagValue.ExpansionStack.Reverse())
        {
            if (!IsSupportedDefinition(new(frame.MacroName, frame.Definition.Text, frame.Definition.Source.Path)) ||
                frame.InvocationOrigins.Count != 1 || frame.InvocationOrigins[0] is not { } invocation ||
                !WccScenarioFieldRouteAnalyzer.SameSource(invocation.Source, source) ||
                invocation.StartOffset <= owner.OpeningOrigin!.StartOffset ||
                invocation.StartOffset + invocation.Length > owner.ClosingOrigin!.StartOffset ||
                !structure.Macros.Any(macro => macro.StartOffset == invocation.StartOffset &&
                    macro.EndOffsetExclusive == invocation.StartOffset + invocation.Length) ||
                !TryReadInvocation(invocation.Text, out var name, out _) || name != frame.MacroName ||
                !iconValue.ExpansionStack.Any(other => other.MacroName == frame.MacroName &&
                    other.Definition == frame.Definition && other.InvocationOrigins.SequenceEqual(frame.InvocationOrigins))) continue;
            return invocation;
        }
        return null;
    }

    public static bool TryGetValues(string invocation, out string flag, out string icon)
    {
        flag = icon = string.Empty;
        if (!TryReadInvocation(invocation, out var name, out var variant)) return false;
        flag = "flags/" + variant + "-flag-[1~" + (name == "FLAG_VARIANT6" ? "6" : "4") + "].png:150";
        icon = "flags/" + variant + "-flag-icon.png";
        return true;
    }

    public static bool TryReadInvocation(string invocation, out string name, out string variant)
    {
        name = variant = string.Empty;
        if (invocation is null || !WmlMacroInvocationSyntaxScanner.TryParseSingle(invocation.Trim(), out var parsed) ||
            parsed!.Name is not ("FLAG_VARIANT" or "FLAG_VARIANT6") || parsed.Arguments.Count != 1) return false;
        var argument = parsed.Arguments[0].RawText.Trim();
        if (argument.Length == 0 || !argument.Split('/').All(component => component.Length > 0 && component is not ("." or "..") &&
                component.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.'))) return false;
        name = parsed.Name; variant = argument;
        return true;
    }

    public static bool IsSupportedDefinition(WmlMacroExpansionDefinition definition)
    {
        if (definition.Name is not ("FLAG_VARIANT" or "FLAG_VARIANT6")) return false;
        var code = new List<string>();
        foreach (var line in definition.RawText.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith('#'))
            {
                var directive = trimmed[1..].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
                if (directive is "define" or "enddef") code.Add("#" + WmlInlineCommentSplitter.Split(trimmed[1..]).Value.Trim());
                else if (directive is "undef" or "ifdef" or "ifndef" or "ifhave" or "ifnhave" or "ifver" or "ifnver" or
                         "else" or "endif" or "textdomain" or "error" or "warning" or "deprecated" or "arg" or "endarg") return false;
                continue;
            }
            var value = WmlInlineCommentSplitter.Split(trimmed).Value.Trim();
            if (value.Length > 0) code.Add(value.Replace(" ", "", StringComparison.Ordinal).Replace("\t", "", StringComparison.Ordinal));
        }
        var frames = definition.Name == "FLAG_VARIANT6" ? "6" : "4";
        return code.SequenceEqual(new[] { "#define " + definition.Name + " NAME", "flag=flags/{NAME}-flag-[1~" + frames +
            "].png:150", "flag_icon=flags/{NAME}-flag-icon.png", "#enddef" });
    }

    private static WccSideFlagPresetSourcePlan Refused(string diagnostic) => new(null, null, diagnostic, false);
}
