using System;
using System.Collections.Generic;
using System.Linq;
using Wcc.App.ViewModels.Wcc;

namespace Wcc.App.ViewModels;

public partial class ScenarioWindowViewModel
{
    private Func<IReadOnlyList<string>>? _scenarioIdSuggestionProvider;
    private IReadOnlyList<string>? _scenarioIdSuggestionProviderValues;
    private object? _scenarioIdSuggestionAsset;
    private object? _scenarioIdSuggestionDocument;
    private long _scenarioIdSuggestionHostGeneration = -1;
    private long _scenarioIdSuggestionDisplayGeneration = -1;
    private string? _scenarioIdSuggestionContext;
    private IReadOnlyList<string> _scenarioIdSuggestions = Array.AsReadOnly(new[] { "null" });
    internal bool IsRefreshingScenarioIdSuggestions { get; private set; }
    public IReadOnlyList<string> ScenarioIdSuggestions
    {
        get
        {
            var values = _scenarioIdSuggestionProvider?.Invoke() ?? Array.Empty<string>();
            if (!ReferenceEquals(values, _scenarioIdSuggestionProviderValues) || !ReferenceEquals(_scenarioIdSuggestionAsset, _currentScenarioAsset) ||
                !ReferenceEquals(_scenarioIdSuggestionDocument, SelectedScenario?.Document) ||
                _scenarioIdSuggestionHostGeneration != _sourceHostGeneration || _scenarioIdSuggestionDisplayGeneration != _staticInitialValuesGeneration ||
                _scenarioIdSuggestionContext != _evaluationContext.SemanticKey)
            {
                _scenarioIdSuggestionProviderValues = values;
                _scenarioIdSuggestionAsset = _currentScenarioAsset;
                _scenarioIdSuggestionDocument = SelectedScenario?.Document;
                _scenarioIdSuggestionHostGeneration = _sourceHostGeneration;
                _scenarioIdSuggestionDisplayGeneration = _staticInitialValuesGeneration;
                _scenarioIdSuggestionContext = _evaluationContext.SemanticKey;
                _scenarioIdSuggestions = Array.AsReadOnly(values.Append("null").Distinct(StringComparer.Ordinal).ToArray());
            }
            return _scenarioIdSuggestions;
        }
    }
    public void SetScenarioIdSuggestionProvider(Func<IReadOnlyList<string>>? provider)
    {
        if (ReferenceEquals(provider, _scenarioIdSuggestionProvider)) return;
        _scenarioIdSuggestionProvider = provider;
        _scenarioIdSuggestionProviderValues = null;
        RefreshScenarioIdSuggestions();
    }
    public void RefreshScenarioIdSuggestions()
    {
        if (IsRefreshingScenarioIdSuggestions) return;
        IsRefreshingScenarioIdSuggestions = true;
        try
        {
            _ = ScenarioIdSuggestions;
            // Background field publication replaces these rows under this gate.
            // Take only the snapshot here; provider and UI callbacks stay outside.
            WccInitialFieldEditorVm[] fields;
            lock (_wccInitialFieldRefreshGate)
                fields = WccScenarioFieldEditors.Where(field => field.Key == "next_scenario").ToArray();
            foreach (var field in fields) field.RefreshScenarioSuggestions();
            OnPropertyChanged(nameof(ScenarioIdSuggestions));
        }
        finally { IsRefreshingScenarioIdSuggestions = false; }
    }
}
