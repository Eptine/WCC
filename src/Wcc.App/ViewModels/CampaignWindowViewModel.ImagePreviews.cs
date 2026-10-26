using System;
using Wcc.App.ViewModels.Wcc;
using Wcc.Core.References;

namespace Wcc.App.ViewModels;

public partial class CampaignWindowViewModel
{
    private WccPresetFieldImagePreviews? _campaignImagePreviews;
    private CampaignDraftBinding? _campaignImagePreviewBinding;
    private string? _campaignImagePreviewInstall;
    private readonly WccFieldImagePreviewVm _unavailableCampaignImagePreview = new();

    // Previews reuse the existing image-expression renderer. They neither set a
    // field nor confer edit authority on a captured or inherited source.
    internal WccFieldImagePreviewVm GetCampaignFieldImagePreview(WccCampaignFieldEditorVm field)
    {
        lock (_campaignFieldGate)
        {
            if (!field.ShowsImagePreview || !_campaignFields.Contains(field) ||
                !ReferenceEquals(field.Owner, _campaignMenuOwner)) return _unavailableCampaignImagePreview;
            return CurrentCampaignImagePreviews()?.Get(field.Owner, field.Key,
                key => key == field.Key ? field.PreviewExpression : null) ?? _unavailableCampaignImagePreview;
        }
    }

    internal WccFieldImagePreviewVm GetCampaignDifficultyImagePreview(CampaignDifficultyDisplayRow row)
    {
        lock (_campaignFieldGate)
        {
            if (!IsCurrentCampaignDifficultyDisplayRow(row) || _campaignMenuOwner is not { } owner)
                return _unavailableCampaignImagePreview;
            var key = "campaign_difficulty:" + row.Define;
            return CurrentCampaignImagePreviews()?.Get(owner, key,
                requested => requested == key ? row.Image : null) ?? _unavailableCampaignImagePreview;
        }
    }

    private WccPresetFieldImagePreviews? CurrentCampaignImagePreviews()
    {
        InvalidateCampaignImagePreviewsIfStale();
        if (_campaignImagePreviews is not null) return _campaignImagePreviews;
        if (_campaignProjectionBinding is not { } binding || !IsCurrentCampaignBinding(binding) ||
            binding.Configuration.Snapshot is not { } snapshot || binding.Configuration.RootRevision is not { } root ||
            _campaignProjection?.Source is not { } source || !IsCurrentCampaignMenuSource(source)) return null;
        var install = _campaignPresetInstallPath;
        _campaignImagePreviewBinding = binding;
        _campaignImagePreviewInstall = install;
        var evaluationSource = _campaignProjection.EvaluationEntrySource ?? source;
        var resources = _campaignEntryCatalog is { Capture: { } capture, Menu.Projection: { } projection }
            ? new Lazy<WccCapturedImageResourceContext?>(() => WccCapturedImageResourceContext.Create(capture, projection))
            : null;
        _campaignImagePreviews = new(snapshot, root, evaluationSource.Path, install, () =>
            ReferenceEquals(binding, _campaignProjectionBinding) && IsCurrentCampaignBinding(binding) &&
            ReferenceEquals(source, _campaignProjection?.Source) && IsCurrentCampaignMenuSource(source) &&
            install == _campaignPresetInstallPath, capturedSource: evaluationSource)
        { CapturedResourceContext = resources };
        return _campaignImagePreviews;
    }

    private void InvalidateCampaignImagePreviewsIfStale()
    {
        if (_campaignImagePreviews is null) return;
        if (_campaignImagePreviewBinding is { } binding && ReferenceEquals(binding, _campaignProjectionBinding) &&
            IsCurrentCampaignBinding(binding) && _campaignImagePreviewInstall == _campaignPresetInstallPath &&
            _campaignProjection?.Source is { } source && IsCurrentCampaignMenuSource(source)) return;
        var previous = _campaignImagePreviews;
        _campaignImagePreviews = null;
        _campaignImagePreviewBinding = null;
        previous.Dispose();
    }
}
