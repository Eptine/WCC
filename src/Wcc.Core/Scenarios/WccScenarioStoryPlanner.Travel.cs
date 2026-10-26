using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using Wcc.Core.Wml;

namespace Wcc.Core.Scenarios;

public static partial class WccScenarioStoryPlanner
{
    private const string TravelMarkerPrefix = "# WCC story travel: ";
    public static IReadOnlyList<string> SupportedTravelMacros { get; } = Array.AsReadOnly(new[]
        { "NEW_JOURNEY", "OLD_JOURNEY", "NEW_BATTLE", "OLD_BATTLE", "NEW_REST", "OLD_REST" });
    public static string? TravelPreviewImage(string? macro) => macro switch
    {
        "NEW_JOURNEY" => "misc/new-journey.png", "OLD_JOURNEY" => "misc/dot-white.png",
        "NEW_BATTLE" => "misc/new-battle2.png", "OLD_BATTLE" => "misc/cross-white.png",
        "NEW_REST" => "misc/flag-red.png", "OLD_REST" => "misc/flag-white.png", _ => null
    };
    private static IEnumerable<WccStoryLiteralNode> ReadDirectTravelInvocations(WmlStaticSource source,
        WmlStaticScalarOwner owner, string effective, IReadOnlyList<WccStoryLiteralNode> literalChildren)
    {
        if (owner.TagName != "part" || owner.OpeningOrigin is not { } opening || owner.ClosingOrigin is not { } closing) yield break;
        foreach (var token in WmlEditorAnalysis.Build(effective).Tokens.Where(token => token.Kind == WmlEditorTokenKind.MacroInvocation
            && token.StartOffset > opening.StartOffset && token.StartOffset < closing.StartOffset
            && !literalChildren.Any(child => token.StartOffset >= child.Range.StartOffset && token.StartOffset < child.Range.StartOffset + child.Range.Length)))
        {
            var raw = effective.Substring(token.StartOffset, token.Length);
            var match = Regex.Match(raw, @"^\{(NEW_JOURNEY|OLD_JOURNEY|NEW_BATTLE|OLD_BATTLE|NEW_REST|OLD_REST)\s+([+-]?\d+)\s+([+-]?\d+)\s*\}$", RegexOptions.CultureInvariant);
            if (!match.Success || !int.TryParse(match.Groups[2].Value, out _) || !int.TryParse(match.Groups[3].Value, out _)) continue;
            var invocation = new WmlStaticSourceRange(source, token.StartOffset, token.Length);
            var start = token.StartOffset; var id = "travel-" + start;
            var lineStart = effective.LastIndexOf('\n', Math.Max(0, start - 1)) + 1;
            var previousEnd = lineStart > 0 ? lineStart - 1 : 0;
            var previousStart = previousEnd > 0 ? effective.LastIndexOf('\n', previousEnd - 1) + 1 : 0;
            var previous = effective[previousStart..previousEnd].Trim();
            if (previous.StartsWith(TravelMarkerPrefix, StringComparison.Ordinal))
            {
                var marker = previous[TravelMarkerPrefix.Length..].Trim();
                if (!ValidNodeId(marker)) throw new InvalidOperationException("A Story travel marker has no exact bounded logical identity.");
                id = marker; start = previousStart;
            }
            var fields = new Dictionary<string, WccStoryLiteralField>(StringComparer.Ordinal);
            foreach (var (key, group) in new[] { ("x", match.Groups[2]), ("y", match.Groups[3]) })
            {
                var value = new WmlStaticSourceRange(source, token.StartOffset + group.Index, group.Length);
                fields[key] = new(key, invocation, value, WmlTextDomainCapture.BeforeOffset(effective, token.StartOffset));
            }
            yield return new(id, "image", new(source, start, token.StartOffset + token.Length - start),
                new ReadOnlyDictionary<string, WccStoryLiteralField>(fields), [], null, null)
            { TravelMacroName = match.Groups[1].Value, TravelInvocation = invocation, EffectiveText = source.Text.Substring(start, token.StartOffset + token.Length - start) };
        }
    }
    private static WccScenarioStoryChildSnapshot AddTravelDisplay(WccScenarioStoryChildSnapshot child)
    {
        var macro = child.Nodes.Values.FirstOrDefault()?.TravelMacroName;
        if (macro is null) return child;
        var fields = child.Fields.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        foreach (var key in ImageKeys.Where(key => key is not ("x" or "y")))
        {
            var raw = key switch { "file" => TravelPreviewImage(macro), "centered" => "yes", "delay" => macro.StartsWith("NEW_", StringComparison.Ordinal) ? "500" : null,
                "resize_with_background" => "no", _ => null };
            fields[key] = new(key, child.Fields[key].RawValues.ToDictionary(pair => pair.Key,
                pair => child.AvailableDefines.Contains(pair.Key, StringComparer.Ordinal) ? raw : null, StringComparer.Ordinal), false, "This value belongs to the captured Core travel macro.");
        }
        return new(child.Id, child.TagName, child.RawText, child.AvailableDefines, child.OrderByDefine, fields, child.Nodes, true, null)
            { MacroName = macro, MacroInvocationText = child.Nodes.Values.First().TravelInvocation?.Text };
    }
    private static string RenderTravel(WccScenarioStoryChildSpec child, WccScenarioStoryChildSnapshot? old, string define, string newline)
    {
        var macro = child.TravelMacroName ?? old?.MacroName;
        if (!SupportedTravelMacros.Contains(macro, StringComparer.Ordinal)) throw new InvalidOperationException("Choose one supported Core Story travel macro.");
        string Coordinate(string key)
        {
            var raw = child.Fields.GetValueOrDefault(key) is { IsTouched: true } field ? FieldValue(field, define) : old?.Fields[key].RawValues.GetValueOrDefault(define);
            if (raw is null || !int.TryParse(raw, out _)) throw new InvalidOperationException("A Core travel marker needs both literal integer coordinates.");
            return raw;
        }
        return TravelMarkerPrefix + ChildId(child) + newline + "{" + macro + " " + Coordinate("x") + " " + Coordinate("y") + "}";
    }
}
