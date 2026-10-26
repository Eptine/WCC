using Wcc.Core.Wml;

namespace Wcc.Core.Units;

/// <summary>
/// One picker/preview projection. SourceDocument remains the editable source
/// block when one exists; PreviewDocument is the preprocessed, effective WML
/// after gender inheritance has been applied.
/// </summary>
public sealed record UnitAnimationPreviewProjection(
    int SourceLineIndex,
    int SubOrder,
    UnitAnimationDocument SourceDocument,
    UnitAnimationDocument PreviewDocument,
    int? SourceDocumentIndex,
    WmlMacroExpansionResult MacroExpansion,
    WmlMacroExpansionProvenance? MacroProvenance,
    UnitGenderVariantKind? VariantKind = null,
    int? VariantDocumentIndex = null,
    int? VariantAnimationIndex = null);

/// <summary>
/// Builds the effective animation view of a unit without changing its source.
/// It uses the shared macro service for every source fragment and the shared
/// WML merge engine for Wesnoth's default [male]/[female] inherit=yes behavior.
/// </summary>
public static class UnitAnimationPreviewProjectionBuilder
{
    public static IReadOnlyList<UnitAnimationPreviewProjection> Build(
        UnitTypeDocument unit,
        IWmlMacroExpansionService macroExpansionService,
        IEnumerable<WmlMacroExpansionDefinition>? localDefinitions = null,
        IWmlMacroExpansionDefinitionResolver? fallbackResolver = null)
    {
        if (unit is null) throw new ArgumentNullException(nameof(unit));
        if (macroExpansionService is null) throw new ArgumentNullException(nameof(macroExpansionService));

        var definitions = localDefinitions?.ToArray();
        var baseEntries = BuildContainerEntries(
            unit.EditableBlock,
            unit.AnimationDocuments,
            sourceLineOffset: 0,
            macroExpansionService,
            definitions,
            fallbackResolver);
        var result = baseEntries
            .Select(entry => entry.ToProjection())
            .ToList();
        var baseByTag = baseEntries
            .GroupBy(x => x.PreviewDocument.TagName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.ToArray(), StringComparer.OrdinalIgnoreCase);

        for (var variantDocumentIndex = 0;
             variantDocumentIndex < unit.GenderVariantDocuments.Count;
             variantDocumentIndex++)
        {
            var variant = unit.GenderVariantDocuments[variantDocumentIndex];
            var variantEntries = BuildContainerEntries(
                variant.EditableBlock,
                variant.AnimationDocuments,
                variant.Span.StartLineIndex,
                macroExpansionService,
                definitions,
                fallbackResolver);
            var inheritsBase = ReadsAsTrueOrDefault(variant.EditableBlock, "inherit", defaultValue: true);
            var ordinalsByTag = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            foreach (var entry in variantEntries)
            {
                var tagName = entry.PreviewDocument.TagName;
                var tagOrdinal = ordinalsByTag.GetValueOrDefault(tagName);
                ordinalsByTag[tagName] = tagOrdinal + 1;

                if (ReadsAsTrueOrDefault(entry.PreviewDocument.EditableBlock, "__remove", defaultValue: false))
                    continue;

                var previewDocument = entry.PreviewDocument;
                var previewExpansion = entry.MacroExpansion;
                if (inheritsBase &&
                    baseByTag.TryGetValue(tagName, out var matchingBase) &&
                    tagOrdinal < matchingBase.Length)
                {
                    try
                    {
                        var baseEntry = matchingBase[tagOrdinal];
                        var mergedText = WmlConfigMerger.Merge(
                            baseEntry.PreviewDocument.EditableBlock.ToRawText(),
                            entry.PreviewDocument.EditableBlock.ToRawText());
                        previewDocument = UnitTypeImporter.ImportSingleAnimationFromRawText(mergedText);
                        previewExpansion = WmlMacroExpansionResult.Compose(
                            mergedText,
                            baseEntry.MacroExpansion,
                            entry.MacroExpansion);
                    }
                    catch (InvalidOperationException exception)
                    {
                        previewExpansion = WmlMacroExpansionResult.Compose(
                            entry.PreviewDocument.EditableBlock.ToRawText(),
                            entry.MacroExpansion);
                        previewExpansion.Issues.Add(new WmlMacroExpansionIssue(
                            WmlMacroExpansionIssueKind.Unsupported,
                            $"The inherited [{tagName}] preview could not be merged: {exception.Message}"));
                    }
                }

                result.Add(new UnitAnimationPreviewProjection(
                    entry.SourceLineIndex,
                    entry.SubOrder,
                    entry.SourceDocument,
                    previewDocument,
                    null,
                    previewExpansion,
                    entry.MacroProvenance,
                    variant.Kind,
                    variantDocumentIndex,
                    entry.SourceDocumentIndex));
            }
        }

        return result
            .OrderBy(x => x.SourceLineIndex)
            .ThenBy(x => x.SubOrder)
            .ToArray();
    }

    private static IReadOnlyList<ExpandedAnimationEntry> BuildContainerEntries(
        WmlEditableBlock owner,
        IReadOnlyList<UnitAnimationDocument> sourceDocuments,
        int sourceLineOffset,
        IWmlMacroExpansionService macroExpansionService,
        IReadOnlyList<WmlMacroExpansionDefinition>? localDefinitions,
        IWmlMacroExpansionDefinitionResolver? fallbackResolver)
    {
        var result = new List<ExpandedAnimationEntry>();
        for (var sourceDocumentIndex = 0;
             sourceDocumentIndex < sourceDocuments.Count;
             sourceDocumentIndex++)
        {
            var sourceDocument = sourceDocuments[sourceDocumentIndex];
            var expansion = macroExpansionService.Expand(
                sourceDocument.EditableBlock.ToRawText(),
                localDefinitions,
                fallbackResolver);
            var previewDocument = TryImportAnimation(expansion.ExpandedText, sourceDocument);
            if (!string.Equals(
                    previewDocument.EditableBlock.ToRawText(),
                    expansion.ExpandedText,
                    StringComparison.Ordinal))
            {
                expansion = WmlMacroExpansionResult.Compose(
                    previewDocument.EditableBlock.ToRawText(),
                    expansion);
            }

            result.Add(new ExpandedAnimationEntry(
                sourceLineOffset + sourceDocument.Span.StartLineIndex,
                0,
                sourceDocument,
                previewDocument,
                sourceDocumentIndex,
                expansion,
                null));
        }

        foreach (var invocation in macroExpansionService.ExpandDirectChildInvocations(
                     owner,
                     localDefinitions,
                     fallbackResolver))
        {
            if (invocation.Expansion.Expansions.Count == 0)
                continue;

            var newLine = owner.NewLine;
            var wrapper =
                $"[unit_type]{newLine}{invocation.Expansion.ExpandedText}{newLine}[/unit_type]";
            UnitTypeDocument generatedUnit;
            try
            {
                generatedUnit = UnitTypeImporter.ImportSingleUnitFromRawText(wrapper);
            }
            catch (InvalidOperationException)
            {
                continue;
            }

            for (var generatedIndex = 0;
                 generatedIndex < generatedUnit.AnimationDocuments.Count;
                 generatedIndex++)
            {
                var generatedDocument = generatedUnit.AnimationDocuments[generatedIndex];
                var rawText = generatedDocument.EditableBlock.ToRawText();
                var provenanceId = WmlMacroExpansionService.ReadLeadingProvenanceMarker(
                    generatedDocument.EditableBlock.Lines);
                result.Add(new ExpandedAnimationEntry(
                    sourceLineOffset + invocation.SourceLineIndex,
                    generatedIndex,
                    generatedDocument,
                    generatedDocument,
                    null,
                    invocation.Expansion.Project(rawText),
                    invocation.Expansion.FindProvenance(provenanceId)));
            }
        }

        return result
            .OrderBy(x => x.SourceLineIndex)
            .ThenBy(x => x.SubOrder)
            .ToArray();
    }

    private static UnitAnimationDocument TryImportAnimation(
        string expandedText,
        UnitAnimationDocument fallback)
    {
        try
        {
            return UnitTypeImporter.ImportSingleAnimationFromRawText(expandedText);
        }
        catch (InvalidOperationException)
        {
            return fallback;
        }
    }

    private static bool ReadsAsTrueOrDefault(
        WmlEditableBlock owner,
        string key,
        bool defaultValue)
    {
        var values = WmlKeyValueExtractor.Extract(
            new WmlBlock(owner.TagName, owner.ToRawText()),
            new HashSet<string>([key], StringComparer.OrdinalIgnoreCase));
        var raw = values.Known.GetValueOrDefault(key);
        if (string.IsNullOrWhiteSpace(raw))
            return defaultValue;

        var normalized = raw.Trim().Trim('"', '\'').ToLowerInvariant();
        return normalized switch
        {
            "yes" or "true" or "1" or "on" => true,
            "no" or "false" or "0" or "off" => false,
            _ => defaultValue
        };
    }

    private sealed record ExpandedAnimationEntry(
        int SourceLineIndex,
        int SubOrder,
        UnitAnimationDocument SourceDocument,
        UnitAnimationDocument PreviewDocument,
        int? SourceDocumentIndex,
        WmlMacroExpansionResult MacroExpansion,
        WmlMacroExpansionProvenance? MacroProvenance)
    {
        public UnitAnimationPreviewProjection ToProjection()
            => new(
                SourceLineIndex,
                SubOrder,
                SourceDocument,
                PreviewDocument,
                SourceDocumentIndex,
                MacroExpansion,
                MacroProvenance);
    }
}
