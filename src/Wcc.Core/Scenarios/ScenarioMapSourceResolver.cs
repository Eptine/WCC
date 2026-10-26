using Wcc.Core.Projects;
using Wcc.Core.References;
using Wcc.Core.Wml;

namespace Wcc.Core.Scenarios;

public enum ScenarioMapSourceState
{
    NoMap,
    Embedded,
    ExternalFile,
    Unresolved,
    Ambiguous,
    Unsupported,
    Error
}

public sealed class ScenarioMapSourceResult
{
    public required ScenarioMapSourceState State { get; init; }
    public string? RawValue { get; init; }
    public string? EffectiveValue { get; init; }
    public string? MapText { get; init; }
    public string? ResolvedPath { get; init; }
    public string? SourceDescription { get; init; }
    public WmlResolvedValueResult? ValueResolution { get; init; }
    public WmlMacroExpansionResult? BlockExpansion { get; internal set; }
    public WmlMacroExpansionProvenance? SourceMacro { get; internal set; }
    public List<string> CandidatePaths { get; } = new();
    public List<string> Diagnostics { get; } = new();
    public bool IsResolved => State is ScenarioMapSourceState.Embedded or ScenarioMapSourceState.ExternalFile;
}

/// <summary>
/// Resolves the initial scenario map without executing scenario runtime state.
/// Direct multiline map_data is read from the authoritative block, file include
/// syntax is treated explicitly, and inline or direct-child field-generating
/// macros use the shared macro-resolved value service.
/// </summary>
public sealed class ScenarioMapSourceResolver(
    IWmlMacroExpansionService? macroExpansionService = null)
{
    private readonly IWmlMacroExpansionService _macroExpansionService =
        macroExpansionService ?? new WmlMacroExpansionService();

    public ScenarioMapSourceResult Resolve(
        ScenarioDocument document,
        string? ownerSourcePath,
        string? wesnothInstallPath = null,
        string? mapFileOverride = null,
        string? mapDataOverride = null,
        IEnumerable<WmlMacroExpansionDefinition>? localDefinitions = null,
        IWmlMacroExpansionDefinitionResolver? fallbackResolver = null)
    {
        if (document is null) throw new ArgumentNullException(nameof(document));

        return ResolveCore(
            document,
            ownerSourcePath,
            wesnothInstallPath,
            mapFileOverride,
            mapDataOverride,
            localDefinitions,
            fallbackResolver,
            allowDirectChildMacroProjection: true);
    }

    /// <summary>
    /// Reads map attributes from a captured initial source projection, without re-expanding a
    /// macro catalog or creating an authored document. The caller must bind a complete, fresh
    /// capture and selected context before publishing this read-only result. This provides no
    /// runtime, editing or persistence authority and does not execute engine-side merges.
    /// </summary>
    public ScenarioMapSourceResult ResolveCapturedInitialFields(
        WmlStaticScalarOwner owner,
        WmlStaticSource ownerSource,
        string? wesnothInstallPath = null)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(ownerSource);
        if (!HasCapturedScenarioBoundaries(owner, ownerSource))
            return CapturedMapProblem("The captured map owner does not match exact literal scenario boundaries in the current source.");

        if (WccScenarioFieldContentScope.CheckOwner(owner) is { } contentProblem)
            return CapturedMapProblem(contentProblem);

        var mapFile = owner.ResolveField("map_file");
        var mapData = owner.ResolveField("map_data");
        if (mapFile.State == WmlStaticScalarResolutionState.Unresolved
            || mapData.State == WmlStaticScalarResolutionState.Unresolved)
            return CapturedMapProblem("The captured initial map_file value or map_data value/absence is unresolved in this context.");

        var fileValue = "";
        var dataValue = "";
        if (mapFile.State == WmlStaticScalarResolutionState.Known
                && !TryDecodeCapturedMapLiteral(mapFile.ValueText, out fileValue)
            || mapData.State == WmlStaticScalarResolutionState.Known
                && !TryDecodeCapturedMapLiteral(mapData.ValueText, out dataValue))
            return CapturedMapProblem("The captured map value is not one supported nontranslated literal; runtime variables and unresolved expressions cannot supply a map preview.");

        var hasFile = !string.IsNullOrWhiteSpace(fileValue);
        var hasData = !string.IsNullOrWhiteSpace(dataValue);
        if (hasFile && hasData)
        {
            var ambiguous = new ScenarioMapSourceResult
            {
                State = ScenarioMapSourceState.Ambiguous,
                RawValue = $"map_file={mapFile.ValueText}; map_data={mapData.ValueText}",
                SourceDescription = "Both captured map_file and map_data are set"
            };
            ambiguous.Diagnostics.Add("Both map_file and map_data are present in the captured initial context. WCC will not guess which source defines the initial map.");
            return ambiguous;
        }

        if (hasData)
            return new ScenarioMapSourceResult
            {
                State = ScenarioMapSourceState.Embedded,
                RawValue = mapData.ValueText,
                EffectiveValue = dataValue,
                MapText = dataValue,
                SourceDescription = "Captured initial map_data"
            };

        if (hasFile)
            return ResolveExternalMap(mapFile.ValueText, fileValue, "Captured initial map_file",
                ownerSource.Path, wesnothInstallPath, valueResolution: null);

        // Absence of literal map attributes does not execute a scenario/map generator.
        foreach (var key in new[] { "map_generation", "scenario_generation" })
        {
            var generator = owner.ResolveField(key);
            if (generator.State == WmlStaticScalarResolutionState.Unresolved
                || generator.State == WmlStaticScalarResolutionState.Known
                    && (!TryDecodeCapturedMapLiteral(generator.ValueText, out var value)
                        || !string.IsNullOrWhiteSpace(value)))
                return CapturedMapProblem("The scenario's initial map may be supplied by an engine generator, which is outside the captured source preview.");
        }

        return new ScenarioMapSourceResult
        {
            State = ScenarioMapSourceState.NoMap,
            SourceDescription = "Captured scenario has no initial map_file or map_data"
        };
    }

    private static bool HasCapturedScenarioBoundaries(WmlStaticScalarOwner owner, WmlStaticSource source)
    {
        if (!source.IsAuthored || owner.TagName != "scenario" || owner.Parent is not null
            || owner.OpeningOutputOffset is not { } openingOutput || owner.ClosingOutputOffset is not { } closingOutput
            || owner.OpeningOrigin is not { } opening || owner.ClosingOrigin is not { } closing
            || !MatchesCapturedBoundary(opening, source, "scenario")
            || !MatchesCapturedBoundary(closing, closing.Source, "/scenario")
            || closingOutput <= openingOutput)
            return false;

        // Ordinary file includes inherit their wrapper's frames. Only an actual tag inside
        // an expanded definition is generated; never bind that definition body as the owner.
        // A captured helper may close and reopen the literal owner with [+scenario]. Final
        // known attributes are display evidence; its closing range is not an insertion boundary.
        return !owner.OpeningExpansionStack.Any(frame => DefinitionContains(frame, opening));
    }

    private static bool MatchesCapturedBoundary(WmlStaticSourceRange boundary, WmlStaticSource source, string tag)
    {
        if (!boundary.Source.IsAuthored || !SameCapturedSource(boundary.Source, source)
            || boundary.StartOffset < 0 || boundary.Length < 2
            || boundary.StartOffset > source.Text.Length - boundary.Length)
            return false;
        var text = source.Text.AsSpan(boundary.StartOffset, boundary.Length);
        return text[0] == '[' && text[^1] == ']' && text[1..^1].Trim().SequenceEqual(tag.AsSpan());
    }

    private static bool DefinitionContains(WmlStaticExpansionFrame frame, WmlStaticSourceRange boundary) =>
        SameCapturedSource(frame.Definition.Source, boundary.Source)
        && frame.Definition.StartOffset < boundary.StartOffset + boundary.Length
        && boundary.StartOffset < frame.Definition.StartOffset + frame.Definition.Length;

    private static bool SameCapturedSource(WmlStaticSource left, WmlStaticSource right) =>
        string.Equals(left.Path, right.Path, OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)
        && string.Equals(left.ContentHash, right.ContentHash, StringComparison.Ordinal);

    private static bool TryDecodeCapturedMapLiteral(string? raw, out string value)
    {
        value = "";
        if (raw is null) return false;
        var scalar = raw.Trim();
        if (scalar.IndexOfAny(['{', '}', '$']) >= 0
            || scalar.StartsWith('_') && scalar[1..].TrimStart().StartsWith('"')) return false;
        if (scalar.Length == 0) return true;
        if (scalar[0] != '"')
        {
            if (scalar.IndexOfAny(['"', '\r', '\n', '<', '>']) >= 0
                || scalar.Contains('+') && !int.TryParse(scalar, System.Globalization.NumberStyles.AllowLeadingSign,
                    System.Globalization.CultureInfo.InvariantCulture, out _)) return false;
            value = scalar;
            return true;
        }

        var decoded = new System.Text.StringBuilder();
        for (var index = 1; index < scalar.Length; index++)
        {
            if (scalar[index] != '"') { decoded.Append(scalar[index]); continue; }
            if (index + 1 < scalar.Length && scalar[index + 1] == '"') { decoded.Append('"'); index++; continue; }
            if (index != scalar.Length - 1) return false;
            value = decoded.ToString();
            return true;
        }
        return false;
    }

    private static ScenarioMapSourceResult CapturedMapProblem(string problem)
    {
        var result = new ScenarioMapSourceResult
        {
            State = ScenarioMapSourceState.Unsupported,
            SourceDescription = "Captured initial map source is unavailable"
        };
        result.Diagnostics.Add(problem);
        return result;
    }

    private ScenarioMapSourceResult ResolveCore(
        ScenarioDocument document,
        string? ownerSourcePath,
        string? wesnothInstallPath,
        string? mapFileOverride,
        string? mapDataOverride,
        IEnumerable<WmlMacroExpansionDefinition>? localDefinitions,
        IWmlMacroExpansionDefinitionResolver? fallbackResolver,
        bool allowDirectChildMacroProjection)
    {
        var definitions = localDefinitions?.ToArray();

        var mapFile = ReadCurrentValue(document, "map_file", mapFileOverride);
        var mapData = ReadCurrentValue(document, "map_data", mapDataOverride);
        var hasMapFile = !string.IsNullOrWhiteSpace(mapFile.Value);
        var hasMapData = !string.IsNullOrWhiteSpace(mapData.Value);
        var mapFileShape = WmlTopLevelFieldAnalyzer.Analyze(document.EditableBlock, "map_file");
        var mapDataShape = WmlTopLevelFieldAnalyzer.Analyze(document.EditableBlock, "map_data");

        if (mapFileShape.Kind == WmlTopLevelFieldKind.ConditionalOrMultiple ||
            mapDataShape.Kind == WmlTopLevelFieldKind.ConditionalOrMultiple)
        {
            var conditional = new ScenarioMapSourceResult
            {
                State = ScenarioMapSourceState.Unsupported,
                SourceDescription = "Map source is conditional or occurs multiple times"
            };
            conditional.Diagnostics.Add(
                "map_file or map_data is conditional or occurs multiple times. WCC will not select a map without the missing preprocessor context.");
            return conditional;
        }

        if (hasMapFile && hasMapData)
        {
            var ambiguous = new ScenarioMapSourceResult
            {
                State = ScenarioMapSourceState.Ambiguous,
                RawValue = $"map_file={mapFile.RawValue}; map_data={mapData.RawValue}",
                SourceDescription = "Both map_file and map_data are set"
            };
            ambiguous.Diagnostics.Add(
                "Both map_file and map_data are present. WCC will not guess which source defines the initial map.");
            return ambiguous;
        }

        if (hasMapData)
        {
            return ResolveMapData(
                mapData,
                ownerSourcePath,
                wesnothInstallPath,
                definitions,
                fallbackResolver);
        }

        if (hasMapFile)
        {
            return ResolveMapFile(
                mapFile,
                ownerSourcePath,
                wesnothInstallPath,
                definitions,
                fallbackResolver);
        }

        if (allowDirectChildMacroProjection)
        {
            var generated = ResolveMacroGeneratedMap(
                document,
                ownerSourcePath,
                wesnothInstallPath,
                definitions,
                fallbackResolver);
            if (generated is not null)
                return generated;
        }

        if (mapFile.IsUnsafeOrConditional || mapData.IsUnsafeOrConditional)
        {
            var unsupported = new ScenarioMapSourceResult
            {
                State = ScenarioMapSourceState.Unsupported,
                SourceDescription = "Map source is generated by conditional or top-level WML"
            };
            unsupported.Diagnostics.Add(
                "No direct map_file or map_data value is available, but the scenario contains WML that may generate one.");
            return unsupported;
        }

        return new ScenarioMapSourceResult
        {
            State = ScenarioMapSourceState.NoMap,
            SourceDescription = "Scenario has no direct map_file or map_data"
        };
    }

    private ScenarioMapSourceResult? ResolveMacroGeneratedMap(
        ScenarioDocument document,
        string? ownerSourcePath,
        string? wesnothInstallPath,
        IReadOnlyList<WmlMacroExpansionDefinition>? localDefinitions,
        IWmlMacroExpansionDefinitionResolver? fallbackResolver)
    {
        var candidates = new List<ScenarioMapSourceResult>();
        foreach (var invocation in _macroExpansionService.ExpandDirectChildInvocations(
                     document.EditableBlock,
                     localDefinitions,
                     fallbackResolver))
        {
            if (invocation.Expansion.Expansions.Count == 0)
                continue;

            var newLine = document.EditableBlock.NewLine;
            var wrapper =
                $"[scenario]{newLine}{invocation.Expansion.ExpandedText}{newLine}[/scenario]";
            ScenarioDocument generatedDocument;
            try
            {
                generatedDocument = ScenarioImporter.ImportAllFromText(wrapper).Single();
            }
            catch (InvalidOperationException)
            {
                continue;
            }

            // Field safety is deliberately conservative for editing: any
            // unresolved top-level macro makes every absent field unsafe to
            // synthesize. That does not mean an unrelated macro generated a
            // map. For source discovery, require this particular expansion to
            // contain an actual top-level map key (plain or conditional).
            var generatedMapFile = WmlTopLevelFieldAnalyzer.Analyze(
                generatedDocument.EditableBlock,
                "map_file");
            var generatedMapData = WmlTopLevelFieldAnalyzer.Analyze(
                generatedDocument.EditableBlock,
                "map_data");
            if (generatedMapFile.Kind == WmlTopLevelFieldKind.None &&
                generatedMapData.Kind == WmlTopLevelFieldKind.None)
            {
                continue;
            }

            ScenarioMapSourceResult candidate;
            if (WmlPreprocessorScanner.FindEnclosingDirectChildRegion(
                    document.EditableBlock,
                    invocation.SourceLineIndex) is not null)
            {
                candidate = new ScenarioMapSourceResult
                {
                    State = ScenarioMapSourceState.Unsupported,
                    SourceDescription = "Map-producing macro is conditionally invoked"
                };
                candidate.Diagnostics.Add(
                    "A macro that emits map_file or map_data is inside a preprocessor conditional. WCC will not choose a branch without that context.");
            }
            else
            {
                candidate = ResolveCore(
                    generatedDocument,
                    ownerSourcePath,
                    wesnothInstallPath,
                    mapFileOverride: null,
                    mapDataOverride: null,
                    localDefinitions,
                    fallbackResolver,
                    allowDirectChildMacroProjection: false);
            }
            candidate.BlockExpansion = invocation.Expansion;
            candidate.SourceMacro = invocation.Expansion.Expansions
                .Where(x => x.Depth == 0)
                .OrderBy(x => x.InvocationLineIndex)
                .FirstOrDefault() ?? invocation.Expansion.Expansions[0];
            candidate.Diagnostics.Insert(
                0,
                $"Map source expanded from {{{candidate.SourceMacro.MacroName}}} " +
                $"({candidate.SourceMacro.SourceLabel}).");
            candidate.Diagnostics.AddRange(invocation.Expansion.Issues.Select(x => x.Message));
            candidates.Add(candidate);
        }

        if (candidates.Count == 0)
            return null;
        if (candidates.Count == 1)
            return candidates[0];

        var ambiguous = new ScenarioMapSourceResult
        {
            State = ScenarioMapSourceState.Ambiguous,
            SourceDescription = "Multiple scenario macros generate map sources"
        };
        ambiguous.Diagnostics.Add(
            "Multiple direct scenario macros generate map_file or map_data. WCC will not guess which map is active.");
        ambiguous.Diagnostics.AddRange(candidates.SelectMany(x => x.Diagnostics));
        return ambiguous;
    }

    private ScenarioMapSourceResult ResolveMapData(
        DirectValue mapData,
        string? ownerSourcePath,
        string? wesnothInstallPath,
        IEnumerable<WmlMacroExpansionDefinition>? localDefinitions,
        IWmlMacroExpansionDefinitionResolver? fallbackResolver)
    {
        var value = mapData.Value!;
        if (TryParseFileInclude(value, out var includePath))
        {
            return ResolveExternalMap(
                mapData.RawValue,
                includePath!,
                "map_data file include",
                ownerSourcePath,
                wesnothInstallPath,
                valueResolution: null);
        }

        WmlResolvedValueResult? valueResolution = null;
        if (value.Contains('{'))
        {
            valueResolution = _macroExpansionService.ResolveValue(
                value,
                localDefinitions,
                fallbackResolver);
            if (valueResolution.State != WmlResolvedValueState.Resolved)
                return FromUnresolvedValue(mapData.RawValue, valueResolution, "map_data");

            value = Unquote(valueResolution.EffectiveValue);
            if (TryParseFileInclude(value, out includePath))
            {
                return ResolveExternalMap(
                    mapData.RawValue,
                    includePath!,
                    "macro-resolved map_data file include",
                    ownerSourcePath,
                    wesnothInstallPath,
                    valueResolution);
            }
        }

        if (value.Contains('$'))
        {
            var unsupported = new ScenarioMapSourceResult
            {
                State = ScenarioMapSourceState.Unsupported,
                RawValue = mapData.RawValue,
                EffectiveValue = value,
                ValueResolution = valueResolution,
                SourceDescription = "Unsupported map_data expression"
            };
            unsupported.Diagnostics.Add(
                "map_data contains a runtime variable expression; WCC cannot know the initial grid without runtime context.");
            return unsupported;
        }

        return new ScenarioMapSourceResult
        {
            State = ScenarioMapSourceState.Embedded,
            RawValue = mapData.RawValue,
            EffectiveValue = value,
            MapText = value,
            ValueResolution = valueResolution,
            SourceDescription = "Embedded map_data"
        };
    }

    private ScenarioMapSourceResult ResolveMapFile(
        DirectValue mapFile,
        string? ownerSourcePath,
        string? wesnothInstallPath,
        IEnumerable<WmlMacroExpansionDefinition>? localDefinitions,
        IWmlMacroExpansionDefinitionResolver? fallbackResolver)
    {
        var valueResolution = _macroExpansionService.ResolveValue(
            mapFile.Value!,
            localDefinitions,
            fallbackResolver);
        if (valueResolution.State is not (WmlResolvedValueState.Literal or WmlResolvedValueState.Resolved))
            return FromUnresolvedValue(mapFile.RawValue, valueResolution, "map_file");

        var lookupPath = Unquote(valueResolution.EffectiveValue);
        return ResolveExternalMap(
            mapFile.RawValue,
            lookupPath,
            "map_file",
            ownerSourcePath,
            wesnothInstallPath,
            valueResolution);
    }

    private static ScenarioMapSourceResult ResolveExternalMap(
        string? rawValue,
        string lookupPath,
        string sourceDescription,
        string? ownerSourcePath,
        string? wesnothInstallPath,
        WmlResolvedValueResult? valueResolution)
    {
        var reference = new WccAssetReference
        {
            Kind = WccAssetReferenceKind.Map,
            OwnerBlockKind = "scenario",
            SourceFieldKey = "map",
            RawValue = rawValue ?? lookupPath,
            LookupPath = lookupPath
        };
        var origin = IsWithinWesnothInstall(ownerSourcePath, wesnothInstallPath)
            ? WccSourceOrigin.CoreInstall
            : WccSourceOrigin.ImportedExternal;
        var resolution = WccAssetPathResolver.ResolveReference(
            ownerSourcePath,
            origin,
            reference,
            wesnothInstallPath);
        var candidates = resolution.CandidatePaths.ToList();

        AddOwnerRelativeIncludeCandidate(candidates, ownerSourcePath, lookupPath);
        var resolvedPath = candidates
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(File.Exists);
        if (resolvedPath is null)
        {
            var unresolved = new ScenarioMapSourceResult
            {
                State = ScenarioMapSourceState.Unresolved,
                RawValue = rawValue,
                EffectiveValue = lookupPath,
                ValueResolution = valueResolution,
                SourceDescription = sourceDescription
            };
            unresolved.CandidatePaths.AddRange(candidates.Distinct(StringComparer.OrdinalIgnoreCase));
            unresolved.Diagnostics.Add($"The map file '{lookupPath}' could not be found in the visible package or Core roots.");
            return unresolved;
        }

        try
        {
            var result = new ScenarioMapSourceResult
            {
                State = ScenarioMapSourceState.ExternalFile,
                RawValue = rawValue,
                EffectiveValue = lookupPath,
                MapText = File.ReadAllText(resolvedPath),
                ResolvedPath = resolvedPath,
                ValueResolution = valueResolution,
                SourceDescription = sourceDescription
            };
            result.CandidatePaths.AddRange(candidates.Distinct(StringComparer.OrdinalIgnoreCase));
            return result;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            var error = new ScenarioMapSourceResult
            {
                State = ScenarioMapSourceState.Error,
                RawValue = rawValue,
                EffectiveValue = lookupPath,
                ResolvedPath = resolvedPath,
                ValueResolution = valueResolution,
                SourceDescription = sourceDescription
            };
            error.CandidatePaths.AddRange(candidates.Distinct(StringComparer.OrdinalIgnoreCase));
            error.Diagnostics.Add($"The map file could not be read: {ex.Message}");
            return error;
        }
    }

    private static ScenarioMapSourceResult FromUnresolvedValue(
        string? rawValue,
        WmlResolvedValueResult resolution,
        string fieldName)
    {
        var state = resolution.State switch
        {
            WmlResolvedValueState.Ambiguous => ScenarioMapSourceState.Ambiguous,
            WmlResolvedValueState.Unsupported or WmlResolvedValueState.Limit => ScenarioMapSourceState.Unsupported,
            _ => ScenarioMapSourceState.Unresolved
        };
        var result = new ScenarioMapSourceResult
        {
            State = state,
            RawValue = rawValue,
            EffectiveValue = resolution.EffectiveValue,
            ValueResolution = resolution,
            SourceDescription = $"Macro-resolved {fieldName}"
        };
        result.Diagnostics.AddRange(resolution.Issues.Select(x => x.Message));
        return result;
    }

    private static DirectValue ReadCurrentValue(
        ScenarioDocument document,
        string key,
        string? overrideValue)
    {
        document.Scenario.FieldStates.TryGetValue(key, out var state);
        var importedValue = state?.PlainValue;
        if (!string.Equals(overrideValue, importedValue, StringComparison.Ordinal) &&
            overrideValue is not null)
        {
            return new DirectValue(overrideValue, Unquote(overrideValue), false);
        }

        // Edit safety and read-only source resolution are separate concerns.
        // A direct map value such as {~add-ons/...} or {MAP_FILE ...} is locked
        // against typed replacement, but it is still the authoritative raw
        // expression that the preview resolver should inspect.
        if (state?.Kind != WmlFieldSafetyKind.ConditionalOrMultipleReadOnly &&
            TryReadDirectScalar(document.EditableBlock, key, out var raw, out var value))
        {
            return new DirectValue(raw, value, false);
        }

        return new DirectValue(
            importedValue,
            Unquote(importedValue),
            state is not null && !state.IsEditable && state.Kind != WmlFieldSafetyKind.MissingEditable);
    }

    private static bool TryReadDirectScalar(
        WmlEditableBlock block,
        string key,
        out string? rawValue,
        out string? value)
    {
        rawValue = null;
        value = null;
        var field = WmlTopLevelFieldAnalyzer.Analyze(block, key);
        if (field.Kind != WmlTopLevelFieldKind.Plain || field.Occurrences.Count != 1)
            return false;

        var lineIndex = field.Occurrences[0].LineIndex;
        var line = block.Lines[lineIndex];
        var equals = line.IndexOf('=');
        if (equals < 0)
            return false;

        var first = line[(equals + 1)..].TrimStart();
        if (first.StartsWith("<<", StringComparison.Ordinal) &&
            TryReadWholeRawStringScalar(block, lineIndex, first, out rawValue, out value))
        {
            return true;
        }

        if (first.Length == 0 || (first[0] != '"' && first[0] != '\''))
        {
            rawValue = WmlInlineCommentSplitter.Split(first).Value;
            value = rawValue?.Trim();
            return true;
        }

        var quote = first[0];
        var content = new System.Text.StringBuilder();
        var raw = new System.Text.StringBuilder();
        var current = first;
        for (var currentLine = lineIndex; currentLine < block.Lines.Count; currentLine++)
        {
            var start = currentLine == lineIndex ? 1 : 0;
            if (currentLine != lineIndex)
                current = block.Lines[currentLine];
            if (currentLine > lineIndex)
            {
                content.Append('\n');
                raw.Append(block.NewLine);
            }

            if (currentLine == lineIndex)
                raw.Append(quote);
            for (var character = start; character < current.Length; character++)
            {
                var ch = current[character];
                raw.Append(ch);
                if (ch == quote)
                {
                    rawValue = raw.ToString();
                    value = content.ToString();
                    return true;
                }

                content.Append(ch);
            }
        }

        rawValue = raw.ToString();
        value = content.ToString();
        return true;
    }

    private static bool TryReadWholeRawStringScalar(
        WmlEditableBlock block,
        int lineIndex,
        string first,
        out string? rawValue,
        out string? value)
    {
        rawValue = null;
        value = null;
        var content = new System.Text.StringBuilder();
        var raw = new System.Text.StringBuilder();
        var current = first;
        for (var currentLine = lineIndex; currentLine < block.Lines.Count; currentLine++)
        {
            var start = currentLine == lineIndex ? 2 : 0;
            if (currentLine != lineIndex)
                current = block.Lines[currentLine];
            if (currentLine > lineIndex)
            {
                content.Append('\n');
                raw.Append(block.NewLine);
            }

            var closing = current.IndexOf(">>", start, StringComparison.Ordinal);
            if (closing < 0)
            {
                raw.Append(current[start..]);
                content.Append(current[start..]);
                continue;
            }

            var suffix = WmlInlineCommentSplitter.Split(current[(closing + 2)..]).Value;
            if (!string.IsNullOrWhiteSpace(suffix))
                return false;

            raw.Append(current[start..(closing + 2)]);
            content.Append(current[start..closing]);
            rawValue = "<<" + raw.ToString();
            value = content.ToString();
            return true;
        }

        return false;
    }

    private static bool TryParseFileInclude(string value, out string? path)
    {
        path = null;
        var trimmed = Unquote(value).Trim();
        if (!trimmed.StartsWith('{') || !trimmed.EndsWith('}'))
            return false;

        var inside = trimmed[1..^1].Trim();
        if (inside.StartsWith('~'))
            inside = inside[1..].TrimStart();
        if (!inside.EndsWith(".map", StringComparison.OrdinalIgnoreCase) ||
            inside.Contains('{') ||
            inside.Contains('}'))
        {
            return false;
        }

        path = inside;
        return true;
    }

    private static string Unquote(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var trimmed = value.Trim();
        if (trimmed.Length >= 4 &&
            trimmed.StartsWith("<<", StringComparison.Ordinal) &&
            trimmed.EndsWith(">>", StringComparison.Ordinal) &&
            trimmed.IndexOf(">>", 2, StringComparison.Ordinal) == trimmed.Length - 2)
        {
            return trimmed[2..^2];
        }

        if (trimmed.Length >= 2 &&
            ((trimmed[0] == '"' && trimmed[^1] == '"') ||
             (trimmed[0] == '\'' && trimmed[^1] == '\'')))
        {
            return trimmed[1..^1];
        }

        return trimmed;
    }

    private static bool IsWithinWesnothInstall(string? sourcePath, string? wesnothInstallPath)
    {
        if (string.IsNullOrWhiteSpace(sourcePath) || string.IsNullOrWhiteSpace(wesnothInstallPath))
            return false;

        try
        {
            var source = Path.GetFullPath(sourcePath);
            var root = Path.GetFullPath(wesnothInstallPath)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
                Path.DirectorySeparatorChar;
            return source.StartsWith(root, StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static void AddOwnerRelativeIncludeCandidate(
        List<string> candidates,
        string? ownerSourcePath,
        string lookupPath)
    {
        if (string.IsNullOrWhiteSpace(ownerSourcePath) ||
            !(lookupPath.StartsWith("./", StringComparison.Ordinal) ||
              lookupPath.StartsWith("../", StringComparison.Ordinal)))
        {
            return;
        }

        var directory = Path.GetDirectoryName(Path.GetFullPath(ownerSourcePath));
        if (directory is null)
            return;
        candidates.Insert(0, Path.GetFullPath(Path.Combine(
            directory,
            lookupPath.Replace('/', Path.DirectorySeparatorChar))));
    }

    private sealed record DirectValue(
        string? RawValue,
        string? Value,
        bool IsUnsafeOrConditional);
}
