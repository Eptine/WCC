using System;
using System.Collections.Generic;
using System.Linq;
using Wcc.App.ViewModels.Wcc;
using Wcc.Core.Scenarios;
using Wcc.Core.Wml;

namespace Wcc.App.ViewModels;

/// <summary>Captured catalog presentation. Fresh Core review, rather than this token, grants write authority.</summary>
public sealed class WccCampaignPresetSelectionBinding
{
    private readonly Func<bool> _isCurrent;
    private readonly Func<WccCampaignLocalPresetEntry, bool>? _isEntryCurrent;
    internal WccCampaignPresetSelectionBinding(WccCampaignLocalPresetRole role,
        IEnumerable<WccCampaignLocalPresetEntry> entries, Func<bool> isCurrent,
        Func<WccCampaignLocalPresetEntry, bool>? isEntryCurrent = null)
    { Role = role; Entries = Array.AsReadOnly(entries.ToArray()); _isCurrent = isCurrent; _isEntryCurrent = isEntryCurrent; }
    internal static IReadOnlyList<WccCampaignLocalPresetEntry> AvailableEntries(
        IEnumerable<WccCampaignLocalPresetEntry> entries, WmlCampaignEvaluationPlan? plan,
        WccCampaignLocalPresetRole? role = null) => plan is null ? [] : Array.AsReadOnly(entries.Where(entry =>
            entry.IsSupported && (role is null || entry.Role == role) && entry.IsAvailableIn(plan)).ToArray());
    internal static WccCampaignPresetSelectionBinding CaptureAvailable(WccCampaignLocalPresetRole role,
        IEnumerable<WccCampaignLocalPresetEntry> entries, WmlCampaignEvaluationPlan? plan, Func<bool> isCurrent,
        Func<WccCampaignLocalPresetEntry, bool>? isEntryCurrent = null) =>
        new(role, AvailableEntries(entries, plan, role), isCurrent, isEntryCurrent);
    internal bool ContainsCurrent(WccCampaignLocalPresetEntry entry) => IsCurrent && entry.IsSupported && entry.Role == Role &&
        Entries.Any(current => ReferenceEquals(current, entry)) && _isEntryCurrent?.Invoke(entry) != false;
    internal static bool MatchesCurrentEntry(WccCampaignLocalPresetEntry current, WccCampaignLocalPresetEntry chosen,
        WmlCampaignEvaluationPlan plan) => current.IsSupported && chosen.IsSupported && current.IsAvailableIn(plan) && chosen.IsAvailableIn(plan) &&
        current.Id == chosen.Id && current.Name == chosen.Name && current.Role == chosen.Role && current.Scope == chosen.Scope &&
        current.Definition.TextDomain == chosen.Definition.TextDomain && SameRange(current.DefinitionRange, chosen.DefinitionRange) &&
        SameRange(current.BodyRange, chosen.BodyRange);
    private static bool SameRange(WmlStaticSourceRange current, WmlStaticSourceRange chosen) =>
        string.Equals(current.Source.Path, chosen.Source.Path, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) &&
        current.Source.ContentHash == chosen.Source.ContentHash && current.StartOffset == chosen.StartOffset && current.Length == chosen.Length && current.Text == chosen.Text;
    public WccCampaignLocalPresetRole Role { get; }
    public IReadOnlyList<WccCampaignLocalPresetEntry> Entries { get; }
    public bool IsCurrent => _isCurrent();
    public bool AuthorizesPersistence => false;
}

public sealed record WccCampaignPresetSelection(WccCampaignPresetSelectionBinding Binding,
    WccCampaignLocalPresetEntry Entry, string? IdentityOverride);

/// <summary>Uses the existing single-selection catalog picker and keeps the exact entry behind its durable ID.</summary>
public sealed class WccCampaignPresetPickerVm : CatalogPickerWindowViewModel
{
    public WccCampaignPresetSelectionBinding Binding { get; }
    public string? IdentityOverride { get; set; }
    public bool HasIdentityOverride => Binding.Role is WccCampaignLocalPresetRole.Leader or WccCampaignLocalPresetRole.Side;
    public string IdentityLabel => Binding.Role == WccCampaignLocalPresetRole.Leader ? "Instance unit ID (optional)" : "Side carryover ID (optional)";
    public string IdentityGuidance => Binding.Role == WccCampaignLocalPresetRole.Leader
        ? "Leave empty to reuse the preset's character ID across scenarios. A second copy in the same scenario needs a distinct ID."
        : "Leave empty to reuse the preset's save_id or first leader ID for carryover. Nested leader IDs are retained; duplicating them needs another preset or RAW.";
    public WccCampaignPresetPickerVm(WccCampaignPresetSelectionBinding binding)
        : base(binding.Entries.Select(entry => new CatalogPickerOptionVm { ValueId = entry.Id.ToString("N"),
            DisplayName = entry.Name, EmissionKind = CatalogPickerEmissionKind.MacroInvocation,
            ResourceSourceLabel = "Campaign", ResourceSourceHint = entry.SourcePath }), null, false,
            "Choose " + binding.Role + " preset", "Saved definitions from the current campaign. Selection creates a local invocation; the shared definition is unchanged. Save before editing local overrides.")
    { Binding = binding; }
    public WccCampaignPresetSelection? GetPresetSelection()
    {
        if (!Binding.IsCurrent) return null;
        var id = GetSelectedCsv();
        var entry = Binding.Entries.SingleOrDefault(candidate => candidate.Id.ToString("N") == id);
        return entry is null || !Binding.ContainsCurrent(entry) ? null : new(Binding, entry, !HasIdentityOverride || string.IsNullOrWhiteSpace(IdentityOverride) ? null : IdentityOverride.Trim());
    }
}
