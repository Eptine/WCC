using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using Wcc.App.ViewModels.Wcc;
using Wcc.Core.Projects;
using Wcc.Core.Scenarios;
using Wcc.Core.Wml;

namespace Wcc.App.ViewModels;

public partial class CampaignWindowViewModel
{
    private sealed record ScenarioIdSource(ScenarioFileAsset Asset, string? Raw, ScenarioDocument[] Documents);
    private static readonly ScenarioInitialFieldDefinition ScenarioIdSuggestionDefinition =
        ScenarioInitialFieldCatalog.ScenarioFields.Single(field => field.Key == "id");
    private readonly HashSet<ProjectScenarioFileVm> _scenarioIdSuggestionRows = [];
    private ScenarioIdSource[] _scenarioIdSuggestionSources = [];
    private WccProject? _scenarioIdSuggestionProject;
    private long _scenarioIdSuggestionGeneration = -1;
    private object? _scenarioIdSuggestionConfiguration;
    private IReadOnlyList<string> _attachedScenarioIdSuggestions = [];
    internal bool IsRefreshingAttachedScenarioIdSuggestions { get; private set; }

    public CampaignWindowViewModel()
    {
        ScenarioFiles.CollectionChanged += (_, _) => RefreshAttachedScenarioIdSuggestions();
        PropertyChanged += (_, args) =>
        { if (args.PropertyName == nameof(CurrentProject)) RefreshAttachedScenarioIdSuggestions(); };
    }

    // These are literal suggestions from attached source documents. They do not
    // infer story order, rename references, or resolve conditional/dynamic IDs.
    public IReadOnlyList<string> AttachedScenarioIdSuggestions
    {
        get
        {
            var project = CurrentProject;
            var sources = project?.ProjectScenarioFiles.Select(asset =>
                new ScenarioIdSource(asset, asset.RawTextSnapshot, asset.Documents.ToArray())).ToArray() ?? [];
            if (ReferenceEquals(project, _scenarioIdSuggestionProject) && _scenarioIdSuggestionGeneration == _campaignFieldGeneration &&
                ReferenceEquals(_scenarioIdSuggestionConfiguration, _campaignFieldConfiguration) && sources.Length == _scenarioIdSuggestionSources.Length &&
                sources.Zip(_scenarioIdSuggestionSources).All(pair => ReferenceEquals(pair.First.Asset, pair.Second.Asset) &&
                    pair.First.Raw == pair.Second.Raw && pair.First.Documents.SequenceEqual(pair.Second.Documents)))
                return _attachedScenarioIdSuggestions;
            _scenarioIdSuggestionProject = project;
            _scenarioIdSuggestionGeneration = _campaignFieldGeneration;
            _scenarioIdSuggestionConfiguration = _campaignFieldConfiguration;
            _scenarioIdSuggestionSources = sources;
            _attachedScenarioIdSuggestions = Array.AsReadOnly(sources.SelectMany(ReadLiteralScenarioIds)
                .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray());
            return _attachedScenarioIdSuggestions;
        }
    }

    public void RefreshAttachedScenarioIdSuggestions()
    {
        if (IsRefreshingAttachedScenarioIdSuggestions) return;
        IsRefreshingAttachedScenarioIdSuggestions = true;
        try
        {
            foreach (var row in _scenarioIdSuggestionRows.Where(row => !ScenarioFiles.Contains(row)).ToArray())
            { row.PropertyChanged -= AttachedScenarioIdRowChanged; _scenarioIdSuggestionRows.Remove(row); }
            foreach (var row in ScenarioFiles.Where(row => !_scenarioIdSuggestionRows.Contains(row)))
            { row.PropertyChanged += AttachedScenarioIdRowChanged; _scenarioIdSuggestionRows.Add(row); }
            _ = AttachedScenarioIdSuggestions;
            foreach (var field in CaptureCampaignFieldsSnapshot().Where(field => field.Key == "first_scenario")) field.RefreshScenarioSuggestions();
            OnPropertyChanged(nameof(AttachedScenarioIdSuggestions));
        }
        finally { IsRefreshingAttachedScenarioIdSuggestions = false; }
    }
    private void AttachedScenarioIdRowChanged(object? sender, PropertyChangedEventArgs args)
    { if (args.PropertyName == nameof(ProjectScenarioFileVm.DisplayName)) RefreshAttachedScenarioIdSuggestions(); }

    private static IEnumerable<string> ReadLiteralScenarioIds(ScenarioIdSource source)
    {
        if (source.Raw is not { Length: > 0 and <= 8 * 1024 * 1024 } raw || string.IsNullOrWhiteSpace(source.Asset.SourcePath)) yield break;
        var analysis = WmlEditorAnalysis.Build(raw);
        foreach (var document in source.Documents)
        {
            if (document.OriginalText != raw || document.BlockSpan.StartIndex < 0 || document.BlockSpan.EndIndexExclusive > raw.Length ||
                raw[document.BlockSpan.StartIndex..document.BlockSpan.EndIndexExclusive] != document.BlockSpan.RawText ||
                !document.Scenario.FieldStates.TryGetValue("id", out var state) || state.Kind != WmlFieldSafetyKind.PlainEditable ||
                WccScenarioFieldValueCodec.InferInputKind(state.PlainValue) != WccScenarioFieldValueInputKind.Literal ||
                !WccScenarioFieldValueCodec.IsSupportedResolvedValue(ScenarioIdSuggestionDefinition, state.PlainValue)) continue;
            var opening = analysis.FindTagAtCaret(document.BlockSpan.StartIndex);
            if (opening is not { Name: "scenario", IsClosing: false, IsComplete: true, IsComputedName: false, MatchingTagIndex: { } partner } ||
                analysis.TagOccurrences[partner].StartOffset + analysis.TagOccurrences[partner].Length != document.BlockSpan.EndIndexExclusive ||
                IsInsideSourceDirective(raw, analysis, document.BlockSpan.StartIndex)) continue;
            var id = WccScenarioFieldValueCodec.DecodeLiteral(state.PlainValue);
            if (!string.IsNullOrWhiteSpace(id) && id.Length <= 4096 && !id.Any(char.IsControl) && id.IndexOfAny(['{', '}', '$']) < 0)
                yield return id;
        }
    }
    private static bool IsInsideSourceDirective(string raw, WmlEditorAnalysis analysis, int offset)
    {
        var regions = new Stack<string>();
        foreach (var token in analysis.Tokens.Where(token => token.Kind == WmlEditorTokenKind.Directive && token.StartOffset < offset))
        {
            var name = raw.Substring(token.StartOffset, token.Length).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            if (name is "#define" or "#ifdef" or "#ifndef" or "#ifhave" or "#ifnhave" or "#ifver" or "#ifnver") regions.Push(name);
            else if (name is "#endif" or "#enddef")
            {
                if (regions.Count == 0 || (name == "#enddef") != (regions.Peek() == "#define")) return true;
                regions.Pop();
            }
            else if (name == "#else" && (regions.Count == 0 || regions.Peek() == "#define")) return true;
        }
        return regions.Count != 0;
    }
}
