using System;
using Wcc.Core.Campaigns;

namespace Wcc.App.ViewModels;

public partial class CampaignWindowViewModel
{
    internal WmlDefinitionSourcePublication? PrepareSharedMacroSourcePublication(
        string sourcePath, string expectedText, string savedText, out string? problem)
    {
        lock (_campaignFieldGate)
        {
            problem = null;
            if (_project is not { } project || !WmlDefinitionSourcePublication.SamePath(sourcePath, project.SourcePath)) return null;
            var document = project.Campaign;
            var generation = _campaignFieldGeneration;
            var version = EditVersion;
            var configuration = _campaignFieldConfiguration;
            var loadRequest = _campaignLoadRequestVersion;
            bool ModelCurrent() => ReferenceEquals(project, _project) && ReferenceEquals(document, project.Campaign) &&
                project.CampaignRawTextSnapshot == expectedText && document?.OriginalText == expectedText &&
                WmlDefinitionSourcePublication.SamePath(sourcePath, project.SourcePath) && version == EditVersion &&
                loadRequest == _campaignLoadRequestVersion && !IsReadOnly && !IsCampaignSourceLoading &&
                !HasCampaignSourceDrafts && !_campaignHasRawSourceChanges && !IsCampaignFieldSaveBusy &&
                !_campaignFieldSession.IsBusy && _campaignFieldSession.Recovery is null;
            bool IsCurrent()
            {
                lock (_campaignFieldGate)
                    return ModelCurrent() && generation == _campaignFieldGeneration &&
                        ReferenceEquals(configuration, _campaignFieldConfiguration);
            }
            if (!IsCurrent())
            { problem = "The main file has changed or has pending edits. Reopen the shared definition after saving or discarding them."; return null; }
            CampaignDocument candidate;
            try { candidate = CampaignImporter.ImportFromText(savedText, sourcePath); }
            catch (Exception error) when (error is ArgumentException or InvalidOperationException)
            { problem = "The saved definition candidate cannot populate the main editor: " + error.Message; return null; }
            bool PublishedCurrent() => ReferenceEquals(project, _project) && ReferenceEquals(candidate, project.Campaign) &&
                project.CampaignRawTextSnapshot == savedText && candidate.OriginalText == savedText &&
                WmlDefinitionSourcePublication.SamePath(sourcePath, project.SourcePath) &&
                version == EditVersion && loadRequest == _campaignLoadRequestVersion &&
                !IsReadOnly && !IsCampaignSourceLoading && !HasCampaignSourceDrafts && !_campaignHasRawSourceChanges &&
                !IsCampaignFieldSaveBusy && !_campaignFieldSession.IsBusy && _campaignFieldSession.Recovery is null;
            bool PublishingCurrent() => PublishedCurrent() && _campaignFieldGeneration == generation + 1 &&
                ReferenceEquals(configuration, _campaignFieldConfiguration);
            bool Publish()
            {
                lock (_campaignFieldGate)
                {
                    if (!IsCurrent()) return false;
                    SourceChanging?.Invoke();
                    if (!IsCurrent()) return false;
                    RevokeCampaignFieldAuthority();
                    if (!ModelCurrent() || _campaignFieldGeneration != generation + 1 ||
                        !ReferenceEquals(configuration, _campaignFieldConfiguration)) return false;
                    project.Campaign = candidate;
                    project.CampaignRawTextSnapshot = savedText;
                    _campaignProjection = null;
                    _campaignMenuOwner = null;
                    _campaignProjectionDocument = null;
                    _campaignRoutes.Clear();
                    _campaignFields.Clear();
                    var hydrated = false;
                    RunUiSync(() => hydrated = HydrateSharedDefinitionCampaignFields(candidate, PublishingCurrent));
                    if (!hydrated || !PublishingCurrent()) return false;
                    RaiseCampaignFieldsChanged();
                    if (!PublishingCurrent()) return false;
                    // The shell's existing notification clears the old dirty marker
                    // and recomputes menu/play context plans from this exact new source.
                    CampaignFieldsSaved?.Invoke(this, EventArgs.Empty);
                    return PublishedCurrent();
                }
            }
            return new(IsCurrent, Publish) { AssetIdentity = project };
        }
    }

    private bool HydrateSharedDefinitionCampaignFields(CampaignDocument document, Func<bool> isCurrent)
    {
        // Each assignment can notify a host that rebinds the source or enters new
        // intent. Stop hydrating that newer editor immediately after the callback.
        Action[] assignments =
        [
            () => TextDomainName = document.TextDomain?.Name,
            () => TextDomainPath = document.TextDomain?.Path,
            () => CampaignId = document.Campaign.Id,
            () => CampaignNameWml = document.Campaign.NameWml,
            () => CampaignAbbrevWml = document.Campaign.AbbrevWml,
            () => CampaignDefine = document.Campaign.Define,
            () => CampaignFirstScenario = document.Campaign.FirstScenario,
            () => CampaignIcon = document.Campaign.Icon,
            () => CampaignImage = document.Campaign.Image,
            () => CampaignBackground = document.Campaign.Background,
            () => CampaignRank = document.Campaign.Rank?.ToString(System.Globalization.CultureInfo.InvariantCulture),
            () => CampaignYear = document.Campaign.Year,
            () => CampaignDescriptionWml = document.Campaign.DescriptionWml
        ];
        foreach (var assignment in assignments)
        {
            if (!isCurrent()) return false;
            assignment();
            if (!isCurrent()) return false;
        }
        return true;
    }
}
