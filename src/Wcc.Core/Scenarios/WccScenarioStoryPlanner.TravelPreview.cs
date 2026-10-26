using Wcc.Core.Library;
using Wcc.Core.Wml;

namespace Wcc.Core.Scenarios;

public static partial class WccScenarioStoryPlanner
{
    private static string? CapturedTravelKey(WmlStaticScalarOwner owner)
    {
        var frame = owner.OpeningExpansionStack.LastOrDefault();
        if (frame is null || !SupportedTravelMacros.Contains(frame.MacroName, StringComparer.Ordinal)
            || frame.RequiredParameters is null || !frame.RequiredParameters.SequenceEqual(new[] { "X", "Y" })
            || frame.OptionalParameters is not { Count: 0 } || frame.InvocationOrigins.Count == 0
            || !frame.Definition.Source.Path.Replace('\\', '/').EndsWith("/macros/image-utils.cfg", StringComparison.OrdinalIgnoreCase)) return null;
        var structure = WmlAuthoredStructureScanner.Scan(frame.Definition.Text);
        if (structure.Problem is not null || structure.Directives.Any(directive => directive.Name is not ("define" or "enddef"))
            || WmlEditorAnalysis.Build(frame.Definition.Text).Tokens.Where(token => token.Kind == WmlEditorTokenKind.MacroInvocation)
                .Any(token => frame.Definition.Text.Substring(token.StartOffset, token.Length) is not ("{X}" or "{Y}"))) return null;
        return "captured-travel-" + WccContentHash.ComputeText(string.Join("|", owner.OpeningExpansionStack.Select(item => item.MacroName
            + ":" + item.Definition.Source.Path + ":" + item.Definition.StartOffset + ":" + item.Definition.Text
            + ":" + string.Join(",", item.InvocationOrigins.Select(origin => origin.Source.Path + ":" + origin.StartOffset + ":" + origin.Text)))))[..32];
    }

    private static bool CapturedTravelGroupMatches(IReadOnlyList<WmlStaticScalarOwner> output)
    {
        if (output.Count == 0 || output[0].OpeningExpansionStack.LastOrDefault() is not { } frame) return false;
        var files = frame.MacroName switch
        {
            "NEW_REST" => new[] { "misc/flag-red.png", "misc/flag-red2.png", "misc/flag-red.png", "misc/flag-red2.png", "misc/flag-red.png" },
            "NEW_BATTLE" => new[] { "misc/new-battle2.png", "misc/new-battle.png", "misc/new-battle2.png", "misc/new-battle.png", "misc/new-battle2.png" },
            _ => new[] { TravelPreviewImage(frame.MacroName) ?? "" }
        };
        if (output.Count != files.Length) return false;
        var x = output[0].GetKnownValue("x"); var y = output[0].GetKnownValue("y");
        if (!int.TryParse(x, out _) || !int.TryParse(y, out _)) return false;
        for (var index = 0; index < output.Count; index++)
        {
            var image = output[index]; var newMarker = frame.MacroName.StartsWith("NEW_", StringComparison.Ordinal);
            if (image.TagName != "image" || image.IsAmended || image.HasUnresolvedContributions || image.DirectChildren.Count != 0
                || image.Fields.Count != (newMarker ? 5 : 4) || image.GetKnownValue("x") != x || image.GetKnownValue("y") != y
                || WccScenarioFieldValueCodec.DecodeLiteral(image.GetKnownValue("file")) != files[index]
                || WccScenarioFieldValueCodec.DecodeLiteral(image.GetKnownValue("centered")) != "yes"
                || newMarker && WccScenarioFieldValueCodec.DecodeLiteral(image.GetKnownValue("delay")) != "500") return false;
        }
        return true;
    }
}
