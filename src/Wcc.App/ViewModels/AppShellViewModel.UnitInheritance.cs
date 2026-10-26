using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Wcc.App.ViewModels.Wcc;
using Wcc.Core;
using Wcc.Core.Library;
using Wcc.Core.Projects;
using Wcc.Core.References;
using Wcc.Core.Scenarios;
using Wcc.Core.Units;
using Wcc.Core.Wml;

namespace Wcc.App.ViewModels;

public partial class AppShellViewModel
{
    // These are captured display receipts, never editor or save authority. One
    // evaluation serves every derived unit in the same exact package/context.
    private WccLibrarySnapshot? _unitDisplaySnapshot;
    private readonly object _unitDisplaySync = new();
    private readonly Dictionary<string, UnitDisplayWork> _unitDisplayScopes = new(StringComparer.Ordinal);
    private readonly HashSet<string> _unitDisplayPublications = new(StringComparer.Ordinal);
    private readonly HashSet<string> _unitDisplayGroupingRequests = new(StringComparer.Ordinal);
    private readonly Dictionary<string, UnitDisplayGrouping> _unitDisplaySummaries = new(StringComparer.Ordinal);

    private readonly Dictionary<MainWindowViewModel, long> _unitDraftDisplayRequests = new();
    private readonly Dictionary<MainWindowViewModel, UnitDisplayLease> _unitEditorDisplayLeases = new();
    private UnitDisplayLease? _unitCampaignContextLease;
    private readonly Dictionary<MainWindowViewModel, CancellationTokenSource> _unitDraftDisplayCancellations = new();
    private readonly SemaphoreSlim _unitDisplayCaptureGate = new(1, 1);
    private readonly HashSet<Task> _unitDisplayWorkers = new();
    private long _unitDisplayAccess;
    private long _unitDisplayEpoch;
    private const int MaximumIdleUnitDisplayScopes = 2;
    // This is an idle retention policy, not a RAM estimate or evaluator limit.
    // Large expanded graphs remain usable by active editors/current campaign
    // workspace and readers, but are not kept
    // merely to accelerate an eventual return to an old context.
    private const long MaximumIdleUnitDisplayCharacters = 16 * 1024 * 1024;

    private sealed class UnitDisplayWork(UnitDisplayBinding binding, long epoch)
    {
        public UnitDisplayBinding Binding { get; } = binding;
        public long Epoch { get; } = epoch;
        public UnitDisplayWorkLifetime Lifetime { get; } = new();
        public TaskCompletionSource<UnitDisplayScope?> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<UnitDisplayScope?> Task => Completion.Task;
        public TaskCompletionSource? GroupingCompletion { get; set; }
        public long LastAccess { get; set; }
    }

    private sealed class UnitDisplayLease(AppShellViewModel host, UnitDisplayWork work, IDisposable reader) : IDisposable
    {
        private IDisposable? _reader = reader;
        public UnitDisplayWork Work { get; } = work;
        public Task<UnitDisplayScope?> Task => Work.Task;
        public CancellationToken Token => Work.Lifetime.Token;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _reader, null) is not { } active) return;
            active.Dispose();
            host.TrimUnitDisplayScopes();
        }
    }

    private sealed class UnitDisplayScope(WccStaticSourceCaptureResult capture,
        WmlStaticEvaluationRequest request, WmlStaticEvaluationResult evaluation, WmlStaticScalarProjection projection,
        WmlScenarioInspectionResult? imageInspection = null)
    {
        private readonly Dictionary<(string Path, string Hash, int Start, int Length), WccUnitTypeEffectiveDisplay?> _displays = new();
        private readonly object _drafts = new();
        private string? _completedDraftKey;
        private UnitDisplayScope? _completedDraft;
        private UnitDraftEvaluation? _pendingDraft;
        private UnitDraftEvaluation? _runningDraft;
        private bool _draftWorkerRunning;
        private bool _retired;
        private Task _draftRetirements = Task.CompletedTask;
        public CancellationToken LifetimeCancellation { get; init; }
        public Task DraftWorkerTask { get; private set; } = Task.CompletedTask;
        public Task CancellationCompletion { get { lock (_drafts) return _draftRetirements; } }
        private sealed record UnitDraftEvaluation(string Key, WmlStaticSource Original, WmlStaticSource Candidate,
            TaskCompletionSource<UnitDisplayScope?> Completion, UnitDisplayWorkLifetime Lifetime)
        {
            public bool Abandoned { get; set; }
        }
        public WccStaticSourceCaptureResult Capture { get; } = capture;
        public WmlStaticEvaluationResult Evaluation { get; } = evaluation;
        public WmlStaticScalarProjection Projection { get; } = projection;
        public long RetainedExpandedCharacters
        {
            get { lock (_drafts) return (long)Evaluation.Text.Length + (_completedDraft?.Evaluation.Text.Length ?? 0); }
        }
        private readonly object _unitRegistrySync = new();
        private WmlUnitReferenceProjection? _unitRegistry;
        public WmlUnitReferenceProjection UnitRegistry => GetUnitRegistry(LifetimeCancellation);
        private WmlUnitReferenceProjection GetUnitRegistry(CancellationToken cancellationToken)
        {
            lock (_unitRegistrySync)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return _unitRegistry ??= WmlUnitReferenceProjection.Create(evaluation, projection, cancellationToken);
            }
        }
        private readonly Lazy<WccCapturedImageResourceContext> _imageResources = new(() =>
            imageInspection is null ? WccCapturedImageResourceContext.Create(capture, evaluation, projection)
                : WccCapturedImageResourceContext.Create(capture, imageInspection));
        public WccCapturedImageResourceContext ImageResources => _imageResources.Value;

        public async Task<UnitDisplayScope?> EvaluateDraftAsync(WmlStaticSource original, string text, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LifetimeCancellation.ThrowIfCancellationRequested();
            if (text == original.Text) return this;
            var candidate = new WmlStaticSource(original.Path, text, original.IsAuthored);
            var key = original.Path + "\u001f" + original.ContentHash + "\u001f" + candidate.ContentHash;
            UnitDraftEvaluation work;
            UnitDraftEvaluation? retiredPending = null;
            IDisposable? reader;
            lock (_drafts)
            {
                if (_retired) return null;
                if (_completedDraftKey == key) return _completedDraft;
                if (_runningDraft is { } running && running.Key == key && !running.Abandoned && !running.Lifetime.IsRetired &&
                    !running.Lifetime.Token.IsCancellationRequested) work = running;
                else if (_pendingDraft is { } pending && pending.Key == key) work = pending;
                else
                {
                // Retain only one running evaluation and the latest waiting draft.
                // Superseded waiting requests complete without a stale projection.
                retiredPending = _pendingDraft;
                work = new UnitDraftEvaluation(key, original, candidate,
                    new(TaskCreationOptions.RunContinuationsAsynchronously), new UnitDisplayWorkLifetime(LifetimeCancellation));
                _pendingDraft = work;
                }
                reader = work.Lifetime.Acquire();
                if (!_draftWorkerRunning)
                {
                    _draftWorkerRunning = true;
                    DraftWorkerTask = Task.Run(EvaluatePendingDrafts);
                }
            }
            SettlePendingDraft(retiredPending);
            try { return await work.Completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false); }
            finally
            {
                reader?.Dispose();
                UnitDisplayWorkLifetime? cancel = null;
                UnitDraftEvaluation? abandonedPending = null;
                lock (_drafts)
                {
                    if (work.Lifetime.Readers == 0 && ReferenceEquals(work, _runningDraft))
                    {
                        // Stop new callers joining before cancellation callbacks
                        // run outside this lock. A later identical request queues
                        // its own work instead of inheriting abandoned work.
                        work.Abandoned = true;
                        cancel = work.Lifetime;
                    }
                    if (work.Lifetime.Readers == 0 && ReferenceEquals(work, _pendingDraft))
                    {
                        _pendingDraft = null;
                        abandonedPending = work;
                    }
                }
                SettlePendingDraft(abandonedPending);
                if (cancel is not null) RetireDraftLifetime(cancel);
            }
        }

        public void Retire()
        {
            UnitDisplayWorkLifetime? running;
            UnitDraftEvaluation? pending;
            lock (_drafts)
            {
                if (_retired) return;
                _retired = true;
                _completedDraftKey = null;
                _completedDraft = null;
                pending = _pendingDraft;
                _pendingDraft = null;
                running = _runningDraft?.Lifetime;
            }
            SettlePendingDraft(pending);
            if (running is not null) RetireDraftLifetime(running);
        }

        private void SettlePendingDraft(UnitDraftEvaluation? pending)
        {
            if (pending is null) return;
            pending.Completion.TrySetResult(null);
            pending.Lifetime.Complete();
            RetireDraftLifetime(pending.Lifetime);
        }

        private void RetireDraftLifetime(UnitDisplayWorkLifetime lifetime)
        {
            lifetime.Dispose();
            lock (_drafts) _draftRetirements = _draftRetirements.IsCompleted
                ? lifetime.CancellationCompletion : Task.WhenAll(_draftRetirements, lifetime.CancellationCompletion);
        }

        private void EvaluatePendingDrafts()
        {
            while (true)
            {
                UnitDraftEvaluation work;
                lock (_drafts)
                {
                    if (_pendingDraft is null) { _draftWorkerRunning = false; return; }
                    work = _pendingDraft;
                    _runningDraft = work;
                    _pendingDraft = null;
                }
                try
                {
                    var resolver = new UnitDraftDisplayResolver(Capture.IncludeResolver, work.Original, work.Candidate);
                    var draftRequest = request with
                    {
                        EntrySource = resolver.Replace(request.EntrySource),
                        PreludeSources = request.PreludeSources?.Select(resolver.Replace).ToArray(),
                        IncludeResolver = resolver
                    };
                    var token = work.Lifetime.Token;
                    token.ThrowIfCancellationRequested();
                    var result = new WmlStaticEvaluator().Evaluate(draftRequest, token);
                    var scope = new UnitDisplayScope(Capture, draftRequest, result, WmlStaticScalarProjection.Create(result, token))
                        { LifetimeCancellation = LifetimeCancellation };
                    lock (_drafts)
                    {
                        if (_retired || token.IsCancellationRequested) { work.Completion.TrySetResult(null); continue; }
                        _completedDraftKey = work.Key;
                        _completedDraft = scope;
                    }
                    work.Completion.TrySetResult(scope);
                }
                catch (OperationCanceledException) { work.Completion.TrySetResult(null); }
                catch (Exception failure) { work.Completion.TrySetException(failure); }
                finally
                {
                    lock (_drafts) _runningDraft = null;
                    work.Lifetime.Complete();
                    RetireDraftLifetime(work.Lifetime);
                }
            }
        }

        public WccUnitTypeEffectiveDisplay? Find(string path, string contentHash, int start, int length)
            => Find(path, contentHash, start, length, CancellationToken.None);

        public WccUnitTypeEffectiveDisplay? Find(string path, string contentHash, int start, int length, CancellationToken cancellationToken)
        {
            using var linked = cancellationToken.CanBeCanceled && LifetimeCancellation.CanBeCanceled
                ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, LifetimeCancellation) : null;
            var token = linked?.Token ?? (cancellationToken.CanBeCanceled ? cancellationToken : LifetimeCancellation);
            lock (_displays)
            {
            token.ThrowIfCancellationRequested();
            if (Volatile.Read(ref _retired)) return null;
            var key = (path, contentHash, start, length);
            if (_displays.TryGetValue(key, out var cached)) return cached;
            var candidates = Projection.Owners.Where(owner => owner.TagName == "unit_type" &&
                owner.Parent is { TagName: "units", Parent: null } &&
                owner.OpeningOrigin is { } opening && owner.ClosingOrigin is { } closing &&
                PathsEqual(opening.Source.Path, path) && opening.Source.ContentHash == contentHash &&
                PathsEqual(closing.Source.Path, path) && closing.Source.ContentHash == contentHash &&
                opening.StartOffset >= start && opening.StartOffset < start + length &&
                closing.StartOffset >= start && closing.StartOffset + closing.Length <= start + length).ToArray();
            if (candidates.Length != 1) { _displays[key] = null; return null; }
            var selected = candidates[0];
            WccUnitTypeEffectiveDisplay? display;
                try { display = WccUnitTypeEffectiveDisplay.Create(Evaluation, Projection, selected, GetUnitRegistry(token), token); }
                catch (Exception failure) when (failure is ArgumentException or InvalidOperationException) { display = null; }
            token.ThrowIfCancellationRequested();
            if (Volatile.Read(ref _retired)) return null;
            _displays[key] = display;
            return display;
            }
        }
    }

    // A display-only replacement of one proved captured file. This delegates the
    // captured namespace, ambiguity, existence and resource rules unchanged; it
    // never grants write authority or resolves a previously unavailable source.
    private sealed class UnitDraftDisplayResolver(IWmlStaticIncludeResolver captured,
        WmlStaticSource original, WmlStaticSource candidate) : IWmlStaticExistenceResolver, IWmlStaticResourceResolver
    {
        public WmlStaticSource Replace(WmlStaticSource source) =>
            PathsEqual(source.Path, original.Path) && source.ContentHash == original.ContentHash ? candidate : source;
        public WmlStaticIncludeResolution Resolve(string includePath, WmlStaticSource includingSource)
        {
            var result = captured.Resolve(includePath, includingSource);
            return result with { Sources = result.Sources.Select(Replace).ToArray() };
        }
        public WmlStaticIncludeResolution ResolveExistence(string includePath, WmlStaticSource includingSource) =>
            captured is IWmlStaticExistenceResolver existence ? existence.ResolveExistence(includePath, includingSource) :
                new(WmlStaticTruth.Unknown, [], "", "The captured resolver has no existence receipt.");
        public IReadOnlyList<string> InstallationResourceFolders => captured is IWmlStaticResourceResolver resources
            ? resources.InstallationResourceFolders : [];
        public WmlStaticIncludeResolution ResolveResource(string virtualPath, WmlStaticSource includingSource) =>
            captured is IWmlStaticResourceResolver resources ? resources.ResolveResource(virtualPath, includingSource) :
                new(WmlStaticTruth.Unknown, [], "", "The captured resolver has no resource receipt.");
    }

    private sealed record UnitDisplayBinding(WccLibrarySnapshot Snapshot, WccPackageRevisionId Revision,
        string EntryPath, string PackageRoot, WmlEvaluationContext Context, WmlCampaignEvaluationPlan? Plan,
        bool Campaign, bool Core, bool Managed, string ProfileIdentity, string? AddonsPath)
    {
        public string[] UndefinedSymbols => (Plan?.Difficulties.Select(row => row.Define) ?? [])
            .Concat(["EDITOR", "MULTIPLAYER"]).Where(symbol => !Context.DefinedSymbols.Contains(symbol))
            .Distinct(StringComparer.Ordinal).OrderBy(symbol => symbol, StringComparer.Ordinal).ToArray();
        public string Key => Revision.Value + "\u001f" + EntryPath + "\u001f" + Context.SemanticKey + "\u001f" +
            ProfileIdentity + "\u001f" + AddonsPath + "\u001f" + Plan?.CampaignMainTextHash + "\u001f" + string.Join("\u001e", UndefinedSymbols);
    }

    // Sidebar receipts deliberately contain detached documents only. Retaining a
    // scalar owner here would retain its parent units container and sibling graph.
    private sealed record UnitDisplaySummary(string? EffectiveRace, UnitTypeDocument PreviewDocument);
    private sealed record UnitDisplayGrouping(UnitDisplayBinding Binding,
        IReadOnlyDictionary<UnitDisplayLookup, UnitDisplaySummary?> Values);

    private void InitializeUnitInheritanceDisplay()
    {
        ScenarioEditor.InitialUnitGenderResolver = ResolveInitialUnitGendersAsync;
        CampaignEditor.PresetUnitGenderResolver = ResolveInitialUnitGendersAsync;
        CampaignEditor.PresetEventSpeakerIds = ScenarioEditor.PresetEventSpeakerIds;
        CampaignEditor.PresetEventMapProvider = () => ScenarioEditor.MapPreview;
        ScenarioEditor.LeaderUnitPreviewResolver = ResolveContextualLeaderUnitPreviewAsync;
        UnitEditor.PropertyChanged += (_, change) =>
        {
            if (!UnitEditor.IsPublishingUnitSource && (change.PropertyName is nameof(MainWindowViewModel.SelectedUnit) or nameof(MainWindowViewModel.LoadedPath) or nameof(MainWindowViewModel.EditVersion) or nameof(MainWindowViewModel.IsPublishingUnitSource) ||
                !UnitEditor.IsSynchronizingUi && IsUnitEditorMutationProperty(change.PropertyName)))
                _ = RefreshUnitInheritanceDisplayAsync(UnitEditor);
        };
        LibraryUnitEditor.PropertyChanged += (_, change) =>
        {
            if (!LibraryUnitEditor.IsPublishingUnitSource && (change.PropertyName is nameof(MainWindowViewModel.SelectedUnit) or nameof(MainWindowViewModel.LoadedPath) or nameof(MainWindowViewModel.EditVersion) or nameof(MainWindowViewModel.IsPublishingUnitSource) ||
                !LibraryUnitEditor.IsSynchronizingUi && IsUnitEditorMutationProperty(change.PropertyName)))
                _ = RefreshUnitInheritanceDisplayAsync(LibraryUnitEditor);
        };
        ScenarioEditor.PropertyChanged += (_, change) =>
        {
            if (change.PropertyName == nameof(ScenarioWindowViewModel.EvaluationContext))
            {
                ReleaseChangedUnitCampaignContext();
                if (CampaignEditor.RequiresSelectedCampaignAdapter) RefreshProjectUnitWorkspaceEntries();
                _ = RefreshUnitInheritanceDisplayAsync(UnitEditor);
                QueueProjectUnitInheritanceDisplay();
            }
        };
        LibrarySnapshotChanged += (_, _) =>
        {
            _ = RefreshUnitInheritanceDisplayAsync(UnitEditor);
            _ = RefreshUnitInheritanceDisplayAsync(LibraryUnitEditor);
            QueueProjectUnitInheritanceDisplay();
            QueueManagedUnitInheritanceDisplay();
        };
    }

    private async Task<WccLeaderUnitPreviewResult> ResolveContextualLeaderUnitPreviewAsync(
        WccLeaderUnitPreviewRequest request, CancellationToken cancellationToken)
    {
        static WccLeaderUnitPreviewResult Unavailable(string diagnostic) => new(new(), Diagnostic: diagnostic);
        bool IsCurrent() => ReferenceEquals(request.Snapshot, LibrarySnapshot) &&
            FindCurrentProjectRevisionId(request.Snapshot) == request.RootRevisionId &&
            ScenarioSession.Scope == WccEditorSessionScope.Project &&
            ReferenceEquals(request.Plan, _campaignFieldEvaluationPlan) &&
            ScenarioEditor.IsCurrentLeaderUnitPreviewRequest(request);
        if (!IsCurrent()) return Unavailable("The leader's captured unit context changed; refresh its preview.");
        var profile = ResolvePinnedProjectSourceResourceProfile();
        if (!SameOptionalLeaderPreviewPath(request.InstallPath, profile?.InstallPath) ||
            !SameOptionalLeaderPreviewPath(request.AddonsPath, profile?.AddonsPath))
            return Unavailable("The leader preview and campaign use different resource paths.");
        var binding = CreateUnitDisplayBinding(request.Snapshot, request.RootRevisionId, request.SourcePath, true);
        if (binding is null || !ReferenceEquals(binding.Plan, request.Plan) ||
            binding.Context.SemanticKey != request.Context.SemanticKey)
            return Unavailable("The leader needs the selected campaign's exact unit registration context.");
        using var reader = AcquireUnitDisplayScope(binding);
        if (reader is null) return Unavailable("The leader's captured unit context changed; refresh its preview.");
        var scope = await reader.Task.WaitAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsCurrent() || reader.Work.Lifetime.IsRetired || scope is null || GetUnitDisplayBindingKey() != binding.Key)
            return Unavailable("The leader's captured unit context changed; refresh its preview.");
        var resolved = await Task.Run(() =>
        {
        cancellationToken.ThrowIfCancellationRequested();
        reader.Token.ThrowIfCancellationRequested();
        if (!scope.Capture.IsCaptureComplete || !scope.Capture.HasCompleteCoreUnitPrelude || !scope.Evaluation.IsComplete ||
            !scope.UnitRegistry.HasCompleteDeclaredInventory || !scope.Capture.Sources.Any(source =>
                PathsEqual(source.Path, request.SourcePath) && source.Text == request.SourceText))
            return Unavailable("The leader type needs a complete captured campaign unit registry. Scenario inspection cannot choose an omitted unit definition.");
        var declarations = scope.UnitRegistry.Declarations.Where(item => item.Id == request.UnitTypeId).Take(2).ToArray();
        if (declarations.Length != 1)
            return Unavailable(declarations.Length == 0 ? "The leader type is not registered in this captured context." :
                "Several active unit declarations match this leader in the captured context.");
        var owner = declarations[0].Owner;
        if (owner.OpeningOrigin is not { } opening || owner.ClosingOrigin is not { } closing ||
            !PathsEqual(opening.Source.Path, closing.Source.Path) || opening.Source.ContentHash != closing.Source.ContentHash ||
            closing.StartOffset < opening.StartOffset)
            return Unavailable("The active leader unit has no exact captured definition span.");
        var display = scope.Find(opening.Source.Path, opening.Source.ContentHash, opening.StartOffset,
            closing.StartOffset + closing.Length - opening.StartOffset, cancellationToken);
        var fieldKey = request.Kind == WccAssetReferenceKind.Image ? "image" : "profile";
        if (display is null || !ReferenceEquals(display.SelectedOwner, owner) || !display.Root.IsStructureKnown ||
            !display.HasResolvedInheritance || display.Root.Fields.TryGetValue(fieldKey, out var field) && !field.IsKnown)
            return Unavailable("The active unit's visual field remains unresolved in this captured context.");
        var resolution = WccUnitPreviewPathResolver.ResolveKind(opening.Source.Path, WccSourceOrigin.ImportedExternal,
            display.PreviewDocument, request.Kind, request.InstallPath);
        var raw = display.Root.GetKnownValue(fieldKey);
        return new WccLeaderUnitPreviewResult(resolution, raw is null ? null : WccScenarioFieldValueCodec.DecodeLiteral(raw));
        }, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return IsCurrent() && !reader.Work.Lifetime.IsRetired && GetUnitDisplayBindingKey() == binding.Key ? resolved :
            Unavailable("The leader's captured unit context changed; refresh its preview.");

        string? GetUnitDisplayBindingKey() => CreateUnitDisplayBinding(request.Snapshot, request.RootRevisionId,
            request.SourcePath, true)?.Key;
    }

    private static bool SameOptionalLeaderPreviewPath(string? left, string? right) =>
        string.IsNullOrWhiteSpace(left) && string.IsNullOrWhiteSpace(right) ||
        !string.IsNullOrWhiteSpace(left) && !string.IsNullOrWhiteSpace(right) && PathsEqual(left, right);

    private UnitDisplayBinding? GetUnitDisplayBinding(MainWindowViewModel editor)
    {
        if (LibrarySnapshot is not { } snapshot || editor.CurrentUnitAsset?.SourcePath is not { } path) return null;
        var project = ReferenceEquals(editor, UnitEditor) && UnitSession.Scope == WccEditorSessionScope.Project;
        var revisions = snapshot.SourceFilesById.Values.Where(file => _activeLibraryRevisionIds.Contains(file.PackageRevisionId) &&
            file.StoragePath is { } sourcePath && PathsEqual(sourcePath, path)).Select(file => file.PackageRevisionId).Distinct().ToArray();
        var revision = project ? FindCurrentProjectRevisionId(snapshot) : revisions.Length == 1 ? (WccPackageRevisionId?)revisions[0] : null;
        return revision is { } id ? CreateUnitDisplayBinding(snapshot, id, path, project) : null;
    }

    private UnitDisplayBinding? CreateUnitDisplayBinding(WccLibrarySnapshot snapshot, WccPackageRevisionId revisionId,
        string sourcePath, bool campaign)
    {
        if (!snapshot.RevisionsById.TryGetValue(revisionId, out var revision) ||
            !snapshot.PackagesById.TryGetValue(revision.PackageId, out var package) || package.SourceLocation is not { } root) return null;
        var core = package.SourceKind == WccPackageSourceKinds.WesnothCore ||
            package.SourceKind == WccPackageSourceKinds.WesnothMainline && package.ContentKind == WccPackageContentKinds.ResourcePack &&
            package.ParentPackageId is null;
        var managed = package.SourceKind == WccPackageSourceKinds.WccManagedLibrary;
        var entry = campaign ? CampaignEditor.CurrentProject?.SourcePath :
            core ? Path.Combine(root, "units.cfg") : sourcePath;
        if (entry is null) return null;
        var context = campaign ? ScenarioEditor.EvaluationContext : new WmlEvaluationContext(
            targetCompatibility: FindActiveWesnothInstallationProfile()?.CompatibilityTarget);
        var profile = campaign ? ResolvePinnedProjectSourceResourceProfile() : FindActiveWesnothInstallationProfile();
        return new(snapshot, revisionId, entry, root, context, campaign ? _campaignFieldEvaluationPlan : null, campaign, core, managed,
            BuildWesnothSourceResourceProfileIdentity(profile), profile?.AddonsPath);
    }

    private Task<UnitDisplayScope?> GetUnitDisplayScope(UnitDisplayBinding binding)
    {
        lock (_unitDisplaySync)
        {
        if (!_unitDisplayScopes.TryGetValue(binding.Key, out var work))
        {
            work = new(binding, _unitDisplayEpoch) { LastAccess = ++_unitDisplayAccess };
            _unitDisplayScopes[binding.Key] = work;
            // Register the shared task before starting its worker. Task.Yield is
            // not a registration barrier on a host without a UI context.
            TrackUnitDisplayWorker(Task.Run(() => RunUnitDisplayWorkAsync(work)));
        }
        work.LastAccess = ++_unitDisplayAccess;
        return work.Task;
        }
    }

    private UnitDisplayLease? AcquireUnitDisplayScope(UnitDisplayBinding binding)
    {
        if (!ReferenceEquals(binding.Snapshot, LibrarySnapshot)) return null;
        if (!ReferenceEquals(_unitDisplaySnapshot, binding.Snapshot)) ResetUnitDisplayScopes();
        var currentCampaign = binding.Campaign && FindCurrentProjectRevisionId(binding.Snapshot) == binding.Revision &&
            CreateUnitDisplayBinding(binding.Snapshot, binding.Revision, binding.EntryPath, true)?.Key == binding.Key;
        UnitDisplayLease? previousCampaign = null;
        UnitDisplayLease? lease;
        lock (_unitDisplaySync)
        {
            if (!ReferenceEquals(binding.Snapshot, _unitDisplaySnapshot)) return null;
            _ = GetUnitDisplayScope(binding);
            var work = _unitDisplayScopes[binding.Key];
            lease = work.Lifetime.Acquire() is { } reader ? new(this, work, reader) : null;
            if (currentCampaign && lease is not null && !ReferenceEquals(_unitCampaignContextLease?.Work, work) &&
                work.Lifetime.Acquire() is { } workspaceReader)
            {
                previousCampaign = _unitCampaignContextLease;
                _unitCampaignContextLease = new(this, work, workspaceReader);
            }
        }
        previousCampaign?.Dispose();
        return lease;
    }

    private void ReleaseChangedUnitCampaignContext()
    {
        UnitDisplayLease? previous;
        lock (_unitDisplaySync) previous = _unitCampaignContextLease;
        if (previous is null) return;
        var binding = previous.Work.Binding;
        if (ReferenceEquals(binding.Snapshot, LibrarySnapshot) && FindCurrentProjectRevisionId(binding.Snapshot) == binding.Revision &&
            CreateUnitDisplayBinding(binding.Snapshot, binding.Revision, binding.EntryPath, true)?.Key == binding.Key) return;
        lock (_unitDisplaySync)
        {
            if (!ReferenceEquals(_unitCampaignContextLease, previous)) return;
            _unitCampaignContextLease = null;
        }
        previous.Dispose();
    }

    private async Task RunUnitDisplayWorkAsync(UnitDisplayWork work)
    {
        UnitDisplayScope? scope = null;
        var entered = false;
        try
        {
            await _unitDisplayCaptureGate.WaitAsync(work.Lifetime.Token).ConfigureAwait(false);
            entered = true;
            scope = await CaptureUnitDisplayScopeAsync(work.Binding, work.Lifetime.Token).ConfigureAwait(false);
            var obsolete = false;
            lock (_unitDisplaySync)
            {
                if (work.Lifetime.IsRetired || work.Epoch != _unitDisplayEpoch ||
                    !_unitDisplayScopes.TryGetValue(work.Binding.Key, out var current) || !ReferenceEquals(current, work))
                {
                    obsolete = true;
                }
            }
            if (obsolete) { scope?.Retire(); scope = null; }
            work.Completion.TrySetResult(scope);
        }
        catch (OperationCanceledException) { work.Completion.TrySetResult(null); }
        catch (Exception failure) { work.Completion.TrySetException(failure); }
        finally
        {
            if (entered) _unitDisplayCaptureGate.Release();
            work.Lifetime.Complete();
            TrimUnitDisplayScopes();
        }
    }

    private void TrackUnitDisplayWorker(Task task)
    {
        lock (_unitDisplaySync) _unitDisplayWorkers.Add(task);
        _ = task.ContinueWith(completed =>
        {
            _ = completed.Exception;
            lock (_unitDisplaySync) _unitDisplayWorkers.Remove(completed);
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private static async Task WaitForUnitDraftWorkerAsync(UnitDisplayScope scope, Task worker)
    {
        try { await worker.ConfigureAwait(false); }
        finally { await scope.CancellationCompletion.ConfigureAwait(false); }
    }

    private void ResetUnitDisplayScopes() => RetireUnitDisplayScopes(campaignOnly: false);
    private void RetireCampaignUnitDisplayScopes() => RetireUnitDisplayScopes(campaignOnly: true);

    private void RetireUnitDisplayScopes(bool campaignOnly)
    {
        UnitDisplayWork[] retired;
        UnitDisplayLease[] readers;
        CancellationTokenSource[] requests;
        MainWindowViewModel[] editors;
        Dictionary<MainWindowViewModel, long> retiredRequests;
        lock (_unitDisplaySync)
        {
            if (!campaignOnly)
            {
                _unitDisplayEpoch++;
                _unitDisplaySnapshot = LibrarySnapshot;
            }
            retired = _unitDisplayScopes.Values.Where(work => !campaignOnly || work.Binding.Campaign).ToArray();
            foreach (var work in retired) RemoveUnitDisplayWork(work);
            var keys = _unitDisplaySummaries.Where(item => !campaignOnly || item.Value.Binding.Campaign)
                .Select(item => item.Key).Concat(retired.Select(work => work.Binding.Key)).Distinct(StringComparer.Ordinal).ToArray();
            foreach (var key in keys)
            {
                _unitDisplaySummaries.Remove(key);
                _unitDisplayPublications.Remove(key);
                _unitDisplayGroupingRequests.Remove(key);
            }
            editors = _unitEditorDisplayLeases.Where(item => !campaignOnly || item.Value.Work.Binding.Campaign)
                .Select(item => item.Key).ToArray();
            readers = editors.Select(editor => _unitEditorDisplayLeases[editor])
                .Concat(_unitCampaignContextLease is { } currentCampaign ? new[] { currentCampaign } : []).ToArray();
            _unitCampaignContextLease = null;
            requests = editors.Where(_unitDraftDisplayCancellations.ContainsKey)
                .Select(editor => _unitDraftDisplayCancellations[editor]).ToArray();
            foreach (var editor in editors)
            {
                _unitEditorDisplayLeases.Remove(editor);
                _unitDraftDisplayCancellations.Remove(editor);
                _unitDraftDisplayRequests[editor] = _unitDraftDisplayRequests.GetValueOrDefault(editor) + 1;
            }
            retiredRequests = editors.ToDictionary(editor => editor, editor => _unitDraftDisplayRequests[editor]);
        }
        foreach (var request in requests) CancelUnitDisplayRequest(request);
        foreach (var work in retired) RetireUnitDisplayWork(work);
        foreach (var reader in readers) reader.Dispose();
        foreach (var editor in editors)
        {
            if (!StillRetired(editor)) continue;
            editor.ConfigureUnitEffectiveDisplayResolver(null);
            // A reentrant current publication owns its newly established lease.
            if (!StillRetired(editor)) continue;
            editor.ConfigureCapturedImageResourceContext(null, unavailableReason: campaignOnly
                ? "The captured campaign unit context changed." : null);
        }

        bool StillRetired(MainWindowViewModel editor)
        {
            lock (_unitDisplaySync) return !_unitEditorDisplayLeases.ContainsKey(editor) &&
                _unitDraftDisplayRequests.GetValueOrDefault(editor) == retiredRequests[editor];
        }
    }

    private void RemoveUnitDisplayWork(UnitDisplayWork work)
    {
        _unitDisplayScopes.Remove(work.Binding.Key);
    }

    private void RetireUnitDisplayWork(UnitDisplayWork work)
    {
        work.Lifetime.Dispose();
        TrackUnitDisplayWorker(work.Lifetime.CancellationCompletion);
        if (work.Task.IsCompletedSuccessfully && work.Task.Result is { } scope)
        {
            scope.Retire();
            TrackUnitDisplayWorker(scope.CancellationCompletion);
        }
    }

    private void TrimUnitDisplayScopes()
    {
        List<UnitDisplayWork> retired = [];
        lock (_unitDisplaySync)
        {
            var idle = _unitDisplayScopes.Values.Where(work => work.Lifetime.Readers == 0)
                .OrderByDescending(work => work.LastAccess).ToArray();
            long retained = 0;
            var count = 0;
            foreach (var work in idle)
            {
                var characters = work.Task.IsCompletedSuccessfully ? work.Task.Result?.RetainedExpandedCharacters ?? 0 : 0;
                if (work.Task.IsCompletedSuccessfully && count < MaximumIdleUnitDisplayScopes &&
                    characters <= MaximumIdleUnitDisplayCharacters - retained)
                {
                    retained += characters;
                    count++;
                    continue;
                }
                RemoveUnitDisplayWork(work);
                retired.Add(work);
            }
        }
        foreach (var work in retired) RetireUnitDisplayWork(work);
    }

    private async Task<UnitDisplayScope?> CaptureUnitDisplayScopeAsync(UnitDisplayBinding binding, CancellationToken cancellationToken)
    {
        try
        {
            var capture = await WccStaticSourceCapture.CaptureAsync(binding.Snapshot, binding.Revision, binding.EntryPath,
                cancellationToken: cancellationToken,
                configuredAddonsPath: binding.AddonsPath,
                includeCoreUnits: true);
            if (!capture.IsCaptureComplete || !capture.HasCompleteCoreUnitPrelude ||
                !WmlStaticSingleplayerEnvironment.TryCreate(capture.CoreVersion, out var environment)) return null;
            if (binding.Campaign && (binding.Plan is null || capture.EntrySource!.ContentHash != binding.Plan.CampaignMainTextHash)) return null;
            return await Task.Run(() =>
            {
                // The Library has no game entry point. This explicit, nonauthored
                // preview wrapper registers the selected file in its captured Core
                // scope. Stored sibling files are independent reusable content,
                // not an entry point: their unknown macros must not invalidate this
                // file's preview. It is never written into a Library or campaign.
                var entry = binding.Campaign ? capture.EntrySource! : binding.Core ?
                    new WmlStaticSource("wcc-static://unit-display/core.cfg", "", false) :
                    new WmlStaticSource(Path.Combine(Path.GetDirectoryName(binding.EntryPath)!, "__wcc_unit_display__.cfg"),
                        "[units]\n{./" + Path.GetFileName(binding.EntryPath) + "}\n[/units]\n", false);
                var undefined = binding.UndefinedSymbols;
                var request = new WmlStaticEvaluationRequest(entry, binding.Context,
                    new[] { environment!.DefinitionsSource }.Concat(capture.PreludeSources).Concat(capture.CoreUnitPreludeSources).ToArray(),
                    undefined, true, capture.IncludeResolver,
                    Limits: WmlStaticEvaluationLimits.Campaign);
                var evaluated = new WmlStaticEvaluator().Evaluate(request, cancellationToken);
                var projection = WmlStaticScalarProjection.Create(evaluated, cancellationToken);
                // Reuse the ordered, bounded inspection solely for active image
                // paths. Full unit values, inheritance and write proof still use
                // the ordinary result above. Changed-source draft scopes keep
                // their existing full-evaluation guard and cannot borrow this
                // exact baseline receipt. Compute off the UI thread, once per
                // captured package/context rather than once per opened unit.
                var imageInspection = binding.Campaign && evaluated.OutputLimitExceeded
                    ? new WmlStaticEvaluator().InspectScenarios(request, cancellationToken) : null;
                return new UnitDisplayScope(capture, request, evaluated, projection, imageInspection)
                    { LifetimeCancellation = cancellationToken };
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            return null;
        }
    }

    public async Task WaitForUnitInheritanceDisplayAsync(CancellationToken cancellationToken = default)
    {
        await RefreshUnitInheritanceDisplayAsync(UnitEditor).WaitAsync(cancellationToken);
        await RefreshUnitInheritanceDisplayAsync(LibraryUnitEditor).WaitAsync(cancellationToken);
        Task<UnitDisplayScope?>[] tasks;
        UnitDisplayBinding[] bindings;
        lock (_unitDisplaySync)
        {
            tasks = _unitDisplayScopes.Values.Select(work => work.Task).ToArray();
            bindings = _unitDisplayScopes.Values.Select(work => work.Binding).ToArray();
        }
        await Task.WhenAll(tasks).WaitAsync(cancellationToken);
        foreach (var binding in bindings)
        {
            bool requested;
            lock (_unitDisplaySync) requested = _unitDisplayGroupingRequests.Contains(binding.Key);
            if (requested && CachedUnitDisplayScope(binding) is not null) QueueUnitDisplayGrouping(binding);
        }
        Task[] grouping;
        lock (_unitDisplaySync) grouping = _unitDisplayScopes.Values
            .Select(work => work.GroupingCompletion?.Task ?? Task.CompletedTask).ToArray();
        await Task.WhenAll(grouping).WaitAsync(cancellationToken);
        while (true)
        {
            Task[] workers;
            lock (_unitDisplaySync)
            {
                _unitDisplayWorkers.RemoveWhere(task => task.IsCompleted);
                workers = _unitDisplayWorkers.ToArray();
            }
            if (workers.Length == 0) break;
            var drain = Task.WhenAll(workers);
            try { await drain.WaitAsync(cancellationToken); }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && drain.IsCanceled)
            {
                // Superseded display preparations cancel normally. Still join
                // their actual completion; caller cancellation and faults,
                // including faulted tasks carrying OCE, must propagate.
            }
        }
    }

    private async Task RefreshUnitInheritanceDisplayAsync(MainWindowViewModel editor)
    {
        if (editor.IsPublishingUnitSource) return;
        long requestId;
        CancellationTokenSource? previousCancellation;
        var cancellation = new CancellationTokenSource();
        var cancellationToken = cancellation.Token;
        lock (_unitDisplaySync)
        {
            _unitDraftDisplayCancellations.TryGetValue(editor, out previousCancellation);
            _unitDraftDisplayCancellations[editor] = cancellation;
            _unitDraftDisplayRequests.TryGetValue(editor, out var previous);
            _unitDraftDisplayRequests[editor] = requestId = previous + 1;
        }
        if (previousCancellation is not null) CancelUnitDisplayRequest(previousCancellation);
        try { await RefreshUnitInheritanceDisplayCoreAsync(editor, requestId, cancellationToken); }
        catch (OperationCanceledException) { /* A newer request or retired shared scope owns publication. */ }
    }

    private void CancelUnitDisplayRequest(CancellationTokenSource cancellation)
    {
        TrackUnitDisplayWorker(CancelAndDisposeAsync());
        async Task CancelAndDisposeAsync()
        {
            try { await cancellation.CancelAsync().ConfigureAwait(false); }
            finally { cancellation.Dispose(); }
        }
    }

    private void ReleaseUnitEditorDisplayLease(MainWindowViewModel editor)
    {
        UnitDisplayLease? previous;
        lock (_unitDisplaySync) _unitEditorDisplayLeases.Remove(editor, out previous);
        previous?.Dispose();
    }

    private void SetUnitEditorDisplayLease(MainWindowViewModel editor, UnitDisplayWork work)
    {
        UnitDisplayLease? previous;
        lock (_unitDisplaySync)
        {
            if (_unitEditorDisplayLeases.TryGetValue(editor, out previous) && ReferenceEquals(previous.Work, work)) return;
            if (work.Lifetime.Acquire() is not { } reader) return;
            _unitEditorDisplayLeases[editor] = new(this, work, reader);
        }
        previous?.Dispose();
    }

    private async Task RefreshUnitInheritanceDisplayCoreAsync(MainWindowViewModel editor, long requestId, CancellationToken cancellationToken)
    {
        bool IsRequestCurrent() => !cancellationToken.IsCancellationRequested &&
            _unitDraftDisplayRequests.GetValueOrDefault(editor) == requestId;
        var asset = editor.CurrentUnitAsset;
        var selected = editor.SelectedUnit;
        var editVersion = editor.EditVersion;
        var inherits = selected?.Document.Unit.BaseUnits.Count is > 0;
        if (selected is null)
        {
            ReleaseUnitEditorDisplayLease(editor);
            if (!IsRequestCurrent()) return;
            editor.UnitInheritancePreviewStatus = null;
            if (!IsRequestCurrent()) return;
            editor.ConfigureCapturedImageResourceContext(null);
            if (!IsRequestCurrent()) return;
            editor.ConfigureUnitEffectiveDisplayResolver(null);
            return;
        }
        if (asset?.SourcePath is not { } path || GetUnitDisplayBinding(editor) is not { } binding)
        {
            ReleaseUnitEditorDisplayLease(editor);
            if (!IsRequestCurrent()) return;
            editor.UnitInheritancePreviewStatus = inherits ? "Inherited values need an indexed source and its pinned Core package." : null;
            if (!IsRequestCurrent()) return;
            editor.ConfigureCapturedImageResourceContext(null, unavailableReason:
                ReferenceEquals(editor, UnitEditor) && UnitSession.Scope == WccEditorSessionScope.Project
                    ? "Campaign images need an indexed source and its pinned Core package." : null);
            if (!IsRequestCurrent()) return;
            if (inherits) editor.ConfigureUnitEffectiveDisplayResolver(null);
            return;
        }
        // Only the actual campaign entry point proves active binary paths. The
        // standalone selected-file wrapper remains an owner/Core preview scope.
        if (!binding.Campaign) editor.ConfigureCapturedImageResourceContext(null);
        else if (!editor.HasCurrentCapturedImageResourceContext)
            editor.ConfigureCapturedImageResourceContext(null,
                unavailableReason: "Resolving the captured campaign image search paths...");
        if (!IsRequestCurrent()) return;
        if (!inherits && !binding.Campaign)
        {
            ReleaseUnitEditorDisplayLease(editor);
            if (!IsRequestCurrent()) return;
            editor.UnitInheritancePreviewStatus = null;
            if (!IsRequestCurrent()) return;
            editor.ConfigureUnitEffectiveDisplayResolver(null);
            return;
        }
        using var reader = AcquireUnitDisplayScope(binding);
        if (reader is null || !IsRequestCurrent()) return;
        SetUnitEditorDisplayLease(editor, reader.Work);
        var task = reader.Task;
        if (inherits) lock (_unitDisplaySync) _unitDisplayGroupingRequests.Add(binding.Key);
        if (!task.IsCompleted)
        {
            editor.UnitInheritancePreviewStatus = inherits ? "Resolving inherited values in " + binding.Context.DisplayName + "..." : null;
            if (!IsRequestCurrent()) return;
            if (inherits) editor.ConfigureUnitEffectiveDisplayResolver(null, binding.Context.DisplayName);
        }
        var scope = await task.WaitAsync(cancellationToken);
        bool IsCurrent() => !editor.IsPublishingUnitSource && ReferenceEquals(binding.Snapshot, LibrarySnapshot) &&
            !reader.Work.Lifetime.IsRetired &&
            ReferenceEquals(asset, editor.CurrentUnitAsset) && ReferenceEquals(selected, editor.SelectedUnit) &&
            editor.EditVersion == editVersion && GetUnitDisplayBinding(editor)?.Key == binding.Key &&
            _unitDraftDisplayRequests.GetValueOrDefault(editor) == requestId;
        bool IsResourceContextCurrent() => !editor.IsPublishingUnitSource && ReferenceEquals(binding.Snapshot, LibrarySnapshot) &&
            !reader.Work.Lifetime.IsRetired &&
            ReferenceEquals(asset, editor.CurrentUnitAsset) && ReferenceEquals(selected, editor.SelectedUnit) &&
            editor.EditVersion == editVersion && GetUnitDisplayBinding(editor)?.Key == binding.Key;
        UnitDraftDisplayCandidate? candidate;
        WmlStaticSource? source;
        lock (_unitDisplaySync)
        {
        if (!IsCurrent()) return;
        source = scope?.Capture.Sources.SingleOrDefault(source => PathsEqual(source.Path, path));
        var originals = source is null ? [] : UnitTypeImporter.ImportAllFromText(source.Text);
        candidate = inherits ? editor.CaptureUnitDraftDisplayCandidate() : editor.CaptureUnitImageResourceDraft();
        // The renderer works on detached blocks, but its draft still needs an
        // exact captured baseline and declaration position. IDs alone are not a
        // receipt, and a RAW edit may change the number of physical declarations.
        var document = selected.Document;
        var original = originals.SingleOrDefault(original => original.BlockSpan.StartIndex == document.BlockSpan.StartIndex &&
            original.BlockSpan.EndIndexExclusive == document.BlockSpan.EndIndexExclusive);
        if (source is null || original is null || source.ContentHash != WccContentHash.ComputeText(document.OriginalText) ||
            candidate is null || candidate.SelectedUnitOrdinal < 0 || candidate.SelectedUnitOrdinal >= originals.Count ||
            originals[candidate.SelectedUnitOrdinal].BlockSpan != original.BlockSpan ||
            UnitTypeImporter.ImportAllFromText(candidate.Text).Count != originals.Count)
        {
            editor.UnitInheritancePreviewStatus = inherits ? "Inherited values need an unchanged captured source baseline in " + binding.Context.DisplayName + "." : null;
            if (!IsCurrent()) return;
            editor.ConfigureCapturedImageResourceContext(null, unavailableReason: binding.Campaign
                ? "Campaign images need an unchanged captured source baseline in " + binding.Context.DisplayName + "." : null);
            if (!IsCurrent()) return;
            if (inherits) editor.ConfigureUnitEffectiveDisplayResolver(null, binding.Context.DisplayName);
            return;
        }
        }
        if (inherits) QueueUnitDisplayGrouping(binding);
        // Coalesce quick field edits before evaluating the immutable candidate.
        // No disk capture or index refresh is needed for a local draft.
        if (candidate.Text != source.Text) await Task.Delay(80, cancellationToken);
        lock (_unitDisplaySync) if (!IsCurrent()) return;
        UnitDisplayScope? draftScope;
        try
        {
            var draftTask = scope!.EvaluateDraftAsync(source, candidate.Text, cancellationToken);
            TrackUnitDisplayWorker(WaitForUnitDraftWorkerAsync(scope, scope.DraftWorkerTask));
            draftScope = await draftTask;
        }
        catch (Exception failure) when (failure is ArgumentException or InvalidOperationException) { draftScope = null; }
        // Build resource search and inherited preview documents on a worker. UI
        // callbacks and all selection/source/version checks stay on the owner.
        var draftDocument = candidate.SelectedDocument;
        var preparation = Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            reader.Token.ThrowIfCancellationRequested();
            var images = binding.Campaign ? draftScope?.ImageResources : null;
            var display = inherits ? draftScope?.Find(path, WccContentHash.ComputeText(candidate.Text),
                draftDocument.BlockSpan.StartIndex, draftDocument.BlockSpan.EndIndexExclusive - draftDocument.BlockSpan.StartIndex,
                cancellationToken) : null;
            cancellationToken.ThrowIfCancellationRequested();
            reader.Token.ThrowIfCancellationRequested();
            return (Images: images, Display: display);
        }, cancellationToken);
        TrackUnitDisplayWorker(preparation);
        var prepared = await preparation;
        lock (_unitDisplaySync)
        {
            if (!IsCurrent()) return;
            if (binding.Campaign)
                editor.ConfigureCapturedImageResourceContext(prepared.Images, IsResourceContextCurrent,
                    draftScope is null ? "The campaign image search context could not be captured." : null);
            if (!IsCurrent()) return;
            if (!inherits)
            {
                editor.UnitInheritancePreviewStatus = null;
                if (!IsCurrent()) return;
                // The new plain unit must not keep an earlier inherited unit's
                // resolver closure and candidate graph alive.
                editor.ConfigureUnitEffectiveDisplayResolver(null, binding.Context.DisplayName);
                return;
            }
            var display = prepared.Display;
            editor.UnitInheritancePreviewStatus = display is null
                ? "Inherited values are unavailable in " + binding.Context.DisplayName + ". Check RAW macros and the captured source context." : null;
            if (!IsCurrent()) return;
            editor.SetUnitEffectiveDraftCandidate(candidate);
            editor.ConfigureUnitEffectiveDisplayResolver(display is null ? null : document =>
                ReferenceEquals(document, selected.Document) ? display : null, binding.Context.DisplayName);
        }
    }

    private UnitDisplayScope? CachedUnitDisplayScope(UnitDisplayBinding? binding)
    {
        lock (_unitDisplaySync)
            return binding is not null && ReferenceEquals(binding.Snapshot, _unitDisplaySnapshot) &&
                _unitDisplayScopes.TryGetValue(binding.Key, out var work) && !work.Lifetime.IsRetired &&
                work.Task.IsCompletedSuccessfully ? work.Task.Result : null;
    }

    private Func<UnitFileAsset, UnitTypeDocument, UnitDisplaySummary?> CreateProjectUnitSummaryReader()
    {
        if (LibrarySnapshot is not { } snapshot || FindCurrentProjectRevisionId(snapshot) is not { } revision ||
            CampaignEditor.CurrentProject?.SourcePath is not { } entryPath ||
            CreateUnitDisplayBinding(snapshot, revision, entryPath, true) is not { } binding ||
            !ReferenceEquals(binding.Snapshot, _unitDisplaySnapshot) ||
            !_unitDisplaySummaries.TryGetValue(binding.Key, out var grouping) ||
            !ReferenceEquals(grouping.Binding.Snapshot, binding.Snapshot)) return (_, _) => null;

        // All documents imported from one file share immutable OriginalText. Keep
        // its hash only for this refresh instead of encoding/hashing it per unit.
        var hashes = new Dictionary<string, string>(ReferenceEqualityComparer.Instance);
        return (asset, document) =>
        {
            if (document.EditableBlock.ToRawText() != document.BlockSpan.RawText || asset.SourcePath is not { } path) return null;
            if (!hashes.TryGetValue(document.OriginalText, out var hash))
                hashes.Add(document.OriginalText, hash = WccContentHash.ComputeText(document.OriginalText));
            return grouping.Values.GetValueOrDefault(new(path, hash, document.BlockSpan.StartIndex,
                document.BlockSpan.EndIndexExclusive - document.BlockSpan.StartIndex));
        };
    }

    private UnitDisplaySummary? CachedIndexedUnitSummary(WccEntityRecord entity, WccSourceFileRecord source)
    {
        if (LibrarySnapshot is not { } snapshot || source.StoragePath is not { } path || entity.SourceSpan?.StartOffset is not { } start ||
            entity.SourceSpan.EndOffsetExclusive is not { } end) return null;
        return CachedUnitDisplaySummary(CreateUnitDisplayBinding(snapshot, source.PackageRevisionId, path, false),
            new(path, source.ContentHash!, start, end - start));
    }

    private UnitDisplaySummary? CachedUnitDisplaySummary(UnitDisplayBinding? binding, UnitDisplayLookup lookup)
    {
        lock (_unitDisplaySync) return binding is not null && ReferenceEquals(binding.Snapshot, _unitDisplaySnapshot) &&
            _unitDisplaySummaries.TryGetValue(binding.Key, out var grouping) && ReferenceEquals(grouping.Binding.Snapshot, binding.Snapshot)
                ? grouping.Values.GetValueOrDefault(lookup) : null;
    }

    private async Task<IReadOnlyList<string>?> ResolveInitialUnitGendersAsync(CatalogPickerOptionVm option)
    {
        if (LibrarySnapshot is not { } snapshot || option.LibraryEntityId is not { } id ||
            !snapshot.EntitiesById.TryGetValue(id, out var entity) || entity.EntityType != WccEntityTypes.UnitType ||
            !snapshot.SourceFilesById.TryGetValue(entity.SourceFileId, out var source) || source.StoragePath is not { } path ||
            entity.SourceSpan?.StartOffset is not { } start || entity.SourceSpan.EndOffsetExclusive is not { } end)
            return null;
        var projectRevision = FindCurrentProjectRevisionId(snapshot);
        var project = projectRevision == source.PackageRevisionId;
        var binding = CreateUnitDisplayBinding(snapshot, source.PackageRevisionId, path, project);
        if (binding is null) return null;
        using var reader = AcquireUnitDisplayScope(binding);
        if (reader is null) return null;
        var scope = await reader.Task;
        if (reader.Work.Lifetime.IsRetired || !ReferenceEquals(snapshot, LibrarySnapshot) || projectRevision != FindCurrentProjectRevisionId(snapshot) ||
            CreateUnitDisplayBinding(snapshot, source.PackageRevisionId, path, project)?.Key != binding.Key ||
            !ScenarioEditor.GetUnitTypeCatalogOptions().Any(current => ReferenceEquals(current, option))) return null;
        // This captured display supplies choices only. Checked Save independently
        // verifies a selected gender against each affected registration context.
        var genders = await Task.Run(() => WccUnitTypeGenderOptions.Read(scope?.Find(path, source.ContentHash!, start, end - start)), reader.Token);
        return !reader.Work.Lifetime.IsRetired && ReferenceEquals(snapshot, LibrarySnapshot) &&
            projectRevision == FindCurrentProjectRevisionId(snapshot) &&
            CreateUnitDisplayBinding(snapshot, source.PackageRevisionId, path, project)?.Key == binding.Key &&
            ScenarioEditor.GetUnitTypeCatalogOptions().Any(current => ReferenceEquals(current, option)) ? genders : null;
    }

    private void QueueProjectUnitInheritanceDisplay()
    {
        if (LibrarySnapshot is not { } snapshot || CampaignEditor.CurrentProject is not { } project ||
            !project.ProjectUnitFiles.Any(file => file.Documents.Any(doc => doc.Unit.BaseUnits.Count != 0)) ||
            FindCurrentProjectRevisionId(snapshot) is not { } revision || project.SourcePath is not { } path) return;
        if (CreateUnitDisplayBinding(snapshot, revision, path, true) is { } binding) QueueUnitDisplayGrouping(binding);
    }

    private void QueueManagedUnitInheritanceDisplay()
    {
        if (LibrarySnapshot is not { } snapshot) return;
        foreach (var revision in snapshot.RevisionsById.Values.Where(revision =>
            _activeLibraryRevisionIds.Contains(revision.Id) && snapshot.PackagesById.TryGetValue(revision.PackageId, out var package) &&
            package.SourceKind == WccPackageSourceKinds.WccManagedLibrary))
        {
            var derivedFiles = snapshot.EntitiesById.Values.Where(entity => entity.EntityType == WccEntityTypes.UnitType &&
                entity.Summary.ContainsKey("base_unit")).Select(entity => entity.SourceFileId).ToHashSet();
            foreach (var file in snapshot.SourceFilesById.Values.Where(file => file.PackageRevisionId == revision.Id &&
                derivedFiles.Contains(file.Id)))
                if (file.StoragePath is { } path && CreateUnitDisplayBinding(snapshot, revision.Id, path, false) is { } binding)
                    QueueUnitDisplayGrouping(binding);
        }
    }

    private void QueueUnitDisplayGrouping(UnitDisplayBinding binding)
    {
        lock (_unitDisplaySync)
            if (_unitDisplaySummaries.TryGetValue(binding.Key, out var existing) &&
                ReferenceEquals(existing.Binding.Snapshot, binding.Snapshot)) return;
        var reader = AcquireUnitDisplayScope(binding);
        if (reader is null) return;
        TaskCompletionSource? completion = null;
        lock (_unitDisplaySync)
        {
        _unitDisplayGroupingRequests.Add(binding.Key);
        if (!_unitDisplayPublications.Contains(binding.Key) && reader.Work.GroupingCompletion is null)
            reader.Work.GroupingCompletion = completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        if (completion is null) { reader.Dispose(); return; }
        var lookups = CaptureUnitDisplayGroupingLookups(binding);
        TrackUnitDisplayWorker(PrepareUnitDisplayGroupingAsync(binding, reader, lookups, completion));
    }

    private sealed record UnitDisplayLookup(string Path, string Hash, int Start, int Length);

    private UnitDisplayLookup[] CaptureUnitDisplayGroupingLookups(UnitDisplayBinding binding)
    {
        if (binding.Campaign)
        {
            return CampaignEditor.CurrentProject?.ProjectUnitFiles.Where(asset => asset.SourcePath is not null)
                .SelectMany(asset => asset.Documents.Select(document => new UnitDisplayLookup(asset.SourcePath!,
                    WccContentHash.ComputeText(document.OriginalText), document.BlockSpan.StartIndex,
                    document.BlockSpan.EndIndexExclusive - document.BlockSpan.StartIndex))).Distinct().ToArray() ?? [];
        }
        return binding.Snapshot.EntitiesById.Values.Where(entity => entity.EntityType == WccEntityTypes.UnitType &&
                binding.Snapshot.SourceFilesById.TryGetValue(entity.SourceFileId, out var file) &&
                file.PackageRevisionId == binding.Revision && file.StoragePath is { } path &&
                (binding.Core || PathsEqual(path, binding.EntryPath)) && entity.SourceSpan?.StartOffset is not null &&
                entity.SourceSpan.EndOffsetExclusive is not null)
            .Select(entity =>
            {
                var file = binding.Snapshot.SourceFilesById[entity.SourceFileId];
                return new UnitDisplayLookup(file.StoragePath!, file.ContentHash!, entity.SourceSpan!.StartOffset!.Value,
                    entity.SourceSpan.EndOffsetExclusive!.Value - entity.SourceSpan.StartOffset.Value);
            }).Distinct().ToArray();
    }

    private async Task PrepareUnitDisplayGroupingAsync(UnitDisplayBinding binding, UnitDisplayLease reader,
        UnitDisplayLookup[] lookups, TaskCompletionSource completion)
    {
        using (reader)
        {
            try
            {
                var scope = await reader.Task;
                if (scope is null || reader.Work.Lifetime.IsRetired) return;
                var summaries = await Task.Run(() =>
                {
                    var values = new Dictionary<UnitDisplayLookup, UnitDisplaySummary?>();
                    foreach (var lookup in lookups)
                    {
                        reader.Token.ThrowIfCancellationRequested();
                        var display = scope.Find(lookup.Path, lookup.Hash, lookup.Start, lookup.Length);
                        values[lookup] = display is null ? null : new(display.EffectiveRace, display.PreviewDocument);
                    }
                    return values;
                }, reader.Token);
                if (!reader.Work.Lifetime.IsRetired && ReferenceEquals(scope, CachedUnitDisplayScope(binding)))
                {
                    lock (_unitDisplaySync)
                    {
                        if (reader.Work.Lifetime.IsRetired || !ReferenceEquals(binding.Snapshot, LibrarySnapshot) ||
                            CreateUnitDisplayBinding(binding.Snapshot, binding.Revision, binding.EntryPath, binding.Campaign)?.Key != binding.Key) return;
                        _unitDisplaySummaries[binding.Key] = new(binding, summaries);
                    }
                    PublishUnitDisplayGrouping(binding);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception failure) { completion.TrySetException(failure); }
            finally { completion.TrySetResult(); }
        }
    }

    private void PublishUnitDisplayGrouping(UnitDisplayBinding binding)
    {
        lock (_unitDisplaySync)
        {
        if (!_unitDisplayGroupingRequests.Contains(binding.Key) || !ReferenceEquals(binding.Snapshot, LibrarySnapshot) ||
            CreateUnitDisplayBinding(binding.Snapshot, binding.Revision, binding.EntryPath, binding.Campaign)?.Key != binding.Key ||
            binding.Campaign && FindCurrentProjectRevisionId(binding.Snapshot) != binding.Revision ||
            !binding.Campaign && !_activeLibraryRevisionIds.Contains(binding.Revision) ||
            !_unitDisplayPublications.Add(binding.Key)) return;
        if (binding.Campaign) RefreshProjectUnitWorkspaceEntries();
        else RefreshUnitCatalogEntries(selectFallbackEntry: false);
        }
    }
}
