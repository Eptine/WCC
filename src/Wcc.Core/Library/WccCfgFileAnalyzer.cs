using Wcc.Core.Campaigns;
using Wcc.Core.Presets;
using Wcc.Core.References;
using Wcc.Core.Scenarios;
using Wcc.Core.Units;
using Wcc.Core.Wml;

namespace Wcc.Core.Library;

/// <summary>
/// First shared WML analysis pass. A caller reads a cfg file once and gives the same text
/// to this analyzer; existing focused importers are reused in memory. This removes the need
/// for each catalog builder to enumerate and read the source tree independently.
/// </summary>
public sealed class WccCfgFileAnalyzer
{
    public WccFileAnalysis Analyze(WccSourceFileRecord sourceFile, string text)
    {
        if (sourceFile is null) throw new ArgumentNullException(nameof(sourceFile));
        if (text is null) throw new ArgumentNullException(nameof(text));

        var actualHash = WccContentHash.ComputeText(text);
        var analysis = new WccFileAnalysis
        {
            SourceFileId = sourceFile.Id,
            ContentHash = actualHash
        };

        if (!string.Equals(sourceFile.ContentHash, actualHash, StringComparison.OrdinalIgnoreCase))
        {
            analysis.Diagnostics.Add(new WccDiagnosticRecord
            {
                Code = "library.file_hash_mismatch",
                Message = "The supplied source text does not match the file record content hash.",
                Severity = WccDiagnosticSeverity.Warning,
                SourceFileId = sourceFile.Id
            });
        }

        var context = new AnalysisContext(sourceFile, text, analysis);

        AnalyzeSafely(context, "campaigns", () => AnalyzeCampaign(context));
        AnalyzeSafely(context, "scenarios", () => AnalyzeScenarios(context));
        AnalyzeSafely(context, "unit_types", () => AnalyzeUnitTypes(context));
        AnalyzeSafely(context, "macro_definitions", () => AnalyzeMacroDefinitions(context));
        AnalyzeSafely(context, "ability_special_registries", () => AnalyzeAbilitySpecialRegistries(context));
        AnalyzeSafely(context, "movement_types", () => AnalyzeMovementTypes(context));
        AnalyzeSafely(context, "traits", () => AnalyzeTraits(context));
        AnalyzeSafely(context, "races", () => AnalyzeRaces(context));
        AnalyzeSafely(context, "terrain_types", () => AnalyzeTerrainTypes(context));
        AnalyzeSafely(context, "items", () => AnalyzeItems(context));
        AnalyzeSafely(context, "macro_invocations", () => AnalyzeMacroInvocations(context));

        return analysis;
    }

    private static void AnalyzeCampaign(AnalysisContext context)
    {
        CampaignDocument document;
        try
        {
            document = CampaignImporter.ImportFromText(
                context.Text,
                context.SourceFile.StoragePath);
        }
        catch (InvalidOperationException)
        {
            // Most cfg files do not contain a campaign declaration.
            return;
        }

        var campaign = document.Campaign;
        var summary = new Dictionary<string, string>(StringComparer.Ordinal);
        AddSummary(summary, "name_wml", campaign.NameWml);
        AddSummary(summary, "abbrev_wml", campaign.AbbrevWml);
        AddSummary(summary, "define", campaign.Define);
        AddSummary(summary, "first_scenario", campaign.FirstScenario);
        AddSummary(summary, "icon", campaign.Icon);
        AddSummary(summary, "image", campaign.Image);
        AddSummary(summary, "background", campaign.Background);
        AddSummary(summary, "description_wml", campaign.DescriptionWml);
        AddSummary(summary, "year", campaign.Year);
        if (campaign.Rank is not null)
        {
            summary["rank"] = campaign.Rank.Value.ToString(
                System.Globalization.CultureInfo.InvariantCulture);
        }

        var span = context.CreateOffsetSpan(
            document.CampaignBlockSpan.StartIndex,
            document.CampaignBlockSpan.EndIndexExclusive);
        var entity = context.AddEntity(
            WccEntityTypes.Campaign,
            campaign.Id,
            campaign.NameWml ?? campaign.Id,
            document.CampaignBlockSpan.RawText,
            span,
            isExportRootCandidate: true,
            summary);

        context.EntityLineRanges.Add(new EntityLineRange(
            entity.Id,
            span.StartLine ?? 0,
            span.EndLineExclusive ?? int.MaxValue));

        if (!string.Equals(campaign.FirstScenario, "null", StringComparison.OrdinalIgnoreCase))
        {
            context.AddSymbolReference(
                entity.Id,
                WccReferenceTypes.CampaignFirstScenario,
                campaign.FirstScenario);
        }

        foreach (var assetReference in document.AssetReferences)
            context.AddAssetReference(entity.Id, assetReference);

        foreach (var includeReference in document.IncludeReferences)
        {
            var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["include_kind"] = includeReference.Kind.ToString()
            };
            if (!string.IsNullOrWhiteSpace(includeReference.ResolvedDirectoryPath))
                metadata["resolved_directory"] = includeReference.ResolvedDirectoryPath;

            context.AddReference(
                entity.Id,
                WccReferenceTypes.Include,
                "{" + includeReference.RawIncludePath + "}",
                includeReference.RawIncludePath,
                sourceSpan: null,
                WccReferenceConfidence.High,
                metadata);
        }
    }

    private static void AnalyzeScenarios(AnalysisContext context)
    {
        foreach (var document in ScenarioImporter.ImportAllFromText(context.Text))
        {
            var scenario = document.Scenario;
            var summary = new Dictionary<string, string>(StringComparer.Ordinal);
            AddSummary(summary, "next_scenario", scenario.NextScenario);
            AddSummary(summary, "map_file", scenario.MapFile);
            AddSummary(summary, "map_data", scenario.MapData);
            AddSummary(summary, "random_start_time", scenario.RandomStartTime);
            AddSummary(summary, "music", scenario.Music);
            AddSummary(summary, "victory_when_enemies_defeated", scenario.VictoryWhenEnemiesDefeated);
            if (scenario.Turns is not null)
                summary["turns"] = scenario.Turns.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (scenario.TurnAt is not null)
                summary["turn_at"] = scenario.TurnAt.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
            summary["side_count"] = scenario.Sides.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
            summary["story_count"] = scenario.Stories.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);

            var span = context.CreateOffsetSpan(
                document.BlockSpan.StartIndex,
                document.BlockSpan.EndIndexExclusive);
            var entity = context.AddEntity(
                WccEntityTypes.Scenario,
                scenario.Id,
                scenario.Id,
                document.BlockSpan.RawText,
                span,
                isExportRootCandidate: true,
                summary);

            context.EntityLineRanges.Add(new EntityLineRange(
                entity.Id,
                span.StartLine ?? 0,
                span.EndLineExclusive ?? int.MaxValue));

            if (!string.Equals(scenario.NextScenario, "null", StringComparison.OrdinalIgnoreCase))
            {
                context.AddSymbolReference(
                    entity.Id,
                    WccReferenceTypes.ScenarioNextScenario,
                    scenario.NextScenario);
            }

            foreach (var side in scenario.Sides)
            {
                context.AddSymbolReference(entity.Id, WccReferenceTypes.UnitType, side.Type);
                foreach (var recruitId in ParseLiteralUnitIds(side.Recruit))
                    context.AddSymbolReference(entity.Id, WccReferenceTypes.UnitType, recruitId);
            }

            foreach (var assetReference in document.AssetReferences)
                context.AddAssetReference(entity.Id, assetReference);
        }
    }

    private static void AnalyzeUnitTypes(AnalysisContext context)
    {
        foreach (var doc in UnitTypeImporter.ImportAllFromText(context.Text))
        {
            var summary = new Dictionary<string, string>(StringComparer.Ordinal);
            AddSummary(summary, "name_wml", doc.Unit.NameWml);
            AddSummary(summary, "race", doc.Unit.Race);
            AddSummary(summary, "movement_type", doc.Unit.MovementType);
            AddSummary(summary, "image", doc.Unit.Image);
            AddSummary(summary, "profile", doc.Unit.Profile);
            AddSummary(summary, "small_profile", doc.Unit.SmallProfile);
            AddSummary(summary, "gender", doc.Unit.Gender);
            AddSummary(summary, "advances_to", doc.Unit.AdvancesTo);
            var baseUnitIds = doc.Unit.BaseUnits
                .Select(x => x.Id)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(x => x!.Trim())
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            if (baseUnitIds.Length > 0)
                summary["base_unit"] = string.Join(",", baseUnitIds);

            var span = context.CreateOffsetSpan(
                doc.BlockSpan.StartIndex,
                doc.BlockSpan.EndIndexExclusive);

            var entity = context.AddEntity(
                WccEntityTypes.UnitType,
                doc.Unit.Id,
                doc.Unit.NameWml ?? doc.Unit.Id,
                doc.BlockSpan.RawText,
                span,
                isExportRootCandidate: true,
                summary);

            context.EntityLineRanges.Add(new EntityLineRange(
                entity.Id,
                span.StartLine ?? 0,
                span.EndLineExclusive ?? int.MaxValue));

            context.AddSymbolReference(entity.Id, WccReferenceTypes.Race, doc.Unit.Race);
            context.AddSymbolReference(entity.Id, WccReferenceTypes.MovementType, doc.Unit.MovementType);

            foreach (var baseUnitId in baseUnitIds)
                context.AddSymbolReference(entity.Id, WccReferenceTypes.BaseUnit, baseUnitId);

            foreach (var nextUnitId in ParseLiteralUnitIds(doc.Unit.AdvancesTo))
            {
                if (!string.Equals(nextUnitId, "null", StringComparison.OrdinalIgnoreCase))
                    context.AddSymbolReference(entity.Id, WccReferenceTypes.AdvancesTo, nextUnitId);
            }

            foreach (var assetReference in doc.AssetReferences)
                context.AddAssetReference(entity.Id, assetReference);
        }
    }

    private static IReadOnlyList<string> ParseLiteralUnitIds(string? rawValue)
    {
        // Importers retain authored WML. Decode the complete literal before
        // splitting its CSV; quotes belong to the value, never to the first/last ID.
        // The shared observer rejects macros, variables and unsupported expressions.
        return WmlUnitReferenceProjection.TryLiteral(rawValue, out var literal)
            ? UnitAdvancesToValue.ParseIds(literal)
            : Array.Empty<string>();
    }

    private static void AnalyzeMacroDefinitions(AnalysisContext context)
    {
        foreach (var definition in WmlMacroDefinitionScanner.Scan(context.Text))
        {
            var fingerprint = WmlMacroDefinitionBodyAnalyzer.Analyze(definition.RawText);
            var summary = new Dictionary<string, string>(StringComparer.Ordinal);

            if (fingerprint.DirectTagNames.Count > 0)
                summary["direct_tags"] = string.Join(",", fingerprint.DirectTagNames);

            if (fingerprint.DirectInvokedMacroNames.Count > 0)
                summary["direct_macro_invocations"] = string.Join(",", fingerprint.DirectInvokedMacroNames);

            var span = context.FindRawTextSpan(
                           WccEntityTypes.MacroDefinition,
                           definition.RawText)
                       ?? new WccSourceSpan
                       {
                           StartLine = definition.StartLineIndex,
                           EndLineExclusive = definition.EndLineIndexExclusive
                       };

            var entity = context.AddEntity(
                WccEntityTypes.MacroDefinition,
                definition.Name,
                definition.Name,
                definition.RawText,
                span,
                isExportRootCandidate: true,
                summary);

            context.MacroDefinitionLineRanges.Add(new EntityLineRange(
                entity.Id,
                definition.StartLineIndex,
                definition.EndLineIndexExclusive));

            foreach (var invokedMacroName in fingerprint.DirectInvokedMacroNames)
            {
                context.AddReference(
                    entity.Id,
                    WccReferenceTypes.MacroInvocation,
                    "{" + invokedMacroName + "}",
                    invokedMacroName,
                    span,
                    WccReferenceConfidence.High);
            }
        }
    }

    private static void AnalyzeAbilitySpecialRegistries(AnalysisContext context)
    {
        foreach (var container in WesnothAbilitySpecialRegistrySourceParser.FindContainers(context.Text))
        {
            var containerType = container.Kind == WesnothAbilitySpecialRegistryKind.Ability
                ? WccEntityTypes.AbilityRegistry
                : WccEntityTypes.WeaponSpecialRegistry;
            var definitionType = container.Kind == WesnothAbilitySpecialRegistryKind.Ability
                ? WccEntityTypes.AbilityDefinition
                : WccEntityTypes.WeaponSpecialDefinition;
            var containerSummary = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["registry_kind"] = container.Kind.ToString(),
                ["conditional"] = container.IsConditional.ToString()
            };
            var containerSpan = context.CreateLineSpan(
                container.StartLine,
                container.EndLineExclusive);
            var containerEntity = context.AddEntity(
                containerType,
                exportSymbol: null,
                container.Kind == WesnothAbilitySpecialRegistryKind.Ability
                    ? "Ability registry"
                    : "Weapon-special registry",
                container.RawText,
                containerSpan,
                isExportRootCandidate: false,
                containerSummary);

            context.EntityLineRanges.Add(new EntityLineRange(
                containerEntity.Id,
                container.StartLine,
                container.EndLineExclusive));

            var parsed = WesnothAbilitySpecialRegistrySourceParser.ParseDefinitions(
                container.Kind,
                container.RawText);
            foreach (var definition in parsed.Definitions)
            {
                var summary = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["registry_id"] = definition.RegistryId,
                    ["tag_name"] = definition.TagName,
                    ["conditional"] = (container.IsConditional || definition.IsConditional).ToString()
                };
                AddSummary(summary, "id", definition.Id);
                AddSummary(summary, "unique_id", definition.UniqueId);
                AddSummary(summary, "name_wml", definition.DisplayNameWml);

                context.AddEntity(
                    definitionType,
                    definition.RegistryId,
                    definition.DisplayNameWml ?? definition.RegistryId,
                    definition.RawText,
                    context.CreateLineSpan(
                        container.StartLine + definition.StartLine,
                        container.StartLine + definition.EndLineExclusive),
                    isExportRootCandidate: false,
                    summary,
                    containerEntity.Id);
            }

            foreach (var issue in parsed.Issues)
            {
                context.Analysis.Diagnostics.Add(new WccDiagnosticRecord
                {
                    Code = issue.Code,
                    Message = issue.Message,
                    Severity = WccDiagnosticSeverity.Warning,
                    SourceFileId = context.SourceFile.Id,
                    SourceEntityId = containerEntity.Id,
                    SourceSpan = issue.StartLine is { } relativeLine
                        ? context.CreateLineSpan(
                            container.StartLine + relativeLine,
                            container.StartLine + relativeLine + 1)
                        : containerSpan
                });
            }
        }
    }

    private static void AnalyzeMovementTypes(AnalysisContext context)
    {
        foreach (var preset in MovementTypePresetImporter.ImportAllFromText(context.Text))
        {
            context.AddPresetEntity(
                WccEntityTypes.MovementType,
                preset.Name,
                preset.Name,
                preset.RawText);
        }
    }

    private static void AnalyzeTraits(AnalysisContext context)
    {
        foreach (var preset in TraitPresetImporter.ImportAllFromText(context.Text))
        {
            var summary = new Dictionary<string, string>(StringComparer.Ordinal);
            AddSummary(summary, "display_name", preset.DisplayName);

            context.AddPresetEntity(
                WccEntityTypes.Trait,
                preset.Id,
                preset.DisplayName ?? preset.Id,
                preset.RawText,
                summary);
        }
    }

    private static void AnalyzeRaces(AnalysisContext context)
    {
        foreach (var preset in RacePresetImporter.ImportAllFromText(context.Text))
        {
            var summary = new Dictionary<string, string>(StringComparer.Ordinal);
            AddSummary(summary, "name", preset.Name);
            AddSummary(summary, "male_name", preset.MaleName);
            AddSummary(summary, "female_name", preset.FemaleName);
            AddSummary(summary, "plural_name", preset.PluralName);
            AddSummary(summary, "description", preset.Description);
            if (preset.NumTraits is not null)
                summary["num_traits"] = preset.NumTraits.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
            AddSummary(summary, "ignore_global_traits", preset.IgnoreGlobalTraits);

            context.AddPresetEntity(
                WccEntityTypes.Race,
                preset.Id,
                preset.Name ?? preset.MaleName ?? preset.FemaleName ?? preset.Id,
                preset.RawText,
                summary);
        }
    }

    private static void AnalyzeTerrainTypes(AnalysisContext context)
    {
        foreach (var preset in TerrainTypePresetImporter.ImportAllFromText(context.Text))
        {
            var summary = new Dictionary<string, string>(StringComparer.Ordinal);
            AddSummary(summary, "name", preset.Name);
            AddSummary(summary, "editor_name", preset.EditorName);
            AddSummary(summary, "string", preset.StringCode);
            AddSummary(summary, "editor_group", preset.EditorGroup);
            AddSummary(summary, "symbol_image", preset.SymbolImage);
            AddSummary(summary, "editor_image", preset.EditorImage);
            AddSummary(summary, "description", preset.Description);

            context.AddPresetEntity(
                WccEntityTypes.TerrainType,
                preset.Id,
                preset.EditorName ?? preset.Name ?? preset.Id,
                preset.RawText,
                summary);
        }
    }

    private static void AnalyzeItems(AnalysisContext context)
    {
        foreach (var preset in ItemPresetImporter.ImportAllFromText(context.Text))
        {
            var summary = new Dictionary<string, string>(StringComparer.Ordinal);
            AddSummary(summary, "name", preset.Name);
            AddSummary(summary, "x", preset.X);
            AddSummary(summary, "y", preset.Y);
            AddSummary(summary, "image", preset.Image);
            AddSummary(summary, "halo", preset.Halo);
            AddSummary(summary, "team_name", preset.TeamName);
            AddSummary(summary, "visible_in_fog", preset.VisibleInFog);
            AddSummary(summary, "submerge", preset.Submerge);
            AddSummary(summary, "z_order", preset.ZOrder);

            context.AddPresetEntity(
                WccEntityTypes.Item,
                exportSymbol: null,
                preset.Name ?? preset.Image ?? preset.Halo ?? "Item",
                preset.RawText,
                summary);
        }
    }

    private static void AnalyzeMacroInvocations(AnalysisContext context)
    {
        foreach (var invocation in WmlMacroInvocationScanner.Scan(context.Text))
        {
            // Macro-body dependencies were attached directly to their definition above.
            if (invocation.IsInsideMacroDefinition ||
                context.MacroDefinitionLineRanges.Any(x => x.Contains(invocation.LineIndex)))
                continue;

            var owner = context.EntityLineRanges
                .FirstOrDefault(x => x.Contains(invocation.LineIndex));

            context.AddReference(
                owner?.EntityId,
                WccReferenceTypes.MacroInvocation,
                invocation.RawInvocation,
                invocation.Name,
                new WccSourceSpan
                {
                    StartOffset = invocation.StartOffset >= 0 ? invocation.StartOffset : null,
                    EndOffsetExclusive = invocation.EndOffsetExclusive >= 0
                        ? invocation.EndOffsetExclusive
                        : null,
                    StartLine = invocation.LineIndex,
                    EndLineExclusive = invocation.RawInvocation.Count(x => x == '\n') +
                                       invocation.LineIndex + 1
                },
                WccReferenceConfidence.High);
        }
    }

    private static void AnalyzeSafely(AnalysisContext context, string analyzerName, Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            context.Analysis.Diagnostics.Add(new WccDiagnosticRecord
            {
                Code = "library.analyzer_failed",
                Message = $"Analyzer '{analyzerName}' failed: {ex.Message}",
                Severity = WccDiagnosticSeverity.Warning,
                SourceFileId = context.SourceFile.Id
            });
        }
    }

    private static void AddSummary(Dictionary<string, string> summary, string key, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
            summary[key] = value;
    }

    private sealed class AnalysisContext
    {
        private readonly Dictionary<WccEntityTypeId, int> _rawSearchOffsets = new();
        private readonly Dictionary<WccEntityTypeId, int> _entityOrdinals = new();
        private readonly Dictionary<(WccEntityId? SourceEntityId, WccReferenceTypeId ReferenceType), int>
            _referenceOrdinals = new();

        public AnalysisContext(
            WccSourceFileRecord sourceFile,
            string text,
            WccFileAnalysis analysis)
        {
            SourceFile = sourceFile;
            Text = text;
            Analysis = analysis;
        }

        public WccSourceFileRecord SourceFile { get; }
        public string Text { get; }
        public WccFileAnalysis Analysis { get; }
        public List<EntityLineRange> EntityLineRanges { get; } = new();
        public List<EntityLineRange> MacroDefinitionLineRanges { get; } = new();

        public WccEntityRecord AddEntity(
            WccEntityTypeId entityType,
            string? exportSymbol,
            string? displayName,
            string rawText,
            WccSourceSpan? sourceSpan,
            bool isExportRootCandidate,
            Dictionary<string, string>? summary = null,
            WccEntityId? parentEntityId = null)
        {
            var entityOrdinal = _entityOrdinals.GetValueOrDefault(entityType);
            _entityOrdinals[entityType] = entityOrdinal + 1;

            var entity = new WccEntityRecord
            {
                Id = WccLibraryScopedIdFactory.CreateEntityId(
                    SourceFile.Id,
                    entityType,
                    entityOrdinal),
                SourceFileId = SourceFile.Id,
                EntityType = entityType,
                ParentEntityId = parentEntityId,
                ExportSymbol = exportSymbol,
                DisplayName = displayName,
                ContentFingerprint = WccContentHash.ComputeText(rawText),
                SourceSpan = sourceSpan,
                IsExportRootCandidate = isExportRootCandidate,
                Summary = summary ?? new Dictionary<string, string>(StringComparer.Ordinal)
            };

            Analysis.Entities.Add(entity);
            return entity;
        }

        public void AddPresetEntity(
            WccEntityTypeId entityType,
            string? exportSymbol,
            string? displayName,
            string rawText,
            Dictionary<string, string>? summary = null)
        {
            AddEntity(
                entityType,
                exportSymbol,
                displayName,
                rawText,
                FindRawTextSpan(entityType, rawText),
                isExportRootCandidate: true,
                summary);
        }

        public void AddSymbolReference(
            WccEntityId sourceEntityId,
            WccReferenceTypeId referenceType,
            string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return;

            AddReference(
                sourceEntityId,
                referenceType,
                value,
                value.Trim(),
                sourceSpan: null,
                WccReferenceConfidence.High);
        }

        public void AddAssetReference(WccEntityId sourceEntityId, WccAssetReference assetReference)
        {
            var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["asset_kind"] = assetReference.Kind.ToString(),
                ["owner_block_kind"] = assetReference.OwnerBlockKind,
                ["source_field"] = assetReference.SourceFieldKey
            };

            if (assetReference.IsTemplatedPath)
                metadata["templated"] = "true";
            if (!string.IsNullOrWhiteSpace(assetReference.TemplatedMacroName))
                metadata["templated_macro_name"] = assetReference.TemplatedMacroName;
            if (!string.IsNullOrWhiteSpace(assetReference.TemplatedMacroRawInvocation))
                metadata["templated_macro_invocation"] = assetReference.TemplatedMacroRawInvocation;
            if (!string.IsNullOrWhiteSpace(assetReference.TemplatedPathSuffix))
                metadata["templated_path_suffix"] = assetReference.TemplatedPathSuffix;

            AddReference(
                sourceEntityId,
                WccReferenceTypes.Resource,
                assetReference.RawValue,
                assetReference.LookupPath,
                sourceSpan: null,
                assetReference.IsTemplatedPath
                    ? WccReferenceConfidence.Medium
                    : WccReferenceConfidence.High,
                metadata);
        }

        public void AddReference(
            WccEntityId? sourceEntityId,
            WccReferenceTypeId referenceType,
            string rawValue,
            string? lookupValue,
            WccSourceSpan? sourceSpan,
            WccReferenceConfidence confidence,
            Dictionary<string, string>? metadata = null)
        {
            var ordinalKey = (sourceEntityId, referenceType);
            var referenceOrdinal = _referenceOrdinals.GetValueOrDefault(ordinalKey);
            _referenceOrdinals[ordinalKey] = referenceOrdinal + 1;

            Analysis.References.Add(new WccReferenceRecord
            {
                Id = WccLibraryScopedIdFactory.CreateReferenceId(
                    SourceFile.Id,
                    sourceEntityId,
                    referenceType,
                    referenceOrdinal),
                SourceFileId = SourceFile.Id,
                SourceEntityId = sourceEntityId,
                ReferenceType = referenceType,
                RawValue = rawValue,
                LookupValue = lookupValue,
                SourceSpan = sourceSpan,
                ResolutionState = WccReferenceResolutionState.Unresolved,
                Confidence = confidence,
                Metadata = metadata ?? new Dictionary<string, string>(StringComparer.Ordinal)
            });
        }

        public WccSourceSpan CreateOffsetSpan(int startOffset, int endOffsetExclusive)
            => new()
            {
                StartOffset = startOffset,
                EndOffsetExclusive = endOffsetExclusive,
                StartLine = CountNewLinesBefore(Text, startOffset),
                EndLineExclusive = CountNewLinesBefore(Text, endOffsetExclusive) + 1
            };

        public WccSourceSpan CreateLineSpan(int startLine, int endLineExclusive)
        {
            var boundedStartLine = Math.Max(0, startLine);
            var boundedEndLine = Math.Max(boundedStartLine, endLineExclusive);
            var startOffset = OffsetAtLine(Text, boundedStartLine);
            var endOffset = OffsetAtLine(Text, boundedEndLine);
            return new WccSourceSpan
            {
                StartOffset = startOffset,
                EndOffsetExclusive = endOffset,
                StartLine = boundedStartLine,
                EndLineExclusive = boundedEndLine
            };
        }

        public WccSourceSpan? FindRawTextSpan(WccEntityTypeId entityType, string rawText)
        {
            var searchStart = _rawSearchOffsets.TryGetValue(entityType, out var previous)
                ? previous
                : 0;

            var start = Text.IndexOf(rawText, searchStart, StringComparison.Ordinal);
            if (start < 0)
                start = Text.IndexOf(rawText, StringComparison.Ordinal);

            if (start < 0)
                return null;

            var end = start + rawText.Length;
            _rawSearchOffsets[entityType] = end;
            return CreateOffsetSpan(start, end);
        }

        private static int CountNewLinesBefore(string text, int offset)
        {
            var boundedOffset = Math.Clamp(offset, 0, text.Length);
            var count = 0;

            for (var i = 0; i < boundedOffset; i++)
            {
                if (text[i] == '\n')
                    count++;
            }

            return count;
        }

        private static int OffsetAtLine(string text, int line)
        {
            if (line <= 0)
                return 0;

            var currentLine = 0;
            for (var index = 0; index < text.Length; index++)
            {
                if (text[index] != '\n')
                    continue;

                currentLine++;
                if (currentLine == line)
                    return index + 1;
            }

            return text.Length;
        }
    }

    private sealed record EntityLineRange(
        WccEntityId EntityId,
        int StartLine,
        int EndLineExclusive)
    {
        public bool Contains(int line) => line >= StartLine && line < EndLineExclusive;
    }
}
