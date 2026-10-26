using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Wcc.Core.Scenarios;

namespace Wcc.App.ViewModels.Wcc;

/// <summary>Immutable display slots for one pending source-topology recipe, never write authority.</summary>
public sealed class WccScenarioDifficultyDraft
{
    internal WccScenarioDifficultyDraft(WccScenarioDifficultyFieldSnapshot snapshot,
        WccScenarioDifficultyValueMode mode, long bindingVersion,
        IEnumerable<KeyValuePair<string, WccScenarioInlineDraft>> slots,
        IEnumerable<WccScenarioDifficultyFieldValue> originalDisplayValues)
    {
        Snapshot = snapshot;
        Mode = mode;
        BindingVersion = bindingVersion;
        Slots = new ReadOnlyDictionary<string, WccScenarioInlineDraft>(
            slots.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal));
        OriginalDisplayValues = Array.AsReadOnly(originalDisplayValues.ToArray());
    }

    public WccScenarioDifficultyFieldSnapshot Snapshot { get; }
    public WccScenarioDifficultyValueMode Mode { get; }
    public IReadOnlyDictionary<string, WccScenarioInlineDraft> Slots { get; }
    internal long BindingVersion { get; }
    internal IReadOnlyList<WccScenarioDifficultyFieldValue> OriginalDisplayValues { get; }
    public string Summary => Mode == WccScenarioDifficultyValueMode.Shared
        ? "Shared value: " + Slots.Values.FirstOrDefault()?.Text
        : "Separate difficulty values: " + string.Join(", ", Snapshot.Values.Select(value =>
            value.Define + "=" + (Slots.TryGetValue(value.ContextSemanticKey, out var slot) ? slot.Text : "?")));
}
