using System;
using System.Linq;
using Wcc.Core.Scenarios;
using Wcc.Core.Wml;

namespace Wcc.App.ViewModels.Wcc;

/// <summary>
/// A display-only recovery of one local quoted map_data include. File inclusion
/// fragments the expanded scalar's origins, so its authored presentation is separate
/// from both the resolved terrain grid and the existing field edit-route proof.
/// </summary>
internal sealed record WccInitialMapIncludeDisplay(string Expression, WmlStaticSourceRange Assignment)
{
    public static WccInitialMapIncludeDisplay? Read(WccScenarioFieldRoute? route)
    {
        if (route is not { Key: "map_data", Source.IsAuthored: true, Owner.TagName: "scenario" } ||
            !route.Owner.Fields.TryGetValue("map_data", out var field) ||
            field.WinningContribution is not { CompositeAssignmentKeys.Count: 0 } contribution ||
            contribution.ExpansionStack.Count != 0 || contribution.ValueExpansionStack.Count != 0 ||
            contribution.AssignmentOrigins.FirstOrDefault() is not { } anchor || !SameSource(anchor.Source, route.Source) ||
            !contribution.ValueOrigins.Any(origin => origin.Source.IsAuthored && !SameSource(origin.Source, route.Source))) return null;

        var source = route.Source;
        if (anchor.StartOffset < 0 || anchor.StartOffset > source.Text.Length - anchor.Length ||
            !anchor.Text.StartsWith("map_data", StringComparison.Ordinal)) return null;
        var end = source.Text.IndexOfAny(['\r', '\n'], anchor.StartOffset);
        if (end < 0) end = source.Text.Length;
        if (end - anchor.StartOffset > 128 * 1024) return null;
        var line = source.Text[anchor.StartOffset..end];
        var syntax = WmlEditorAnalysis.Build(line);
        var attribute = syntax.Tokens.FirstOrDefault(token => token.Kind == WmlEditorTokenKind.AttributeName && token.StartOffset == 0);
        if (attribute is null || line[..attribute.Length] != "map_data") return null;
        var valueStart = attribute.Length;
        while (valueStart < line.Length && line[valueStart] is ' ' or '\t') valueStart++;
        if (valueStart >= line.Length || line[valueStart++] != '=') return null;
        while (valueStart < line.Length && line[valueStart] is ' ' or '\t') valueStart++;
        if (syntax.QuotedRegions.Count != 1 || syntax.QuotedRegions[0] is not { } quoted ||
            quoted.StartOffset != valueStart || quoted.EndOffsetExclusive <= valueStart + 1 ||
            line[quoted.EndOffsetExclusive - 1] != '"') return null;
        var suffix = line[quoted.EndOffsetExclusive..];
        if (!string.IsNullOrWhiteSpace(suffix) && !suffix.TrimStart().StartsWith('#')) return null;
        var included = line[(valueStart + 1)..(quoted.EndOffsetExclusive - 1)];
        if (syntax.References.Count != 1) return null;
        var reference = syntax.References[0];
        if (reference.Kind != WmlEditorReferenceKind.Include ||
            !WmlMacroInvocationSyntaxScanner.TryParseSingle(included, out var invocation) || invocation!.Arguments.Count != 0 ||
            reference.StartOffset != valueStart + 1 || reference.Length != included.Length) return null;
        var filename = invocation.Name.StartsWith("./", StringComparison.Ordinal) ? invocation.Name[2..] : invocation.Name;
        if (!WmlResourceReferenceSyntax.ParseFilename(filename).IsComplete) return null;
        return new(line[valueStart..quoted.EndOffsetExclusive], new(source, anchor.StartOffset, quoted.EndOffsetExclusive));
    }

    private static bool SameSource(WmlStaticSource left, WmlStaticSource right) => left.IsAuthored && right.IsAuthored &&
        left.ContentHash == right.ContentHash && string.Equals(left.Path, right.Path,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}
