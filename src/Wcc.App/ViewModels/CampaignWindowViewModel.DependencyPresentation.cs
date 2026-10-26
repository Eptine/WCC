using System;
using System.Collections.Generic;
using System.Linq;
using Wcc.Core.Campaigns;
using Wcc.Core.Library;

namespace Wcc.App.ViewModels;

public partial class CampaignWindowViewModel
{
    private IReadOnlyList<WccCampaignContainedSourceObservation> CurrentContainedSourceObservations()
    {
        lock (_campaignFieldGate)
            return _campaignProjectionBinding is { } binding && IsCurrentCampaignBinding(binding) &&
                _campaignStructureInventory is { } inventory &&
                (_campaignProjection?.EvaluationEntrySource ?? _campaignProjection?.Source) is { } source &&
                ReferenceEquals(inventory.Snapshot.Source, source) && source.Text == GetCurrentMainSourceRawWml()
                ? inventory.ContainedSources.Where(value => value.ProjectRevisionId == binding.Configuration.RootRevision).ToArray() : [];
    }

    private static bool IsProjectDependencyProvider(WccWorkingProjectDependencyEntry entry) =>
        entry.Status == WccWorkingProjectDependencyStatus.Resolved &&
        (entry.IsCurrentProjectSource || entry.IsVerifiedWorkingProjectSource);

    private bool IsUnselectedExternalAlternative(WccWorkingProjectDependencyEntry entry) => !entry.IsDeclared &&
        CurrentContainedSourceObservations().Any(value => value.ExternalAddonId == entry.AddonId &&
            value.OwningAddonId == entry.RequiredByAddonId);

    private CampaignDeclaredDependencyRow BuildDependencyRow(IReadOnlyList<WccWorkingProjectDependencyEntry> entries)
    {
        var entry = entries[0];
        var alternative = entries.All(IsUnselectedExternalAlternative);
        var status = IsProjectDependencyProvider(entry) ? "Current campaign source" : alternative
            ? "Contained fallback observed / External reference retained" : !entries.Any(value => value.IsDeclared) && entry.Status != WccWorkingProjectDependencyStatus.Resolved
            ? "Possible source reference / " + (entry.Status switch
            {
                WccWorkingProjectDependencyStatus.Missing => "Not attached",
                WccWorkingProjectDependencyStatus.Unavailable => "Unavailable",
                WccWorkingProjectDependencyStatus.Ambiguous => "Ambiguous",
                WccWorkingProjectDependencyStatus.Incompatible => "Context mismatch",
                _ => "Needs review"
            }) : entry.Status switch
            {
                WccWorkingProjectDependencyStatus.Resolved => "Dependency source available",
                WccWorkingProjectDependencyStatus.Missing => "Dependency source missing",
                WccWorkingProjectDependencyStatus.Ambiguous => "Ambiguous dependency sources",
                WccWorkingProjectDependencyStatus.Unavailable => "Dependency source unavailable",
                WccWorkingProjectDependencyStatus.Incompatible => "Incompatible dependency source",
                _ => "Dependency source needs review"
            };
        var details = alternative ? entries.Select(value => value.Message).Concat(CurrentContainedSourceObservations().Where(value => value.ExternalAddonId == entry.AddonId)
            .Select(value => "The captured phase uses the project-contained source at " + value.RootPath + ". " +
                value.Scope + "; this fallback does not classify other references, phases or runtime loading. The external location remains authored in RAW WML."))
            : entries.Select(value => value.Message);
        return new(entry.AddonId, status, string.Join(Environment.NewLine, details.Distinct(StringComparer.Ordinal)))
        {
            IsDeclared = entries.Any(value => value.IsDeclared),
            HasCapturedContainedSource = alternative,
            RequiredBy = string.Join(Environment.NewLine, entries.Select(value =>
                (value.IsDeclared ? "Declared by " : "Referenced by ") + value.RequiredByAddonId).Distinct(StringComparer.Ordinal)),
            LogicalMount = entry.LogicalMount, SourcePath = entry.RootPath
        };
    }

    private string BuildDependencyResolutionSummary()
    {
        var report = CurrentCampaignDependencySourceHost?.Report;
        // A reached fallback describes one branch; other references to the same
        // external source can remain unresolved, so retain every diagnostic.
        var messages = report?.Diagnostics
            .Select(value => value.Message).Distinct(StringComparer.Ordinal).ToArray() ?? [];
        return messages.Length == 0
            ? "Source availability is separate from runtime activation. Lua-generated loading remains available through source inspection."
            : string.Join(Environment.NewLine, messages);
    }
}
