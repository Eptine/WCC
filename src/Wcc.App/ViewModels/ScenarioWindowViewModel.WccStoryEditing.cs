using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading;
using Wcc.Core.Scenarios;
using Wcc.Core.Projects;
using Wcc.Core.Wml;
using Wcc.App.ViewModels.Wcc;

namespace Wcc.App.ViewModels;

/// <summary>A detached Story window binding. Apply stages intent; it never writes a source.</summary>
public sealed class WccScenarioStoryEditingSession : IDisposable
{
    private readonly Func<string?> _currentProblem;
    private readonly Func<WccScenarioStoryEdit, bool> _apply;
    private readonly string _capturedScenarioTitle;
    private readonly IReadOnlyDictionary<string, string> _capturedScenarioTitles;
    private readonly Func<string, string?>? _pendingScenarioTitle;
    private readonly SynchronizationContext? _ownerContext = SynchronizationContext.Current;
    private bool _disposed;

    internal WccScenarioStoryEditingSession(WccScenarioStorySnapshot snapshot, WccScenarioStoryEdit? pendingEdit,
        WmlEvaluationContext context, string projectRootPath, string scenarioTitle, string? sourceTextDomain, string? sourceInstallPath,
        string? runtimeInstallPath, string profileIdentity, Func<string?> currentProblem,
        Func<WccScenarioStoryEdit, bool> apply, WccStoryAudioCatalog? audioCatalog = null,
        IReadOnlyDictionary<string, string>? scenarioTitles = null, Func<string, string?>? pendingScenarioTitle = null)
    {
        Snapshot = snapshot; PendingEdit = pendingEdit; CurrentContext = context; ProjectRootPath = projectRootPath;
        _capturedScenarioTitle = scenarioTitle;
        _capturedScenarioTitles = new System.Collections.ObjectModel.ReadOnlyDictionary<string, string>(
            scenarioTitles?.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal)
            ?? new Dictionary<string, string>(StringComparer.Ordinal));
        _pendingScenarioTitle = pendingScenarioTitle;
        SourceTextDomain = sourceTextDomain; SourceInstallPath = sourceInstallPath;
        RuntimeInstallPath = runtimeInstallPath; ProfileIdentity = profileIdentity;
        _currentProblem = currentProblem; _apply = apply; AudioCatalog = audioCatalog;
    }

    public WccScenarioStorySnapshot Snapshot { get; }
    public WccScenarioStoryEdit? PendingEdit { get; }
    public WmlEvaluationContext CurrentContext { get; }
    public string SourcePath => Snapshot.Source.Path;
    public string ProjectRootPath { get; }
    public string ScenarioTitle => GetScenarioTitle(Snapshot.Contexts.FirstOrDefault(context =>
        context.SemanticKey == CurrentContext.SemanticKey)?.Define ?? "");
    public string GetScenarioTitle(string define) => OnOwner(() =>
    {
        if (!Snapshot.Contexts.Any(context => context.Define == define)) return "";
        if (!_disposed && _currentProblem() is null && _pendingScenarioTitle?.Invoke(define) is { } pending) return pending;
        if (_capturedScenarioTitles.TryGetValue(define, out var captured)) return captured;
        // Null is missing evidence; an explicit empty name is a known value.
        if (Snapshot.ScenarioTitles.TryGetValue(define, out var title) && title is not null) return title;
        return Snapshot.Contexts.Any(context => context.Define == define && context.SemanticKey == CurrentContext.SemanticKey)
            ? _capturedScenarioTitle : "";
    });
    public string? SourceTextDomain { get; }
    public string? SourceInstallPath { get; }
    public string? RuntimeInstallPath { get; }
    public string ProfileIdentity { get; }
    public WccStoryAudioCatalog? AudioCatalog { get; }
    public string? CurrentProblem => OnOwner(() => _disposed
        ? "The Story window has closed. Reopen it before applying changes." : _currentProblem());
    public bool IsCurrent => CurrentProblem is null;
    public bool Apply(WccScenarioStoryEdit edit)
    {
        ArgumentNullException.ThrowIfNull(edit);
        return OnOwner(() => !_disposed && Snapshot.CanEdit && _currentProblem() is null && _apply(edit));
    }

    public WccScenarioStoryUploadReceipt CaptureUpload(WccScenarioStoryAssetSlot slot, string sourcePath,
        string? preferredFileName = null, WccBinaryDiskRevision? expectedSourceRevision = null) => OnOwner(() =>
    {
        if (_disposed) throw new InvalidOperationException("The Story window has closed.");
        if (!Snapshot.CanEdit) throw new InvalidOperationException(Snapshot.Diagnostic);
        if (_currentProblem() is { } problem) throw new InvalidOperationException(problem);
        var receipt = WccScenarioStoryPlanner.CaptureUpload(Snapshot, slot, ProjectRootPath, sourcePath, preferredFileName, expectedSourceRevision);
        if (_disposed) throw new InvalidOperationException("The Story window has closed.");
        if (_currentProblem() is { } changed) throw new InvalidOperationException(changed);
        return receipt;
    });

    private T OnOwner<T>(Func<T> action)
    {
        if (_ownerContext is null || ReferenceEquals(SynchronizationContext.Current, _ownerContext)) return action();
        T result = default!;
        ExceptionDispatchInfo? error = null;
        _ownerContext.Send(_ =>
        {
            try { result = action(); }
            catch (Exception exception) { error = ExceptionDispatchInfo.Capture(exception); }
        }, null);
        error?.Throw();
        return result;
    }

    public void Dispose() => OnOwner(() => _disposed = true);
}

public sealed record WccScenarioStoryPendingChange(string Label, string Summary);

/// <summary>Frozen pending Story intent. Fresh Core review supplies write authority separately.</summary>
public sealed class WccScenarioStoryDraftContribution
{
    internal WccScenarioStoryDraftContribution(object editor, object binding, long version,
        WccScenarioStoryEdit edit) { Editor = editor; Binding = binding; Version = version; Edit = edit; }
    internal object Editor { get; }
    internal object Binding { get; }
    internal long Version { get; }
    public WccScenarioStoryEdit Edit { get; }
    public bool AuthorizesPersistence => false;
}

public partial class ScenarioWindowViewModel
{
    private WccMusicScheduleSourceBinding? _storySourceBinding;
    private WccScenarioStorySnapshot? _storySnapshot;
    private WccScenarioStoryEdit? _storyEdit;
    private WccScenarioStoryMenuBinding? _storyMenuBinding;
    private long _storyDraftVersion;
    private long _storyDifficultyBindingVersion;
    private IReadOnlyList<WccScenarioStoryPendingChange> _storyPendingChanges = [];

    public bool HasWccStoryDraft => _storyEdit is not null;
    public IReadOnlyList<WccScenarioStoryPendingChange> WccStoryPendingChanges => _storyPendingChanges;
    public event EventHandler? WccStoryDraftChanged;
    public string WccStoryActionLabel => IsReadOnly || _campaignPresetHost?.IsReadOnly == true || !_sourceHostSession.CanWrite || IsProtectedWesnothInstallPath(_currentSourcePath ?? LoadedPath)
        ? "View introduction…" : "Edit introduction…";
    public bool CanOpenWccStoryEditing => CanInspectWccScenarioSource() && !_collectedSaveRecoveryPending &&
        !_changingWccDifficultyDrafts && HasCurrentStaticScalarProjection && _staticScenarioOwner is not null &&
        _sourceEvaluationPlan is not null && (_storySourceBinding is null || StoryBindingProblem(_storySourceBinding) is null) &&
        (_storyMenuBinding ?? CaptureStoryMenuBinding()) is not null;

    public WccScenarioStoryEditingSession? BeginWccStoryEditingSession(Func<bool>? hostIsCurrent = null)
    {
        lock (_wccInitialFieldRefreshGate)
        {
            if (!CanOpenWccStoryEditing || hostIsCurrent?.Invoke() == false) return null;
            var binding = _storySourceBinding ?? CaptureEventSourceBinding();
            var menu = _storyMenuBinding ?? CaptureStoryMenuBinding();
            if (menu is null) return null;
            var version = _storyDraftVersion;
            var difficultyBindingVersion = _wccDifficultyBindingVersion;
            try
            {
                var snapshot = _storySnapshot ?? WccScenarioStoryPlanner.Read(binding.Range.Source, binding.Range,
                    menu.Plan, GetWccStoryDestinationTextDomain(menu));
                var writable = CanCaptureWccMusicScheduleSource() && menu.CanWrite;
                var captured = CaptureCompletedStoryProjections(binding);
                var inspected = CaptureCompletedStoryInspections(binding);
                if (_storySnapshot is null)
                {
                    snapshot = !writable && inspected.Count > 0
                        ? WccScenarioStoryPlanner.WithScenarioInspectionReadOnlyPreview(snapshot, inspected)
                        : writable ? WccScenarioStoryPlanner.WithCapturedPreview(snapshot, captured)
                        : WccScenarioStoryPlanner.WithCapturedReadOnlyPreview(snapshot, captured);
                    if (writable && binding.Host.Scope == WccEditorSessionScope.Project)
                        snapshot = WccScenarioStoryPlanner.WithUniqueWritableDefinition(snapshot, captured, binding.Host.RootPath!, CaptureCompletedStorySourceInventory(binding));
                }
                if (!writable)
                    snapshot = WccScenarioStoryPlanner.AsReadOnlyPreview(snapshot,
                        "Read-only Story preview. Open an editable campaign copy to change these slides; the original WML remains available in Source.");
                var hostGeneration = _sourceHostGeneration;
                var readOnly = IsReadOnly;
                var inspectionBinding = _scenarioInspectionBinding;
                var currentInspection = _publishedScenarioInspection;
                string? CurrentProblem() => writable
                    ? StorySessionProblem(binding, menu, snapshot, version, difficultyBindingVersion, hostIsCurrent)
                    : !ReferenceEquals(inspectionBinding, _scenarioInspectionBinding) || !ReferenceEquals(currentInspection, _publishedScenarioInspection)
                        ? "The captured scenario inspection changed. Reopen the Story preview."
                        : StoryPreviewSessionProblem(binding, menu, version, difficultyBindingVersion, hostGeneration, readOnly, hostIsCurrent);
                if (inspected.Count > 0)
                    captured = captured.Concat(inspected.Select(pair => new KeyValuePair<string, WmlStaticScalarProjection>(pair.Key, pair.Value.Projection)))
                        .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
                var titleCapture = CaptureStoryScenarioTitles(snapshot, binding, menu.Plan, captured,
                    CaptureCompleteStoryTitleContexts(binding, captured));
                var currentDefine = snapshot.Contexts.FirstOrDefault(context => context.SemanticKey == _evaluationContext.SemanticKey)?.Define;
                var audioScopes = snapshot.Contexts.ToDictionary(context => context.Define,
                    context => CaptureDependencyAudioScope(context.SemanticKey), StringComparer.Ordinal);
                var scenarioTitle = currentDefine is not null && titleCapture.Titles.TryGetValue(currentDefine, out var currentTitle) ? currentTitle : "";
                return new(snapshot, _storyEdit, _evaluationContext, binding.Host.RootPath!, scenarioTitle, binding.TextDomain, binding.InstallPath,
                    _wesnothInstallPath, binding.ProfileIdentity, CurrentProblem,
                    edit => writable && StageWccStoryEdit(binding, menu, snapshot, version, difficultyBindingVersion, hostIsCurrent, edit),
                    _macroLibrarySnapshot is { } library && _staticRootRevisionSelection is { } root
                        ? new WccStoryAudioCatalog(library, GetInitialMusicPickerRevisions(library), root.RootRevisionId,
                            define => (define ?? currentDefine) is { } selected && audioScopes.TryGetValue(selected, out var audio) ? audio : null) : null,
                    titleCapture.Titles, define => GetPendingStoryScenarioTitle(titleCapture, define));
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
            { Status = exception.Message; return null; }
        }
    }

    private IReadOnlyDictionary<string, WmlStaticScalarProjection> CaptureCompletedStoryProjections(WccMusicScheduleSourceBinding binding)
    {
        var result = new Dictionary<string, WmlStaticScalarProjection>(StringComparer.Ordinal);
        foreach (var entry in _staticEvaluationCache.CompletedValues())
            if (entry.Key is StaticEvaluationKey key && entry.Value.Inspection is null && entry.Value.Projection is { } projection
                && PathsEqual(key.SourcePath, binding.Range.Source.Path) && key.SourceHash == binding.Range.Source.ContentHash
                && ReferenceEquals(key.Snapshot, _macroLibrarySnapshot) && Equals(key.RootSelection, _staticRootRevisionSelection)
                && key.ProfileIdentity == binding.ProfileIdentity && ReferenceEquals(key.Plan, _sourceEvaluationPlan))
                result[key.ContextKey] = projection;
        if (_publishedScenarioInspection is null && _staticScenarioTopologyProjection is { } current && HasCurrentStaticScalarProjection)
            result[_evaluationContext.SemanticKey] = current;
        return result;
    }

    private IReadOnlyDictionary<string, WmlScenarioInspectionResult> CaptureCompletedStoryInspections(WccMusicScheduleSourceBinding binding)
    {
        var result = new Dictionary<string, WmlScenarioInspectionResult>(StringComparer.Ordinal);
        foreach (var entry in _staticEvaluationCache.CompletedValues())
            if (entry.Key is StaticEvaluationKey key && entry.Value.Inspection is { IsComplete: true } inspection &&
                PathsEqual(key.SourcePath, binding.Range.Source.Path) && key.SourceHash == binding.Range.Source.ContentHash &&
                ReferenceEquals(key.Snapshot, binding.Snapshot) && Equals(key.RootSelection, _staticRootRevisionSelection) &&
                ReferenceEquals(key.Plan, binding.Plan) && ReferenceEquals(key.InspectionBinding, _scenarioInspectionBinding) &&
                key.ProfileIdentity == binding.ProfileIdentity && SameOptionalWccInlinePath(key.AddonsPath, binding.AddonsPath) &&
                MusicScheduleRevisionSignature(key.VisibleRevisions) == binding.MacroVisibleRevisions &&
                (key.InspectionBinding is null || HasCurrentScenarioInspectionBinding(key.InspectionBinding, binding.Range.Source,
                    key.Plan, key.Snapshot, key.RootSelection, key.VisibleRevisions, key.AddonsPath, key.ProfileIdentity)))
                result[key.ContextKey] = inspection;
        return result;
    }

    private IReadOnlyList<WmlStaticSource> CaptureCompletedStorySourceInventory(WccMusicScheduleSourceBinding binding)
    {
        if (_staticSourceCaptureCache.CompletedValue() is { Key: StaticCaptureKey key, Value: { IsCaptureComplete: true } capture }
            && PathsEqual(key.SourcePath, binding.Range.Source.Path) && key.SourceHash == binding.Range.Source.ContentHash
            && ReferenceEquals(key.Snapshot, _macroLibrarySnapshot) && key.RootRevision == _staticRootRevisionSelection?.RootRevisionId
            && key.ProfileIdentity == binding.ProfileIdentity && ReferenceEquals(key.Plan, _sourceEvaluationPlan)) return capture.Sources;
        return [];
    }

    private string? StoryBindingProblem(WccMusicScheduleSourceBinding binding) =>
        GetWccMusicScheduleBindingProblem(binding)?.Replace("Music and schedule", "Story", StringComparison.Ordinal)
            .Replace("music and schedule", "Story", StringComparison.Ordinal) ??
        (_collectedSaveRecoveryPending ? "A previous save requires recovery. Story drafts are retained." : null) ??
        (_storyEdit is not null && (_storyMenuBinding is not { } menu || !IsCurrentStoryMenuBinding(menu))
            ? "The captured campaign menu changed. Story drafts are retained." : null) ??
        (_storyEdit is not null && _storyDifficultyBindingVersion != _wccDifficultyBindingVersion
            ? "The captured campaign source or difficulty authoring metadata changed. Story drafts are retained." : null) ??
        (_storySnapshot is { } snapshot && snapshot.DestinationTextDomain != GetWccStoryDestinationTextDomain(_storyMenuBinding)
            ? "The campaign authoring textdomain changed. Story drafts are retained." : null);

    private string? StoryPreviewSessionProblem(WccMusicScheduleSourceBinding binding, WccScenarioStoryMenuBinding menu,
        long version, long difficultyBindingVersion, long hostGeneration, bool readOnly, Func<bool>? hostIsCurrent)
    {
        lock (_wccInitialFieldRefreshGate)
        {
            if (hostIsCurrent?.Invoke() == false || hostGeneration != _sourceHostGeneration || readOnly != IsReadOnly ||
                version != _storyDraftVersion || difficultyBindingVersion != _wccDifficultyBindingVersion || !IsCurrentStoryMenuBinding(menu))
                return "The captured campaign or scenario changed. Reopen the Story preview.";
            return GetWccScenarioSourceBindingProblem(binding)?.Replace("Music and schedule", "Story", StringComparison.Ordinal)
                .Replace("music and schedule", "Story", StringComparison.Ordinal) ??
                (_collectedSaveRecoveryPending ? "A previous save requires recovery. Reopen Story after recovery." : null);
        }
    }

    private WccScenarioStoryMenuBinding? CaptureStoryMenuBinding()
    {
        var menu = _campaignPresetHost?.CaptureWccStoryMenuBinding(out _);
        return menu is not null && IsCurrentStoryMenuBinding(menu) ? menu : null;
    }

    private bool IsCurrentStoryMenuBinding(WccScenarioStoryMenuBinding menu) => menu.IsCurrent() &&
        ReferenceEquals(menu.Snapshot, _macroLibrarySnapshot) && menu.RootRevision == _staticRootRevisionSelection?.RootRevisionId &&
        menu.ProfileIdentity == _wesnothSourceProfileIdentity && menu.Host.Scope == _sourceHostSession.Scope &&
        menu.Host.AccessMode == _sourceHostSession.AccessMode && PathsEqual(menu.Host.RootPath, _sourceHostSession.RootPath) &&
        _sourceEvaluationPlan is { } supplied && PathsEqual(menu.Source.Path, supplied.CampaignMainSourcePath) &&
        menu.Source.ContentHash == supplied.CampaignMainTextHash && menu.Plan.CampaignId == supplied.CampaignId &&
        menu.Plan.CampaignDefine == supplied.CampaignDefine && menu.Plan.Contexts.Select(context => context.SemanticKey)
            .SequenceEqual(supplied.Contexts.Select(context => context.SemanticKey), StringComparer.Ordinal);

    private string? GetWccStoryDestinationTextDomain(WccScenarioStoryMenuBinding? menu)
    {
        if (menu?.DestinationTextDomain is { } domain) return domain;
        if (menu is not { Source: { } main, Plan: { AuthoredCampaignSpan: { } span } plan } ||
            !PathsEqual(main.Path, plan.CampaignMainSourcePath) || main.ContentHash != plan.CampaignMainTextHash ||
            span.TagName != "campaign" || span.StartIndex < 0 || span.EndIndexExclusive > main.Text.Length ||
            main.Text[span.StartIndex..span.EndIndexExclusive] != span.RawText) return null;
        return WmlTextDomainCapture.BeforeOffset(main.Text, span.StartIndex);
    }

    private string? StorySessionProblem(WccMusicScheduleSourceBinding binding, WccScenarioStoryMenuBinding menu, WccScenarioStorySnapshot snapshot,
        long version, long difficultyBindingVersion, Func<bool>? hostIsCurrent)
    {
        lock (_wccInitialFieldRefreshGate)
        {
            if (hostIsCurrent?.Invoke() == false) return "The Story window's scenario host changed. Reopen it; your draft remains in the window.";
            if (!IsCurrentStoryMenuBinding(menu) || _storyMenuBinding is not null && !ReferenceEquals(menu, _storyMenuBinding))
                return "The captured campaign menu changed. Reopen the Story window; its draft is retained.";
            if (difficultyBindingVersion != _wccDifficultyBindingVersion)
                return "The captured campaign source or difficulty authoring metadata changed. Reopen the Story window; its draft is retained.";
            if (version != _storyDraftVersion || _storySourceBinding is not null && !ReferenceEquals(binding, _storySourceBinding) ||
                _storySnapshot is not null && !ReferenceEquals(snapshot, _storySnapshot))
                return "The pending Story changed. Reopen the window; newer changes are retained.";
            if (snapshot.DestinationTextDomain != GetWccStoryDestinationTextDomain(menu))
                return "The campaign authoring textdomain changed. Reopen the Story window; its draft is retained.";
            return StoryBindingProblem(binding) ??
                (IsWccInlineSaveBusy || WccFieldEditSession.IsBusy ||
                    _campaignPresetHost?.IsCampaignFieldSaveBusy == true || _changingWccDifficultyDrafts
                    ? "Finish the current scenario operation before applying Story changes." : null);
        }
    }

    private bool StageWccStoryEdit(WccMusicScheduleSourceBinding binding, WccScenarioStoryMenuBinding menu, WccScenarioStorySnapshot snapshot,
        long version, long difficultyBindingVersion, Func<bool>? hostIsCurrent, WccScenarioStoryEdit edit)
    {
        lock (_wccInitialFieldRefreshGate)
        {
            if (StorySessionProblem(binding, menu, snapshot, version, difficultyBindingVersion, hostIsCurrent) is { } problem)
            { Status = problem; return false; }
            if (!ReferenceEquals(edit.Snapshot, snapshot))
            { Status = "The Story edit does not belong to this captured scenario. Reopen the Story window."; return false; }
            try
            {
                var changed = edit.HasChanges;
                if (StorySessionProblem(binding, menu, snapshot, version, difficultyBindingVersion, hostIsCurrent) is { } scalarProblem)
                { Status = scalarProblem; return false; }
                WccFieldEditSession.Invalidate();
                if (StorySessionProblem(binding, menu, snapshot, version, difficultyBindingVersion, hostIsCurrent) is { } fieldProblem)
                { Status = fieldProblem; return false; }
                _storySourceBinding = changed ? binding : null;
                _storySnapshot = changed ? snapshot : null;
                _storyEdit = changed ? edit : null;
                _storyMenuBinding = changed ? menu : null;
                _storyDifficultyBindingVersion = difficultyBindingVersion;
                _storyPendingChanges = changed ? [new("Introduction", "Story slides and appearance")] : [];
                _storyDraftVersion++;
                var stagedVersion = _storyDraftVersion;
                Status = changed ? "Story changes staged. Save the scenario to write all pending changes together." : "Story matches the saved source.";
                RaiseWccStoryDraftChanged();
                return stagedVersion == _storyDraftVersion && StoryBindingProblem(binding) is null;
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
            { Status = exception.Message; return false; }
        }
    }

    internal bool TryCaptureWccStoryContribution(WmlStaticSource source,
        out WccScenarioStoryDraftContribution? contribution, out string? problem)
    {
        lock (_wccInitialFieldRefreshGate)
        {
            contribution = null;
            if (_storyEdit is not { } edit || _storySourceBinding is not { } binding)
            { problem = "There are no pending Story changes."; return false; }
            problem = StoryBindingProblem(binding);
            if (problem is not null) return false;
            if (!SameAuthoredFlagSource(source, binding.Range.Source))
            { problem = "The collected source differs from the pending Story. Its draft is retained."; return false; }
            contribution = new(this, binding, _storyDraftVersion, edit);
            return true;
        }
    }

    internal bool IsCurrentWccStoryContribution(WccScenarioStoryDraftContribution contribution)
    {
        lock (_wccInitialFieldRefreshGate)
            return HasWccStoryDraft && ReferenceEquals(contribution.Editor, this) &&
                ReferenceEquals(contribution.Binding, _storySourceBinding) && contribution.Version == _storyDraftVersion &&
                ReferenceEquals(contribution.Edit, _storyEdit) && _storySourceBinding is { } binding && StoryBindingProblem(binding) is null;
    }

    internal bool CompleteWccStoryContribution(WccScenarioStoryDraftContribution contribution)
    {
        lock (_wccInitialFieldRefreshGate)
        {
            if (!IsCurrentWccStoryContribution(contribution)) return false;
            ClearWccStoryDraft(); RaiseWccStoryDraftChanged(); return true;
        }
    }

    public void DiscardWccStoryDraft()
    {
        lock (_wccInitialFieldRefreshGate)
        {
            if (IsWccInlineSaveBusy || _changingWccDifficultyDrafts) return;
            ClearWccStoryDraft(); RaiseWccStoryDraftChanged();
        }
    }

    private void ClearWccStoryDraft()
    { _storyEdit = null; _storySnapshot = null; _storySourceBinding = null; _storyMenuBinding = null; _storyPendingChanges = []; _storyDifficultyBindingVersion = 0; _storyDraftVersion++; }

    internal string GetPendingWccStoryChangeStamp() => _storyDraftVersion.ToString(System.Globalization.CultureInfo.InvariantCulture);
    private void RaiseWccStoryCapabilitiesChanged()
    { OnPropertyChanged(nameof(CanOpenWccStoryEditing)); OnPropertyChanged(nameof(WccStoryActionLabel)); }
    private void RaiseWccStoryDraftChanged()
    {
        CollectedSaveAuthorityChanging?.Invoke();
        OnPropertyChanged(nameof(HasWccStoryDraft)); OnPropertyChanged(nameof(WccStoryPendingChanges));
        OnPropertyChanged(nameof(CanEditDocument)); RaiseWccStoryCapabilitiesChanged();
        RaiseInitialScalarFieldsChanged(); WccStoryDraftChanged?.Invoke(this, EventArgs.Empty);
    }
}
