using System;
using System.Linq;
using Wcc.Core.Campaigns;
using Wcc.Core.Library;
using Wcc.Core.References;
using Wcc.Core.Scenarios;
using Wcc.Core.Wml;

namespace Wcc.App.ViewModels;

public partial class ScenarioWindowViewModel
{
    private ScenarioInspectionBinding? _scenarioInspectionBinding;
    private bool _scenarioInspectionOnly;
    private WmlScenarioInspectionResult? _publishedScenarioInspection;

    /// <summary>Whether the selected preview omits unit interiors and cannot authorize an edit.</summary>
    public bool IsBoundedScenarioInspection => _scenarioInspectionBinding is not null || _scenarioInspectionOnly;

    internal void SetCampaignScenarioInspection(WccStaticSourceCaptureResult? capture,
        WccCampaignScenarioInspectionCatalogProjection? catalog, WccCampaignScenarioMembership? membership,
        WccLibrarySnapshot? snapshot, Func<bool> isCurrent)
    {
        var old = _scenarioInspectionBinding;
        if (ReferenceEquals(old?.Catalog, catalog) && ReferenceEquals(old?.Membership, membership) &&
            ReferenceEquals(old?.Snapshot, snapshot) && (old is null || Equals(old.RootSelection, _staticRootRevisionSelection) &&
                old.ProfileIdentity == _wesnothSourceProfileIdentity && SameOptionalWccInlinePath(old.AddonsPath, _wesnothSourceAddonsPath) &&
                MusicScheduleRevisionSignature(old.VisibleRevisions) == MusicScheduleRevisionSignature(_macroVisibleRevisionIds)) &&
            (catalog is not null || old is null)) return;
        var next = capture is not null && catalog is not null && membership?.OwnerSource is not null &&
            membership.DisplayScenarioSpan is not null && catalog.Scenarios.Any(item => ReferenceEquals(item, membership)) && snapshot is not null &&
            ReferenceEquals(catalog.Entry.Plan, _sourceEvaluationPlan)
            ? new ScenarioInspectionBinding(capture, catalog, membership, snapshot, _staticRootRevisionSelection,
                _macroVisibleRevisionIds, _wesnothSourceAddonsPath, _wesnothSourceProfileIdentity, isCurrent) : null;
        if (old is null && next is null) return;
        _scenarioInspectionBinding = next;
        RefreshMapPreview();
        RaiseScenarioInspectionChanged();
    }

    private bool HasCurrentScenarioInspectionBinding(ScenarioInspectionBinding binding, WmlStaticSource source,
        WmlCampaignEvaluationPlan? plan, WccLibrarySnapshot? snapshot, StaticRootRevisionSelection? root,
        System.Collections.Generic.IReadOnlySet<WccPackageRevisionId>? visible, string? addons, string profile) =>
        binding.IsCurrent() && binding.Capture.IsCaptureComplete && ReferenceEquals(binding.Catalog.Entry.Plan, plan) &&
        ReferenceEquals(binding.Snapshot, snapshot) && Equals(binding.RootSelection, root) &&
        MusicScheduleRevisionSignature(binding.VisibleRevisions) == MusicScheduleRevisionSignature(visible) &&
        SameOptionalWccInlinePath(binding.AddonsPath, addons) && binding.ProfileIdentity == profile &&
        binding.Membership.OwnerSource is { } captured && PathsEqual(captured.Path, source.Path) &&
        captured.ContentHash == source.ContentHash && captured.Text == source.Text;

    private void RaiseScenarioInspectionChanged()
    {
        OnPropertyChanged(nameof(IsBoundedScenarioInspection));
        OnPropertyChanged(nameof(IsReadOnly));
        OnPropertyChanged(nameof(CanEditDocument));
        RaiseEditingStateChanged();
    }

    private StaticInitialValuesResult ReadScenarioInspection(WmlEvaluationContext context,
        ScenarioInspectionBinding binding)
    {
        var inspected = binding.Catalog.Contexts.SingleOrDefault(item => item.Context.SemanticKey == context.SemanticKey)?.Inspection;
        if (inspected is not { IsComplete: true })
            return new(null, false, "The retained scenario inspection is incomplete in this difficulty.") { InspectionOnly = true };
        return CreateScenarioInspectionValues(binding.Capture, inspected);
    }

    private static StaticInitialValuesResult CreateScenarioInspectionValues(WccStaticSourceCaptureResult capture,
        WmlScenarioInspectionResult inspection) => new(inspection.IsComplete ? inspection.Projection : null, inspection.IsComplete,
        inspection.IsComplete ? "Read-only scenario preview: full expansion exceeded the output limit. Unit interiors were omitted for display; scenario and unit saving require complete validation."
            : "The retained scenario inspection is incomplete.", true)
    {
        CanWarmDeclaredContexts = inspection.IsComplete,
        InspectionOnly = true,
        Inspection = inspection,
        ImageResources = inspection.IsComplete ? new Lazy<WccCapturedImageResourceContext>(() =>
            WccCapturedImageResourceContext.Create(capture, inspection)) : null
    };

    private sealed record ScenarioInspectionBinding(WccStaticSourceCaptureResult Capture,
        WccCampaignScenarioInspectionCatalogProjection Catalog, WccCampaignScenarioMembership Membership,
        WccLibrarySnapshot Snapshot, StaticRootRevisionSelection? RootSelection,
        System.Collections.Generic.IReadOnlySet<WccPackageRevisionId>? VisibleRevisions,
        string? AddonsPath, string ProfileIdentity, Func<bool> IsCurrent);
}
