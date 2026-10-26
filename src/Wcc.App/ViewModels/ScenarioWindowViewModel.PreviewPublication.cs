using System;
using System.Threading.Tasks;
using Wcc.Core.Projects;
using Wcc.Core.Scenarios;

namespace Wcc.App.ViewModels;

public partial class ScenarioWindowViewModel
{
    private int _previewRefreshDeferralDepth;
    private bool _previewRefreshPending;
    private long _previewRefreshDeferralNavigationGeneration;
    private bool _previewRefreshReuseStaticSources;
    private SavedSourcePreviewDeferral? _savedSourcePreviewDeferral;
    private long _savedSourcePreviewNavigationGeneration;

    // Source, catalog and revision bindings form one preview input. Invalidate
    // previous evidence immediately, then evaluate only the completed binding.
    public IDisposable DeferPreviewRefresh()
    {
        if (_previewRefreshDeferralDepth++ == 0)
        {
            _previewRefreshPending = false;
            _previewRefreshDeferralNavigationGeneration = _savedSourcePreviewNavigationGeneration;
            _previewRefreshReuseStaticSources = true;
            CancelBackgroundOperations();
        }
        return new PreviewRefreshDeferral(this);
    }

    private bool DeferRequestedPreviewRefresh(bool reuseStaticSources)
    {
        if (_collectedSourcePreviewDeferral is { IsCurrent: true } collected)
        {
            collected.RefreshPending = true;
            return true;
        }
        if (_previewRefreshDeferralDepth > 0 &&
            _previewRefreshDeferralNavigationGeneration == _savedSourcePreviewNavigationGeneration)
        {
            _previewRefreshPending = true;
            _previewRefreshReuseStaticSources &= reuseStaticSources;
            return true;
        }
        if (_savedSourcePreviewDeferral is { } saved)
        {
            if (saved.IsCurrent)
            {
                saved.RefreshPending = true;
                return true;
            }
            // Explicit navigation owns its own refresh; a late saved-source
            // callback cannot delay it or restore the earlier selection.
            _savedSourcePreviewDeferral = null;
        }
        return false;
    }

    private sealed class PreviewRefreshDeferral(ScenarioWindowViewModel owner) : IDisposable
    {
        private bool _disposed;
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            if (--owner._previewRefreshDeferralDepth != 0 || !owner._previewRefreshPending) return;
            var reuse = owner._previewRefreshReuseStaticSources;
            owner._previewRefreshPending = false;
            if (owner._previewRefreshDeferralNavigationGeneration == owner._savedSourcePreviewNavigationGeneration)
                owner.RefreshMapPreview(reuse);
        }
    }

    private sealed class SavedSourcePreviewDeferral(ScenarioWindowViewModel owner) : IDisposable
    {
        private readonly ScenarioDocument? _document = owner.SelectedScenario?.Document;
        private readonly string? _sourcePath = owner._currentSourcePath ?? owner.LoadedPath;
        private readonly string? _sourceText = owner._pristineLoadedSourceText;
        private readonly string _context = owner._evaluationContext.SemanticKey;
        private readonly object _host = owner._sourceHostSession;
        private readonly long _navigation = owner._savedSourcePreviewNavigationGeneration;
        public bool RefreshPending { get; set; }
        public bool IsCurrent => _navigation == owner._savedSourcePreviewNavigationGeneration &&
            ReferenceEquals(_document, owner.SelectedScenario?.Document) &&
            ReferenceEquals(_host, owner._sourceHostSession) &&
            PathsEqual(_sourcePath, owner._currentSourcePath ?? owner.LoadedPath) &&
            string.Equals(_sourceText, owner._pristineLoadedSourceText, StringComparison.Ordinal) &&
            string.Equals(_context, owner._evaluationContext.SemanticKey, StringComparison.Ordinal) &&
            !owner._hasAuthoredSourceChanges && !owner._externalSourceChangesPending;

        public void Dispose()
        {
            if (!ReferenceEquals(owner._savedSourcePreviewDeferral, this)) return;
            owner._savedSourcePreviewDeferral = null;
            if (RefreshPending && IsCurrent) owner.RefreshMapPreview();
        }
    }

    // The shell refreshes the package index before the saved-source preview is
    // published. A standalone editor needs no host hook and refreshes directly.
    public Func<Task>? SourceLibraryRefreshAsync { get; set; }

    private async Task PublishSavedScenarioSourceAsync(ScenarioFileAsset asset, string text,
        int scenarioIndex, int sideIndex, string status)
    {
        var refreshHost = SourceLibraryRefreshAsync;
        SavedSourcePreviewDeferral? saved = null;
        using (DeferPreviewRefresh())
        {
            ReloadSavedScenarioSource(asset, text, scenarioIndex, sideIndex);
            if (refreshHost is not null)
                _savedSourcePreviewDeferral = saved = new SavedSourcePreviewDeferral(this);
            Status = status;
        }
        using (saved)
        {
            ScenarioSourceSaved?.Invoke(this, EventArgs.Empty);
            if (refreshHost is null) return;
            try
            {
                await refreshHost();
            }
            catch (Exception exception)
            {
                if (saved!.IsCurrent)
                    Status = "The field change was saved, but the package index could not refresh: " + exception.Message;
            }
        }
    }
}
