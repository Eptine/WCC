using Wcc.Core.Library;
using Wcc.Core.Wml;

namespace Wcc.Core.Scenarios;

internal static partial class WccScenarioFieldStaticReviewer
{
    private static void ValidateStoryTravelNodes(WccStoryLiteralNode expected, WmlStaticScalarOwner actual, WccStaticSourceCaptureResult capture,
        WmlStaticExpansionFrame? outerDefinition = null)
    {
        foreach (var child in expected.Children)
        {
            if (child.TravelMacroName is { } name && child.TravelInvocation is { } invocation)
            {
                var depth = outerDefinition is null ? 1 : 2;
                var output = actual.DirectChildren.Where(node => node.OpeningExpansionStack.Count == depth
                    && node.OpeningExpansionStack[^1].MacroName == name && node.OpeningExpansionStack[^1].InvocationOrigins.Count == 1
                    && SameStoryRange(node.OpeningExpansionStack[^1].InvocationOrigins[0], invocation)).ToArray();
                var files = name switch
                {
                    "NEW_REST" => new[] { "misc/flag-red.png", "misc/flag-red2.png", "misc/flag-red.png", "misc/flag-red2.png", "misc/flag-red.png" },
                    "NEW_BATTLE" => new[] { "misc/new-battle2.png", "misc/new-battle.png", "misc/new-battle2.png", "misc/new-battle.png", "misc/new-battle2.png" },
                    _ => new[] { WccScenarioStoryPlanner.TravelPreviewImage(name) ?? throw new InvalidOperationException("Unsupported Story travel macro.") }
                };
                if (output.Length != files.Length) throw new InvalidOperationException("A Core travel invocation has a different expanded image count.");
                for (var index = 0; index < output.Length; index++)
                {
                    var image = output[index]; var frame = image.OpeningExpansionStack[^1];
                    if (outerDefinition is not null)
                    {
                        var parent = image.OpeningExpansionStack[0];
                        if (parent.MacroName != outerDefinition.MacroName || !SameStoryRange(parent.Definition, outerDefinition.Definition)
                            || parent.InvocationOrigins.Count != 1 || outerDefinition.InvocationOrigins.Count != 1
                            || !SameStoryRange(parent.InvocationOrigins[0], outerDefinition.InvocationOrigins[0]))
                            throw new InvalidOperationException("A nested Core travel marker has a different Story definition caller.");
                    }
                    var roots = WccStaticSourceCapture.GetCapturedHelperSourceRoots(capture).Where(root => root.IsCore
                        && PathComparer.Equals(Path.GetFullPath(Path.Combine(root.RootPath, "macros", "image-utils.cfg")), frame.Definition.Source.Path)).ToArray();
                    if (roots.Length != 1 || frame.RequiredParameters is null || !frame.RequiredParameters.SequenceEqual(new[] { "X", "Y" })
                        || frame.OptionalParameters is null || frame.OptionalParameters.Count != 0 || image.TagName != "image" || image.IsAmended
                        || image.HasUnresolvedContributions || image.DirectChildren.Count != 0 || !capture.Sources.Any(source => WccScenarioStoryPlanner.SameStorySource(source, frame.Definition.Source)))
                        throw new InvalidOperationException("Story travel editing needs the exact active captured Core X/Y image macro.");
                    var structure = WmlAuthoredStructureScanner.Scan(frame.Definition.Text);
                    if (structure.Problem is not null || structure.Directives.Any(directive => directive.Name is not ("define" or "enddef"))
                        || WmlEditorAnalysis.Build(frame.Definition.Text).Tokens.Where(token => token.Kind == WmlEditorTokenKind.MacroInvocation)
                            .Any(token => frame.Definition.Text.Substring(token.StartOffset, token.Length) is not ("{X}" or "{Y}")))
                        throw new InvalidOperationException("A Story travel definition has stateful directives or other helper calls outside its pure Core image contract.");
                    var wanted = new Dictionary<string, string>(StringComparer.Ordinal)
                    { ["x"] = child.Fields["x"].Value.Text, ["y"] = child.Fields["y"].Value.Text, ["file"] = files[index], ["centered"] = "yes" };
                    if (name.StartsWith("NEW_", StringComparison.Ordinal)) wanted["delay"] = "500";
                    if (image.Fields.Count != wanted.Count || wanted.Any(pair => WccScenarioFieldValueCodec.DecodeLiteral(image.GetKnownValue(pair.Key)) != pair.Value))
                        throw new InvalidOperationException("A Story travel macro differs from its complete native image/coordinate contract.");
                }
                continue;
            }
            var nested = actual.DirectChildren.SingleOrDefault(node => node.OpeningExpansionStack.Count == (outerDefinition is null ? 0 : 1) && node.OpeningOrigin is { } origin
                && SameStoryRangeSource(origin, child.Range.Source) && origin.StartOffset == child.Range.StartOffset);
            if (nested is not null) ValidateStoryTravelNodes(child, nested, capture, outerDefinition);
        }
    }
}
