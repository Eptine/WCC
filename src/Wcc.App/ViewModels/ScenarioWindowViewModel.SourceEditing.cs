using System;
using System.IO;
using System.Linq;
using Wcc.App.ViewModels.Wcc;
using Wcc.Core.Projects;
using Wcc.Core.Scenarios;
using Wcc.Core.Wml;

namespace Wcc.App.ViewModels;

public partial class ScenarioWindowViewModel
{
    // Shared source binding for field, event, Story and collected Save workflows.
    // The WCC edit session owns persistence and retains recovery across rebinding.
    private WmlCampaignEvaluationPlan? _sourceEvaluationPlan;
    private WccEditorSessionContext _sourceHostSession = WccEditorSessionContext.Empty;
    private long _sourceHostGeneration;
    private long _exactPreviewScalarHostGeneration;
    private bool _externalSourceChangesPending;
    public event EventHandler? ScenarioSourceSaved;

    public bool UsesSourceBackedFieldEditing =>
        _sourceHostSession.Scope == WccEditorSessionScope.Project && _sourceEvaluationPlan is not null &&
        IsRuntimeOwnedPath(_currentSourcePath ?? LoadedPath);

    public string? SourceEditingStatus { get; private set; }

    private void InitializeSourceEditing()
    {
        InitializeWccFieldEditing();
    }

    public void InvalidateSourceEditingSession()
    {
        using var displayRefresh = DeferFieldDisplayRefresh();
        InvalidateWccFieldEditing();
        _sourceHostGeneration++;
    }

    public void SetSourceEditingHostSession(WccEditorSessionContext session)
    {
        _savedSourcePreviewNavigationGeneration++;
        ClearPendingWccFieldSelection();
        CancelStaticInitialValues();
        InvalidateSourceEditingSession();
        _sourceHostSession = session;
        OnPropertyChanged(nameof(UsesSourceBackedFieldEditing));
        RaiseEditingStateChanged();
    }

    public void SetExternalSourceChangesPending(bool pending)
    {
        if (_externalSourceChangesPending == pending)
            return;
        CancelStaticInitialValues();
        InvalidateSourceEditingSession();
        _externalSourceChangesPending = pending;
        RaiseInitialScalarFieldsChanged();
    }

    public bool IsCurrentScalarBreadcrumb(ScenarioScalarSourceBreadcrumbVm row) =>
        IsEngineWmlEvaluationEnabled && EffectiveContextSourceBreadcrumbs.Contains(row) &&
        row.HostGeneration == _exactPreviewGeneration && row.SessionGeneration == _sourceHostGeneration;

    private void PublishNativeSourceBreadcrumbs(
        string sourceText,
        ScenarioDocument selectedDocument,
        ScenarioDocument effectiveDocument,
        string sourcePath,
        string projectRevision,
        WesnothPreprocessRequest request)
    {
        if (!IsEngineWmlEvaluationEnabled)
            return;
        SourceEditingStatus = null;
        // Fresh importer spans describe the exact loaded bytes-as-text. Rendering
        // mutable GUI documents here could normalize unrelated authored WML.
        var documents = ScenarioImporter.ImportAllFromText(sourceText);
        var authored = documents.SingleOrDefault(document =>
            document.BlockSpan.StartIndex == selectedDocument.BlockSpan.StartIndex &&
            document.BlockSpan.EndIndexExclusive == selectedDocument.BlockSpan.EndIndexExclusive);
        if (authored is null || _hasAuthoredSourceChanges || _externalSourceChangesPending ||
            !string.Equals(sourceText, _pristineLoadedSourceText, StringComparison.Ordinal))
        {
            SourceEditingStatus = "Save or reload pending source changes before inspecting native source locations.";
            OnPropertyChanged(nameof(SourceEditingStatus));
            return;
        }

        var routes = ScenarioScalarSourceRouteAnalyzer.Analyze(authored, effectiveDocument, _evaluationContext);
        // The retired transaction snapshot used this exact imported physical span.
        var authoredBlock = sourceText.Substring(authored.BlockSpan.StartIndex,
            authored.BlockSpan.EndIndexExclusive - authored.BlockSpan.StartIndex);

        EffectiveContextSourceBreadcrumbs.Clear();
        foreach (var route in routes.AllRoutes.Where(route => route.EffectiveValue is not null))
        {
            EffectiveContextSourceBreadcrumbs.Add(new ScenarioScalarSourceBreadcrumbVm
            {
                Route = route,
                AuthoredScenarioWml = authoredBlock,
                SourcePath = sourcePath,
                HostGeneration = _exactPreviewGeneration,
                SessionGeneration = _sourceHostGeneration
            });
        }
        OnPropertyChanged(nameof(HasEffectiveContextSourceBreadcrumbs));
        OnPropertyChanged(nameof(SourceEditingStatus));
        RaiseInitialScalarFieldsChanged();
    }

    internal bool TryPublishReviewedPackageRawScenario(ScenarioFileAsset asset, string baseline,
        string candidate, int scenarioIndex, int sideIndex)
    {
        lock (_wccInitialFieldRefreshGate)
        {
            if (!ReferenceEquals(asset, _currentScenarioAsset) || asset.RawTextSnapshot != baseline ||
                _pristineLoadedSourceText != baseline || _hasAuthoredSourceChanges || _externalSourceChangesPending)
                return false;
            ReloadSavedScenarioSource(asset, candidate, scenarioIndex, sideIndex);
            return ReferenceEquals(asset, _currentScenarioAsset) && asset.RawTextSnapshot == candidate;
        }
    }

    // The shell calls this only for the exact owner in its completed package
    // commit publication. Synchronization hydrates fields; it cannot save them.
    internal bool TrySynchronizeReviewedScenarioSelection(ScenarioFileAsset asset, string documentId,
        string sourceText, Action publish)
    {
        if (asset.RawTextSnapshot != sourceText || !asset.Documents.Any(document => document.InternalId == documentId &&
                document.OriginalText == sourceText) || HasWccInlineDrafts || HasWccMusicScheduleDrafts || HasWccSourceRecipeDrafts ||
            _hasAuthoredSourceChanges || _externalSourceChangesPending || _collectedSaveRecoveryPending || WccFieldEditSession.Recovery is not null)
            return false;
        RunUiSync(publish);
        return ReferenceEquals(asset, _currentScenarioAsset) && SelectedScenario?.Document.InternalId == documentId &&
            asset.RawTextSnapshot == sourceText && _pristineLoadedSourceText == sourceText;
    }

    private void ReloadSavedScenarioSource(ScenarioFileAsset asset, string text, int scenarioIndex, int sideIndex)
    {
        using var previewRefresh = DeferPreviewRefresh();
        var freshDocuments = ScenarioImporter.ImportAllFromText(text);
        InvalidateSourceEditingSession();
        RunUiSync(() =>
        {
            // Detach stale selection before replacing the shared asset. Both
            // ordinary saves and reviewed saves need fresh physical-file spans.
            SelectedScenario = null;
            asset.RawTextSnapshot = text;
            asset.Documents.Clear();
            asset.Documents.AddRange(freshDocuments);
            asset.MacroDefinitions.Clear();
            asset.MacroDefinitions.AddRange(WmlMacroDefinitionScanner.Scan(text));
            LoadFromExistingScenarioAsset(asset);
            SelectedScenario = Scenarios.ElementAtOrDefault(scenarioIndex) ?? Scenarios.FirstOrDefault();
            SelectedSide = Sides.FirstOrDefault(side => GetAuthoredSideIndex(side) == sideIndex) ?? Sides.FirstOrDefault();
        });
        RefreshMapPreview();
    }

    public bool CanSaveScenarioCopyTo(string path)
    {
        if (HasWccInlineDrafts || IsWccInlineSaveBusy || HasWccMusicScheduleDrafts || (HasWccSideFlagDrafts || HasWccEventDrafts || HasWccSideTopologyDrafts || HasWccLeaderAdditionDrafts || HasWccInitialUnitDrafts || HasWccStoryDraft))
        {
            Status = "Save or discard the inline field drafts before exporting a copy.";
            return false;
        }
        if (WccFieldEditSession.IsBusy ||
            WccFieldEditSession.Recovery is not null ||
            PathsEqual(path, _currentSourcePath ?? LoadedPath) ||
            IsProtectedWesnothInstallPath(path) || File.Exists(path))
        {
            Status = "Save As needs a new file outside the Wesnoth installation. Use the editor's Save action for the current source.";
            return false;
        }
        return true;
    }
}
