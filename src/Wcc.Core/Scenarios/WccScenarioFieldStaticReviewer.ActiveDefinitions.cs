using Wcc.Core.Wml;

namespace Wcc.Core.Scenarios;

internal static partial class WccScenarioFieldStaticReviewer
{
    /// <summary>
    /// Exact success-only reuse during one active-definition validation call.
    /// Settings own their definition array, the caller owns its completed range
    /// dictionary, and frames come only from the evaluation's immutable snapshots.
    /// No range or frame from another context, candidate or call is retained.
    /// </summary>
    private sealed class ProjectActiveDefinitionValidation(
        WccScenarioInitialSettingsEdit? settings,
        IReadOnlyDictionary<WmlMacroExpansionDefinition, WmlStaticSourceRange> definitions,
        WccScenarioFieldSourceChange change, bool proposed, WccSourceSaveDiagnosticCollector? diagnostics,
        int maximumDefinitionCandidates = 8_192, int maximumValidatedFrames = 8_192,
        int maximumRecipeRanges = 8_192)
    {
        private readonly HashSet<WmlStaticExpansionFrame> _validatedFrames = new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<WmlMacroExpansionDefinition, WmlStaticSourceRange> _verifiedRecipes =
            new(ReferenceEqualityComparer.Instance);
        private Dictionary<string, List<WmlMacroExpansionDefinition>>? _candidatesByName;
        private bool _lookupAttempted;

        public bool IsAllowed(WmlStaticExpansionFrame frame)
        {
            diagnostics?.Add(WccSourceSaveCount.ActiveDefinitionFrameChecks);
            if (_validatedFrames.Contains(frame))
            {
                diagnostics?.Add(WccSourceSaveCount.ActiveDefinitionFrameCacheHits);
                return true;
            }
            if (settings is null) return false;
            foreach (var definition in Candidates(frame.MacroName))
            {
                // The fallback retains the original ordered, short-circuit
                // predicate, including malformed candidates that are never used.
                if (definition.Name != frame.MacroName) continue;
                if (!_verifiedRecipes.TryGetValue(definition, out var expected))
                {
                    diagnostics?.Add(WccSourceSaveCount.ActiveDefinitionRecipeChecks);
                    if (!definitions.TryGetValue(definition, out var original)
                        || (proposed ? change.MapRange(original) : original) is not { } mapped
                        || mapped.Text != original.Text) continue;
                    expected = mapped;
                    if (_verifiedRecipes.Count < maximumRecipeRanges)
                        _verifiedRecipes.Add(definition, expected);
                }
                else diagnostics?.Add(WccSourceSaveCount.ActiveDefinitionRecipeCacheHits);
                if (!WccScenarioFieldRouteAnalyzer.SameSource(expected.Source, frame.Definition.Source)
                    || expected.StartOffset != frame.Definition.StartOffset || expected.Length != frame.Definition.Length) continue;
                if (_validatedFrames.Count < maximumValidatedFrames) _validatedFrames.Add(frame);
                return true;
            }
            return false;
        }

        private IEnumerable<WmlMacroExpansionDefinition> Candidates(string name)
        {
            if (!_lookupAttempted)
            {
                _lookupAttempted = true;
                if (settings!.Definitions.Count <= maximumDefinitionCandidates)
                {
                    var byName = new Dictionary<string, List<WmlMacroExpansionDefinition>>(StringComparer.Ordinal);
                    foreach (var definition in settings.Definitions)
                    {
                        // Null records/names must not change when the original
                        // predicate throws or succeeds before an unused entry.
                        if (definition is null || definition.Name is null)
                        {
                            diagnostics?.Add(WccSourceSaveCount.ActiveDefinitionLookupFallbacks);
                            return settings.Definitions;
                        }
                        if (!byName.TryGetValue(definition.Name, out var candidates))
                            byName.Add(definition.Name, candidates = []);
                        candidates.Add(definition);
                    }
                    _candidatesByName = byName;
                }
                else diagnostics?.Add(WccSourceSaveCount.ActiveDefinitionLookupFallbacks);
            }
            if (_candidatesByName is null) return settings!.Definitions;
            return name is not null && _candidatesByName.TryGetValue(name, out var matching) ? matching : [];
        }
    }
}
