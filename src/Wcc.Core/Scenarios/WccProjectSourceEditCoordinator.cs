using System.Text.Json;
using Wcc.Core.Library;
using Wcc.Core.Campaigns;
using Wcc.Core.Projects;
using Wcc.Core.Units;

namespace Wcc.Core.Scenarios;

/// <summary>
/// One coordinator-owned review of all pristine source candidates. Persistence
/// is guarded per file; a partial I/O failure is explicit recovery, never rollback.
/// Hosts invalidate this publication before changing source, access or scope.
/// </summary>
public sealed class WccProjectSourceEditCoordinator
{
    private delegate Task<WccTextAtomicBatchReplaceResult> BatchWriter(
        IReadOnlyList<WccTextAtomicBatchCandidate> candidates,
        Func<IReadOnlyList<WccTextDiskRevision>, CancellationToken, Task<string?>> verifyBeforeFirstReplace,
        Func<Func<WccTextAtomicBatchReplaceResult>, WccTextAtomicBatchReplaceResult> commitGuard,
        CancellationToken cancellationToken);
    private delegate Task<WccTextAtomicBatchReplaceResult> MixedBatchWriter(
        IReadOnlyList<WccTextAtomicBatchCandidate> candidates,
        IReadOnlyList<WccTextAtomicBatchCreationCandidate> creations,
        Func<IReadOnlyList<WccTextAtomicBatchStagedFile>, CancellationToken, Task<string?>> verifyBeforeFirstWrite,
        Func<Func<WccTextAtomicBatchReplaceResult>, WccTextAtomicBatchReplaceResult> commitGuard,
        CancellationToken cancellationToken);
    private readonly BatchWriter _batchWriter;
    private readonly MixedBatchWriter? _mixedBatchWriter;
    private delegate Task<WccTextAtomicBatchReplaceResult> ResourceBatchWriter(
        IReadOnlyList<WccTextAtomicBatchCandidate> replacements,
        IReadOnlyList<WccTextAtomicBatchCreationCandidate> creations,
        IReadOnlyList<WccBinaryAtomicBatchCreationCandidate> binaryCreations,
        Func<IReadOnlyList<WccAtomicBatchStagedFile>, CancellationToken, Task<string?>> verifyBeforeFirstWrite,
        Func<Func<WccTextAtomicBatchReplaceResult>, WccTextAtomicBatchReplaceResult> commitGuard,
        CancellationToken cancellationToken);
    private readonly ResourceBatchWriter? _resourceBatchWriter;
    private delegate Task<WccTextAtomicBatchReplaceResult> MutationBatchWriter(
        IReadOnlyList<WccTextAtomicBatchCandidate> replacements,
        IReadOnlyList<WccTextAtomicBatchCreationCandidate> creations,
        IReadOnlyList<WccBinaryAtomicBatchCreationCandidate> binaryCreations,
        IReadOnlyList<WccBinaryAtomicBatchDeletionCandidate> deletions,
        Func<IReadOnlyList<WccAtomicBatchStagedFile>, CancellationToken, Task<string?>> verifyBeforeFirstWrite,
        Func<Func<WccTextAtomicBatchReplaceResult>, WccTextAtomicBatchReplaceResult> commitGuard,
        CancellationToken cancellationToken);
    private readonly MutationBatchWriter? _mutationBatchWriter;
    private readonly bool _collectDiagnostics;
    private readonly object _stateGate = new();
    private readonly SemaphoreSlim _operations = new(1, 1);
    private Binding? _binding;
    private WccProjectSourceEditResult? _recovery;
    private WccSourceSaveDiagnostics? _lastDiagnostics;
    public WccProjectSourceEditResult? Recovery { get { lock (_stateGate) return _recovery; } }
    public WccSourceSaveDiagnostics? LastDiagnostics { get { lock (_stateGate) return _lastDiagnostics; } }

    public WccProjectSourceEditCoordinator() : this(collectDiagnostics: false) { }
    public WccProjectSourceEditCoordinator(bool collectDiagnostics)
    {
        _batchWriter = WccTextAtomicBatchReplacer.ReplaceAsync;
        _mixedBatchWriter = WccTextAtomicBatchReplacer.ReplaceAsync;
        _resourceBatchWriter = WccTextAtomicBatchReplacer.ReplaceAsync;
        _mutationBatchWriter = WccTextAtomicBatchReplacer.ReplaceAsync;
        _collectDiagnostics = collectDiagnostics;
    }

    // Private deterministic I/O seam; review and opaque-ticket guards still run.
    private WccProjectSourceEditCoordinator(BatchWriter batchWriter) => _batchWriter = batchWriter;

    public void SetSession(IEnumerable<WccScenarioFieldEditSessionState> states)
    {
        lock (_stateGate)
        {
            _binding = null;
            ArgumentNullException.ThrowIfNull(states);
            var published = states.ToArray();
            if (published.Length is 0 or > 8 || published.Any(state => state is null || state.Source is null
                    || state.Snapshot is null || state.Owner is null || state.Context is null
                    || string.IsNullOrWhiteSpace(state.ProjectIdentity) || string.IsNullOrWhiteSpace(state.ProjectRevision)
                    || !Path.IsPathFullyQualified(state.ProjectRootPath))
                || published.Select(state => state.Source.Path).Distinct(PathComparer).Count() != published.Length)
                throw new ArgumentException("A bounded unique complete source/session publication is required.", nameof(states));
            var first = published[0];
            var manifestText = JsonSerializer.Serialize(first.Snapshot.Manifest);
            var planText = JsonSerializer.Serialize(first.Plan);
            var packageRawIntentText = JsonSerializer.Serialize(first.EntryReview);
            if (published.Any(state => state.ProjectIdentity != first.ProjectIdentity
                    || !PathComparer.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(state.ProjectRootPath)),
                        Path.TrimEndingDirectorySeparator(Path.GetFullPath(first.ProjectRootPath)))
                    || state.RootRevisionId != first.RootRevisionId || state.ProfileIdentity != first.ProfileIdentity
                    || !SameOptionalDirectory(state.ConfiguredAddonsPath, first.ConfiguredAddonsPath)
                    || JsonSerializer.Serialize(state.Snapshot.Manifest) != manifestText
                    || JsonSerializer.Serialize(state.Plan) != planText
                    || JsonSerializer.Serialize(state.EntryReview) != packageRawIntentText))
                throw new ArgumentException("Combined sources must share the exact project, pinned manifest, profile and campaign context plan.", nameof(states));
            var manifest = JsonSerializer.Deserialize<WccLibraryManifest>(manifestText)
                ?? throw new ArgumentException("The source manifest could not be frozen.", nameof(states));
            var snapshot = WccLibrarySnapshotBuilder.Build(manifest, []);
            var frozen = published.Select(state => state with
            {
                Snapshot = snapshot,
                ProjectRootPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(state.ProjectRootPath)),
                ConfiguredAddonsPath = NormalizeOptionalDirectory(state.ConfiguredAddonsPath)
            }).ToArray();
            _binding = new(Array.AsReadOnly(frozen), Array.AsReadOnly(published), WccContentHash.ComputeText(manifestText));
        }
    }

    public void InvalidateSession() { lock (_stateGate) _binding = null; }

    public ValueTask<WccProjectSourceEditResult> PrepareAsync(IEnumerable<WccProjectSourceEditRequest> requests,
        CancellationToken cancellationToken = default) => _collectDiagnostics
        ? ObserveAsync("ProjectPrepare", diagnostics => PrepareCoreAsync(requests, cancellationToken, diagnostics))
        : PrepareCoreAsync(requests, cancellationToken, null);

    private async ValueTask<WccProjectSourceEditResult> PrepareCoreAsync(IEnumerable<WccProjectSourceEditRequest> requests,
        CancellationToken cancellationToken, WccSourceSaveDiagnosticCollector? diagnostics)
    {
        if (requests is null) return new(WccProjectSourceEditStatus.InvalidRequest, "Combined source requests are required.");
        var captured = requests.ToArray();
        if (diagnostics is not null)
        {
            diagnostics.Add(WccSourceSaveCount.RequestedFiles, captured.Length);
            foreach (var request in captured.Where(request => request is not null))
            {
                diagnostics.Add(WccSourceSaveCount.RequestedFields, request.Fields.Count);
                diagnostics.Add(WccSourceSaveCount.RequestedRawPatches, request.RawPatches.Count);
                if (request.PackageEdit is not null) diagnostics.Add(WccSourceSaveCount.RequestedStructuralOperations);
                if (request.CampaignStructure is not null) diagnostics.Add(WccSourceSaveCount.RequestedStructuralOperations);
                if (request.CampaignCredits is not null) diagnostics.Add(WccSourceSaveCount.RequestedStructuralOperations);
                if (request.PresetStorage is { } storage) diagnostics.Add(WccSourceSaveCount.RequestedStructuralOperations, storage.Presets.Count + 1);
                if (request.ScenarioCreation is not null) diagnostics.Add(WccSourceSaveCount.RequestedStructuralOperations);
                if (request.UnitTypeCreation is not null) diagnostics.Add(WccSourceSaveCount.RequestedStructuralOperations);
                diagnostics.Add(WccSourceSaveCount.RequestedStructuralOperations, request.DifficultyFields.Count);
                if (request.SideTopology is { } topology)
                    diagnostics.Add(WccSourceSaveCount.RequestedStructuralOperations, topology.Removals.Count + topology.AddedCount);
                diagnostics.Add(WccSourceSaveCount.RequestedStructuralOperations,
                    request.LeaderAdditions.Sum(edit => edit?.Leaders.Count ?? 0));
                diagnostics.Add(WccSourceSaveCount.RequestedStructuralOperations, request.UnitInstances.Count);
                if (request.InitialSettings is { } settings)
                    diagnostics.Add(WccSourceSaveCount.RequestedStructuralOperations,
                        (settings.Music is null ? 0 : 1) + (settings.Schedule is null ? 0 : 1) + settings.SideFlags.Count + settings.Events.Count);
            }
        }
        if (captured.Length is 0 or > 8 || captured.Any(request => request is null)
            || captured.Select(request => request.Source.Path).Distinct(PathComparer).Count() != captured.Length)
            return new(WccProjectSourceEditStatus.InvalidRequest, "Each pending source must have one complete request.");
        using (diagnostics?.Measure(WccSourceSavePhase.QueueWait))
            await _operations.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Binding binding;
            using var sessionMeasurement = diagnostics?.Measure(WccSourceSavePhase.SessionValidation);
            lock (_stateGate)
            {
                if (CheckBinding() is { } refusal) return refusal;
                binding = _binding!;
            }
            diagnostics?.Add(WccSourceSaveCount.SessionStates, binding.States.Count);
            if (captured.Any(request => !binding.States.Any(state => SameSource(state.Source, request.Source))
                    || request.PackageEdit is { } package && !ReferenceEquals(package.Source, request.Source)
                    || request.CampaignStructure is { } structure && !ReferenceEquals(structure.Source, request.Source)
                    || request.CampaignCredits is { } credits && !ReferenceEquals(credits.Source, request.Source)
                    || request.PresetStorage is { } storage && !ReferenceEquals(storage.Source, request.Source)
                    || request.ScenarioCreation is { } scenario && !ReferenceEquals(scenario.Source, request.Source)
                    || request.UnitTypeCreation is { } unit && !ReferenceEquals(unit.Source, request.Source)
                    || request.DifficultyFields.Any(edit => edit is null || !ReferenceEquals(edit.Source, request.Source))
                    || request.SideTopology is { } topology && !ReferenceEquals(topology.Source, request.Source)
                    || request.StoryEdit is { } story && !ReferenceEquals(story.Source, request.Source)
                    || request.LeaderAdditions.Any(edit => edit is null || !ReferenceEquals(edit.Source, request.Source))
                    || request.UnitInstances.Any(edit => edit is null || !ReferenceEquals(edit.Source, request.Source))
                    || request.Fields.Any(proposal => proposal is null || proposal.Route is null
                        || !SameSource(proposal.Route.Source, request.Source))))
                return new(WccProjectSourceEditStatus.InvalidRequest, "A draft does not belong to its exact published source.");
            sessionMeasurement?.Dispose();
            var packageEdit = captured.Select(request => request.PackageEdit).SingleOrDefault(edit => edit is not null);
            if (packageEdit is not null && (captured.Length != 1 || !WccScenarioFieldStaticReviewer.IsManagedPackageOnly(captured[0])))
                return new(WccProjectSourceEditStatus.InvalidRequest, "Save or discard other pending edits before changing package dependencies or chapters.");
            var creationEdits = captured.Where(request => request.ScenarioCreation is not null).ToArray();
            if (creationEdits.Length > 1)
                return new(WccProjectSourceEditStatus.InvalidRequest, "Create one scenario at a time in the collected save.");
            var unitEdits = captured.Where(request => request.UnitTypeCreation is not null).ToArray();
            var unitUpdates = captured.Select(request => request.UnitTypeUpdate).Where(edit => edit is not null).Select(edit => edit!).ToArray();
            var storyEdits = captured.Select(request => request.StoryEdit).Where(edit => edit is not null).Select(edit => edit!).ToArray();
            if (RecheckStories(storyEdits) is { } storyRefusal) return storyRefusal;
            if (unitUpdates.Length > 1 || unitUpdates.Length != 0 && unitEdits.Length != 0)
                return new(WccProjectSourceEditStatus.InvalidRequest, "Save one existing unit or create one unit at a time.");
            if (RecheckUnitUpdates(unitUpdates) is { } unitUpdateRefusal) return unitUpdateRefusal;
            if (unitEdits.Length > 1)
                return new(WccProjectSourceEditStatus.InvalidRequest, "Create one unit type at a time in the collected save.");
            var copiedSources = unitEdits.Select(request => request.UnitTypeCreation!.Spec.Copy)
                .Where(source => source is not null).Select(source => source!).ToArray();
            if (RecheckCopiedSources(copiedSources) is { } copiedSourceRefusal) return copiedSourceRefusal;
            var materializations = unitEdits.Select(request => request.UnitTypeCreation!.Spec.Materialization)
                .Where(plan => plan is not null).Select(plan => plan!).ToArray();
            if (RecheckMaterializedSources(materializations) is { } materializedSourceRefusal) return materializedSourceRefusal;
            var binaryPlans = unitEdits.Select(request => WccUnitTypeMaterializationPlanner.PlanBinaryDestinations(
                request.UnitTypeCreation!.Spec, binding.States[0].ProjectRootPath))
                .Concat(unitUpdates.Select(WccUnitTypeUpdatePlanner.PlanBinaryDestinations))
                .Concat(storyEdits.Select(WccScenarioStoryPlanner.PlanBinaryDestinations)).Concat(packageEdit is null ? [] : new[] { packageEdit.BinaryDestinations }).ToArray();
            var binaryCreates = Array.AsReadOnly(binaryPlans.SelectMany(plan => plan.Creates).ToArray());
            var binaryReuses = Array.AsReadOnly(binaryPlans.SelectMany(plan => plan.Reuses).ToArray());
            if (RecheckBinaryReuses(binaryReuses) is { } binaryReuseRefusal) return binaryReuseRefusal;
            var storageEdits = captured.Where(request => request.PresetStorage is not null).ToArray();
            if (storageEdits.Length > 1)
                return new(WccProjectSourceEditStatus.InvalidRequest, "Keep one preset storage recipe in the collected save.");
            var attachedCandidates = storageEdits.SelectMany(request =>
                WccCampaignLocalPresetStoragePlanner.PlanStorageCandidates(request.PresetStorage!)).ToArray();
            var ownershipCandidates = creationEdits.Select(request =>
                    WccScenarioCreationPlanner.PlanOwnershipCandidate(request.ScenarioCreation!))
                .Where(candidate => candidate is not null).Select(candidate => candidate!).ToArray();
            if (ownershipCandidates.Any(ownership => attachedCandidates.Any(candidate =>
                    PathComparer.Equals(candidate.Source.Path, ownership.Source.Path))))
                return new(WccProjectSourceEditStatus.InvalidRequest,
                    "Save preset definitions before creating a scenario when both update the package authoring companion. Both drafts are retained.");
            var replacementSources = (packageEdit is null ? captured.Select(request => request.Source) : packageEdit.SourceReplacements.Select(file => file.Source))
                .Concat(attachedCandidates.Select(candidate => candidate.Source)).Concat(unitUpdates.Select(edit => edit.OriginalSource))
                .Concat(WccScenarioStoryPlanner.PlanCombinedDefinitionCandidates(storyEdits).Select(candidate => candidate.Source))
                .Concat(ownershipCandidates.Select(candidate => candidate.Source)).ToArray();
            var sourceCreates = Array.AsReadOnly(creationEdits.SelectMany(request =>
                WccScenarioCreationPlanner.PlanSources(request.ScenarioCreation!))
                .Concat(unitEdits.SelectMany(request => WccUnitTypeCreationPlanner.PlanSources(request.UnitTypeCreation!)))
                .Concat(unitEdits.SelectMany(request => WccUnitTypeMaterializationPlanner.PlanMissingHelperSources(
                    request.UnitTypeCreation!, binding.States[0].Snapshot, binding.States[0].RootRevisionId)))
                .Concat(storageEdits.SelectMany(request => WccCampaignLocalPresetStoragePlanner.PlanSources(request.PresetStorage!))).ToArray());
            sourceCreates = Array.AsReadOnly(sourceCreates.Concat(unitUpdates.SelectMany(edit => edit.SourceCreates)).Concat(packageEdit?.SourceCreates ?? []).ToArray());
            if (sourceCreates.Count + replacementSources.Length > 8 ||
                replacementSources.Select(source => source.Path).Concat(sourceCreates.Select(source => source.Path))
                    .Distinct(StringComparer.OrdinalIgnoreCase).Count() != replacementSources.Length + sourceCreates.Count)
                return new(WccProjectSourceEditStatus.InvalidRequest, "The new files overlap or exceed the bounded collected save.");
            if (sourceCreates.Count != 0 && _mixedBatchWriter is null)
                return new(WccProjectSourceEditStatus.InvalidRequest, "This save host has no checked new-file transaction.");
            if (binaryCreates.Count + binaryReuses.Count + sourceCreates.Count > 64
                || binaryCreates.Sum(item => item.SourceRevision.RawByteLength) + binaryReuses.Sum(item => item.SourceRevision.RawByteLength) > 64L * 1024 * 1024
                || sourceCreates.Select(source => source.Path).Concat(replacementSources.Select(source => source.Path))
                    .Concat(binaryCreates.Select(item => item.DestinationPath)).Concat(binaryReuses.Select(item => item.DestinationPath)).Distinct(PathComparer).Count()
                    != sourceCreates.Count + replacementSources.Length + binaryCreates.Count + binaryReuses.Count)
                return new(WccProjectSourceEditStatus.InvalidRequest, "The resource files overlap or exceed the checked materialization budget.");
            if (binaryCreates.Count != 0 && _resourceBatchWriter is null)
                return new(WccProjectSourceEditStatus.InvalidRequest, "This save host has no checked binary creation transaction.");
            var directoryRemovalRoutes = WccTextAtomicReplacer.CaptureDirectoryRemovalRoutes(binding.States[0].ProjectRootPath,
                packageEdit?.DirectoryRemovals ?? []);
            var deletionDisks = new List<WccBinaryDiskRevision>();
            if ((packageEdit?.SourceDeletes.Count ?? 0) + (packageEdit?.BinaryDeletes.Count ?? 0) != 0 && _mutationBatchWriter is null)
                return new(WccProjectSourceEditStatus.InvalidRequest, "This save host has no checked file removal transaction.");
            foreach (var source in packageEdit?.SourceDeletes ?? [])
            {
                var text = WccTextDiskRevisionCapture.Capture(binding.States[0].ProjectRootPath, source.Path, source.Text);
                var bytes = WccTextDiskRevisionCapture.CaptureBinary(binding.States[0].ProjectRootPath, source.Path);
                if (!text.IsCaptured || !bytes.IsCaptured || bytes.Revision is null
                    || text.Revision!.SourceIdentity != bytes.Revision.SourceIdentity
                    || text.Revision.RawByteSha256 != bytes.Revision.RawByteSha256)
                    return new(WccProjectSourceEditStatus.DiskRevisionRefused, "An owned source changed before removal review.");
                deletionDisks.Add(bytes.Revision);
            }
            foreach (var source in packageEdit?.BinaryDeletes ?? [])
            {
                var bytes = WccTextDiskRevisionCapture.CaptureBinary(binding.States[0].ProjectRootPath, source.Path);
                if (!bytes.IsCaptured || bytes.Revision is null || bytes.Revision.RawByteSha256 != source.RawByteSha256)
                    return new(WccProjectSourceEditStatus.DiskRevisionRefused, "An owned resource changed before removal review.");
                deletionDisks.Add(bytes.Revision);
            }
            if (deletionDisks.Select(item => item.SourcePath).Concat(replacementSources.Select(item => item.Path))
                .Concat(sourceCreates.Select(item => item.Path)).Concat(binaryCreates.Select(item => item.DestinationPath))
                .Concat(binaryReuses.Select(item => item.DestinationPath)).Distinct(StringComparer.OrdinalIgnoreCase).Count()
                != deletionDisks.Count + replacementSources.Length + sourceCreates.Count + binaryCreates.Count + binaryReuses.Count
                || deletionDisks.Count + replacementSources.Length + sourceCreates.Count + binaryCreates.Count > 256)
                return new(WccProjectSourceEditStatus.InvalidRequest, "Owned removals overlap another file operation or exceed the batch limit.");
            var absences = new List<WccTextDiskFileAbsence>();
            foreach (var destination in sourceCreates.Select(source => source.Path).Concat(binaryCreates.Select(item => item.DestinationPath)))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var missing = WccTextDiskRevisionCapture.CaptureCreationAbsence(binding.States[0].ProjectRootPath, destination);
                if (!missing.IsCaptured || missing.Absence is null)
                    return new(WccProjectSourceEditStatus.DiskRevisionRefused,
                        missing.ErrorMessage ?? "A new source has no proven current missing filename.");
                absences.Add(missing.Absence);
            }
            var disks = new List<WccTextDiskRevision>();
            foreach (var source in replacementSources)
            {
                using var diskMeasurement = diagnostics?.Measure(WccSourceSavePhase.DiskCapture);
                diagnostics?.Add(WccSourceSaveCount.DiskCaptures);
                var disk = WccTextDiskRevisionCapture.Capture(binding.States[0].ProjectRootPath, source.Path, source.Text);
                if (!disk.IsCaptured || disk.Revision is null) return DiskRefused(disk);
                diagnostics?.Add(WccSourceSaveCount.CapturedDiskBytes, disk.Revision.RawByteLength);
                disks.Add(disk.Revision);
            }
            var proof = await WccScenarioFieldStaticReviewer.ReviewProjectWithDiagnosticsAsync(binding.States,
                Array.AsReadOnly(captured), cancellationToken, diagnostics).ConfigureAwait(false);
            using var finalMeasurement = diagnostics?.Measure(WccSourceSavePhase.FinalValidation);
            lock (_stateGate)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (CheckBinding(binding) is { } refusal) return refusal;
                foreach (var disk in disks)
                    if (RecheckDiskWithDiagnostics(disk, diagnostics) is { } changed) return changed;
                if (!replacementSources.Select(source => (source.Path, source.ContentHash)).SequenceEqual(
                        proof.Candidate.Files.Select(file => (file.Source.Path, file.Source.ContentHash))))
                    return new(WccProjectSourceEditStatus.ReviewChanged, "The reviewed existing files differ from the exact source-bound storage proposal.");
                if (!sourceCreates.Select(source => (source.Path, source.ContentHash)).SequenceEqual(
                        proof.Candidate.SourceCreates.Select(source => (source.Path, source.ContentHash))))
                    return new(WccProjectSourceEditStatus.ReviewChanged, "The reviewed new files differ from the immutable creation proposal.");
                if (!binaryCreates.Select(item => (item.DestinationPath, item.SourceRevision.RawByteSha256)).SequenceEqual(
                        proof.Candidate.BinaryCreates.Select(item => (item.DestinationPath, item.SourceRevision.RawByteSha256))))
                    return new(WccProjectSourceEditStatus.ReviewChanged, "The reviewed binary destinations or bytes differ from the immutable recipe.");
                if (binaryReuses.Count != proof.Candidate.BinaryReuses.Count
                    || binaryReuses.Where((item, index) => !item.IsSameReceiptAs(proof.Candidate.BinaryReuses[index])).Any())
                    return new(WccProjectSourceEditStatus.ReviewChanged, "The reviewed shared resource reuse differs from the exact captured source or destination revision.");
                var requiredDirectories = absences.SelectMany(absence => absence.RequiredDirectories).Distinct(StringComparer.Ordinal)
                    .OrderBy(path => path.Length).ThenBy(path => path, StringComparer.Ordinal);
                if (!requiredDirectories.SequenceEqual(proof.Candidate.DirectoryCreates, StringComparer.Ordinal))
                    return new(WccProjectSourceEditStatus.ReviewChanged, "The reviewed directory creations differ from the exact absent destination routes.");
                foreach (var absence in absences)
                    if (RecheckAbsence(absence) is { } changed) return changed;
                if (RecheckCopiedSources(copiedSources) is { } copiedSourceChanged) return copiedSourceChanged;
                if (RecheckMaterializedSources(materializations) is { } materializedSourceChanged) return materializedSourceChanged;
                if (RecheckUnitUpdates(unitUpdates) is { } unitUpdateChanged) return unitUpdateChanged;
                if (RecheckStories(storyEdits) is { } storyChanged) return storyChanged;
                if (RecheckBinaryReuses(binaryReuses) is { } binaryReuseChanged) return binaryReuseChanged;
                if (!(packageEdit?.SourceDeletes ?? []).Select(item => (item.Path, item.ContentHash)).SequenceEqual(
                        proof.Candidate.SourceDeletes.Select(item => (item.Path, item.ContentHash)))
                    || !(packageEdit?.BinaryDeletes ?? []).SequenceEqual(proof.Candidate.BinaryDeletes)
                    || !(packageEdit?.DirectoryRemovals ?? []).SequenceEqual(proof.Candidate.DirectoryRemovals, StringComparer.Ordinal))
                    return new(WccProjectSourceEditStatus.ReviewChanged, "The owned removals differ from the exact reviewed proposal.");
                foreach (var disk in deletionDisks)
                {
                    var observed = WccTextDiskRevisionCapture.CaptureBinary(disk.ProjectRootPath, disk.SourcePath);
                    if (!observed.IsCaptured || observed.Revision is null || !disk.IsSameRevisionAs(observed.Revision))
                        return new(WccProjectSourceEditStatus.DiskRevisionRefused, "An owned file changed during removal review.");
                }
                var ticket = new WccProjectSourceEditTicket(this, new Prepared(binding, proof, Array.AsReadOnly(disks.ToArray()))
                    { DirectoryRemovalRoutes = directoryRemovalRoutes, DeletionDisks = Array.AsReadOnly(deletionDisks.ToArray()), Absences = Array.AsReadOnly(absences.ToArray()), CopiedSources = Array.AsReadOnly(copiedSources),
                        Materializations = Array.AsReadOnly(materializations), UnitUpdates = Array.AsReadOnly(unitUpdates), Stories = Array.AsReadOnly(storyEdits) });
                return new(WccProjectSourceEditStatus.Prepared,
                    "All pending sources are reviewed together in the menu and every declared play context.", ticket, proof.Candidate);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or InvalidOperationException or ArgumentException or NotSupportedException)
        {
            return new(WccProjectSourceEditStatus.ReviewRefused, "The combined source review was refused: " + exception.Message);
        }
        finally { _operations.Release(); }
    }

    public ValueTask<WccProjectSourceEditResult> CommitAsync(WccProjectSourceEditTicket ticket,
        CancellationToken cancellationToken = default) => _collectDiagnostics
        ? ObserveAsync("ProjectCommit", diagnostics => CommitCoreAsync(ticket, cancellationToken, diagnostics))
        : CommitCoreAsync(ticket, cancellationToken, null);

    private async ValueTask<WccProjectSourceEditResult> CommitCoreAsync(WccProjectSourceEditTicket ticket,
        CancellationToken cancellationToken, WccSourceSaveDiagnosticCollector? diagnostics)
    {
        if (ticket is null || !ticket.BelongsTo(this) || ticket.Proof is not Prepared prepared)
            return new(WccProjectSourceEditStatus.InvalidTicket, "This combined save ticket belongs to another coordinator.");
        if (!ticket.TryConsume()) return new(WccProjectSourceEditStatus.TicketAlreadyConsumed, "This combined ticket was already attempted.");
        using (diagnostics?.Measure(WccSourceSavePhase.QueueWait))
            await _operations.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (_stateGate)
                if (CheckBinding(prepared.Binding) is { } refusal) return refusal;
            foreach (var disk in prepared.Disks)
                if (RecheckDiskWithDiagnostics(disk, diagnostics) is { } changed) return changed;
            foreach (var absence in prepared.Absences)
                if (RecheckAbsence(absence) is { } changed) return changed;
            if (RecheckCopiedSources(prepared.CopiedSources) is { } copiedSourceChanged) return copiedSourceChanged;
            if (RecheckMaterializedSources(prepared.Materializations) is { } materializedSourceChanged) return materializedSourceChanged;
            if (RecheckUnitUpdates(prepared.UnitUpdates) is { } unitUpdateChanged) return unitUpdateChanged;
            if (RecheckStories(prepared.Stories) is { } storyChanged) return storyChanged;
            if (RecheckBinaryReuses(prepared.Review.Candidate.BinaryReuses) is { } binaryReuseChanged) return binaryReuseChanged;
            var candidates = prepared.Review.Candidate.Files
                .Where(file => file.Source.Text != file.CandidateSource.Text)
                .Select(file => new WccTextAtomicBatchCandidate(
                prepared.Disks.Single(disk => PathComparer.Equals(disk.SourcePath, file.Source.Path)),
                file.CandidateSource.Text, file.CandidateSource.ContentHash)).ToArray();
            var creations = prepared.Review.Candidate.SourceCreates.Select(source => new WccTextAtomicBatchCreationCandidate(
                prepared.Absences.Single(absence => PathComparer.Equals(absence.SourcePath, source.Path)),
                source.Text, source.ContentHash)).ToArray();
            var binaryCreations = prepared.Review.Candidate.BinaryCreates.Select(item => new WccBinaryAtomicBatchCreationCandidate(
                prepared.Absences.Single(absence => PathComparer.Equals(absence.SourcePath, item.DestinationPath)), item.SourceRevision)).ToArray();
            var deletions = prepared.DeletionDisks.Select(item => new WccBinaryAtomicBatchDeletionCandidate(item)).ToArray();
            diagnostics?.Add(WccSourceSaveCount.RequestedFiles, candidates.Length + creations.Length + binaryCreations.Length + deletions.Length);
            diagnostics?.Add(WccSourceSaveCount.BatchWriteAttempts);
            using var writeMeasurement = diagnostics?.Measure(WccSourceSavePhase.BatchWrite);
            async Task<string?> VerifyStages(IReadOnlyList<WccTextDiskRevision> ownedStages, CancellationToken token)
            {
                using var freshnessMeasurement = diagnostics?.Measure(WccSourceSavePhase.CommitFreshness);
                diagnostics?.Add(WccSourceSaveCount.CommitFreshnessChecks);
                lock (_stateGate) if (CheckBinding(prepared.Binding) is { } refusal) return refusal.Diagnostic;
                if (RecheckCopiedSources(prepared.CopiedSources) is { } copiedSourceChanged) return copiedSourceChanged.Diagnostic;
                if (RecheckMaterializedSources(prepared.Materializations) is { } materializedSourceChanged) return materializedSourceChanged.Diagnostic;
                if (RecheckUnitUpdates(prepared.UnitUpdates) is { } unitUpdateChanged) return unitUpdateChanged.Diagnostic;
                if (RecheckStories(prepared.Stories, checkAbsence: false) is { } storyChanged) return storyChanged.Diagnostic;
                if (RecheckBinaryReuses(prepared.Review.Candidate.BinaryReuses) is { } binaryReuseChanged) return binaryReuseChanged.Diagnostic;
                return await WccScenarioFieldStaticReviewer.VerifyProjectPreparedWithDiagnosticsAsync(prepared.Binding.States,
                    prepared.Review, ownedStages, token, diagnostics).ConfigureAwait(false);
            }
            WccTextAtomicBatchReplaceResult GuardCommit(Func<WccTextAtomicBatchReplaceResult> commit)
            {
                using var nativeMeasurement = diagnostics?.Measure(WccSourceSavePhase.NativeCommit);
                diagnostics?.Add(WccSourceSaveCount.NativeCommitAttempts);
                lock (_stateGate)
                {
                    if (CheckBinding(prepared.Binding) is { } refusal)
                        return new(WccTextAtomicBatchReplaceStatus.Refused, refusal.Diagnostic);
                    if (RecheckCopiedSources(prepared.CopiedSources) is { } copiedSourceChanged)
                        return new(WccTextAtomicBatchReplaceStatus.Refused, copiedSourceChanged.Diagnostic);
                    if (RecheckMaterializedSources(prepared.Materializations) is { } materializedSourceChanged)
                        return new(WccTextAtomicBatchReplaceStatus.Refused, materializedSourceChanged.Diagnostic);
                    if (RecheckUnitUpdates(prepared.UnitUpdates) is { } unitUpdateChanged)
                        return new(WccTextAtomicBatchReplaceStatus.Refused, unitUpdateChanged.Diagnostic);
                    if (RecheckStories(prepared.Stories, checkAbsence: false) is { } storyChanged)
                        return new(WccTextAtomicBatchReplaceStatus.Refused, storyChanged.Diagnostic);
                    if (RecheckBinaryReuses(prepared.Review.Candidate.BinaryReuses) is { } binaryReuseChanged)
                        return new(WccTextAtomicBatchReplaceStatus.Refused, binaryReuseChanged.Diagnostic);
                    return commit();
                }
            }
            Task<string?> VerifyMixedStages(IReadOnlyList<WccTextAtomicBatchStagedFile> stages, CancellationToken token)
            {
                var expected = candidates.Select(candidate => (candidate.Expected.SourcePath,
                        candidate.CandidateTextSha256, IsCreation: false))
                    .Concat(creations.Select(candidate => (candidate.ExpectedAbsence.SourcePath,
                        candidate.CandidateTextSha256, IsCreation: true))).ToArray();
                if (stages.Count != expected.Length || stages.Select(stage => stage.SourcePath).Distinct(PathComparer).Count() != stages.Count
                    || stages.Any(stage => !expected.Any(file => PathComparer.Equals(file.SourcePath, stage.SourcePath)
                        && file.IsCreation == stage.IsCreation && file.CandidateTextSha256 == stage.Revision.DecodedTextSha256)))
                    return Task.FromResult<string?>("The staged files do not match every exact reviewed destination and payload.");
                return VerifyStages(Array.AsReadOnly(stages.Select(stage => stage.Revision).ToArray()), token);
            }
            async Task<string?> VerifyResourceStages(IReadOnlyList<WccAtomicBatchStagedFile> stages, CancellationToken token)
            {
                var textExpected = candidates.Select(item => (Path: item.Expected.SourcePath, Hash: item.CandidateTextSha256, Create: false))
                    .Concat(creations.Select(item => (Path: item.ExpectedAbsence.SourcePath, Hash: item.CandidateTextSha256, Create: true))).ToArray();
                var textStages = stages.Where(item => item.TextRevision is not null && item.BinaryRevision is null).ToArray();
                var binaryStages = stages.Where(item => item.BinaryRevision is not null && item.TextRevision is null && !item.IsDeletion).ToArray();
                var deletionStages = stages.Where(item => item.IsDeletion).ToArray();
                if (stages.Count != textExpected.Length + binaryCreations.Length + deletions.Length || textStages.Length != textExpected.Length
                    || binaryStages.Length != binaryCreations.Length || deletionStages.Length != deletions.Length || stages.Select(item => item.SourcePath).Distinct(PathComparer).Count() != stages.Count
                    || textStages.Any(item => !textExpected.Any(expected => PathComparer.Equals(expected.Path, item.SourcePath)
                        && expected.Hash == item.TextRevision!.DecodedTextSha256 && expected.Create == item.IsCreation))
                    || deletionStages.Any(item => item.BinaryRevision is null || item.TextRevision is not null
                        || !deletions.Any(expected => PathComparer.Equals(expected.Expected.SourcePath, item.SourcePath)
                            && expected.Expected.RawByteSha256 == item.BinaryRevision.RawByteSha256
                            && expected.Expected.RawByteLength == item.BinaryRevision.RawByteLength))
                    || binaryStages.Any(item => !item.IsCreation || !prepared.Review.Candidate.BinaryCreates.Any(expected =>
                        PathComparer.Equals(expected.DestinationPath, item.SourcePath)
                        && expected.SourceRevision.RawByteSha256 == item.BinaryRevision!.RawByteSha256
                        && expected.SourceRevision.RawByteLength == item.BinaryRevision.RawByteLength)))
                    return "The staged text and binary files do not match every exact reviewed destination and payload.";
                lock (_stateGate) if (CheckBinding(prepared.Binding) is { } refusal) return refusal.Diagnostic;
                if (RecheckCopiedSources(prepared.CopiedSources) is { } copyChanged) return copyChanged.Diagnostic;
                if (RecheckMaterializedSources(prepared.Materializations) is { } materializedChanged) return materializedChanged.Diagnostic;
                if (RecheckUnitUpdates(prepared.UnitUpdates) is { } unitUpdateChanged) return unitUpdateChanged.Diagnostic;
                if (RecheckStories(prepared.Stories, checkAbsence: false) is { } storyChanged) return storyChanged.Diagnostic;
                if (RecheckBinaryReuses(prepared.Review.Candidate.BinaryReuses) is { } binaryReuseChanged) return binaryReuseChanged.Diagnostic;
                return await WccScenarioFieldStaticReviewer.VerifyProjectPreparedWithResourcesAsync(prepared.Binding.States,
                    prepared.Review, Array.AsReadOnly(textStages.Select(item => item.TextRevision!).ToArray()),
                    Array.AsReadOnly(binaryStages.Concat(deletionStages).Select(item => item.BinaryRevision!).ToArray()), token, diagnostics).ConfigureAwait(false);
            }
            async Task<WccTextAtomicBatchReplaceResult> VerifyUnchangedSources()
            {
                if (await VerifyStages([], cancellationToken).ConfigureAwait(false) is { } changed)
                    return new(WccTextAtomicBatchReplaceStatus.Refused, changed);
                return GuardCommit(() => new(WccTextAtomicBatchReplaceStatus.Succeeded,
                    "The checked sources already match the requested unit draft."));
            }
            var save = candidates.Length == 0 && creations.Length == 0 && binaryCreations.Length == 0 && deletions.Length == 0
                ? await VerifyUnchangedSources().ConfigureAwait(false)
                : deletions.Length != 0
                ? await _mutationBatchWriter!(candidates, creations, binaryCreations, deletions, VerifyResourceStages, GuardCommit, cancellationToken).ConfigureAwait(false)
                : binaryCreations.Length != 0
                ? await _resourceBatchWriter!(candidates, creations, binaryCreations, VerifyResourceStages, GuardCommit, cancellationToken).ConfigureAwait(false)
                : creations.Length == 0
                ? await _batchWriter(candidates, VerifyStages, GuardCommit, cancellationToken).ConfigureAwait(false)
                : await _mixedBatchWriter!(candidates, creations,
                    VerifyMixedStages,
                    GuardCommit, cancellationToken).ConfigureAwait(false);
            // Native file batches release their retained route handles before returning.
            // Empty-directory cleanup is part of this same checked coordinator operation,
            // before publication, and never turns a completed file Save into a failure.
            if (save.IsSuccess && prepared.DirectoryRemovalRoutes.Count != 0)
            {
                lock (_stateGate)
                {
                    save = CheckBinding(prepared.Binding) is null
                        ? WccTextAtomicReplacer.CleanupEmptyReviewedDirectories(save, prepared.DirectoryRemovalRoutes)
                        : new(WccTextAtomicBatchReplaceStatus.SucceededWithCleanupWarning,
                            save.Diagnostic + " Chapter folders were retained because the editing session changed after the file Save.", save.Files)
                            { CreatedDirectories = save.CreatedDirectories };
                }
            }
            writeMeasurement?.Dispose();
            diagnostics?.Add(WccSourceSaveCount.ReplacedFiles, save.Files.Count(file => file.WasReplaced));
            var result = new WccProjectSourceEditResult(save.Status switch
            {
                WccTextAtomicBatchReplaceStatus.Succeeded => WccProjectSourceEditStatus.Succeeded,
                WccTextAtomicBatchReplaceStatus.SucceededWithCleanupWarning => WccProjectSourceEditStatus.SucceededWithCleanupWarning,
                WccTextAtomicBatchReplaceStatus.RecoveryRequired => WccProjectSourceEditStatus.RecoveryRequired,
                _ => save.Files.Any(file => file.Save?.Status == WccTextAtomicReplaceStatus.FileInUse)
                    ? WccProjectSourceEditStatus.FileInUse : WccProjectSourceEditStatus.SaveRefused
            }, save.Diagnostic, candidate: prepared.Review.Candidate,
                files: save.Files.Select(file => new WccProjectSourceFileOutcome(file.SourcePath, file.Save, file.WasReplaced, file.Diagnostic)
                    { WasCreated = file.WasCreated, WasDeleted = file.WasDeleted })) { RemovedDirectories = save.RemovedDirectories };
            lock (_stateGate)
            {
                if (result.Status == WccProjectSourceEditStatus.RecoveryRequired) _recovery = result;
                if (result.IsSuccess || result.Status == WccProjectSourceEditStatus.RecoveryRequired) _binding = null;
            }
            return result;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or InvalidOperationException or ArgumentException or NotSupportedException)
        {
            return new(WccProjectSourceEditStatus.SaveRefused, "The combined source save was refused: " + exception.Message);
        }
        finally { _operations.Release(); }
    }

    private async ValueTask<WccProjectSourceEditResult> ObserveAsync(string operationName,
        Func<WccSourceSaveDiagnosticCollector, ValueTask<WccProjectSourceEditResult>> operation)
    {
        var diagnostics = new WccSourceSaveDiagnosticCollector(operationName);
        string? status = null;
        var cancelled = false;
        var failed = false;
        try
        {
            var result = await operation(diagnostics).ConfigureAwait(false);
            status = result.Status.ToString();
            return result;
        }
        catch (OperationCanceledException) { cancelled = true; throw; }
        catch { failed = true; throw; }
        finally
        {
            var snapshot = diagnostics.Complete(status, cancelled, failed);
            lock (_stateGate) _lastDiagnostics = snapshot;
        }
    }

    private WccProjectSourceEditResult? CheckBinding(Binding? expected = null)
    {
        if (_recovery is not null) return _recovery;
        if (expected is not null && !ReferenceEquals(expected, _binding))
            return new(WccProjectSourceEditStatus.StaleSession, "A source, context, index, access or project publication changed.");
        if (_binding is null) return new(WccProjectSourceEditStatus.SessionUnavailable, "No current combined source editing session is bound.");
        if (_binding.States.Any(state => !state.IsWritable)) return new(WccProjectSourceEditStatus.ReadOnly, "A pending source is read-only.");
        if (_binding.Published.Any(state => WccContentHash.ComputeText(JsonSerializer.Serialize(state.Snapshot.Manifest)) != _binding.ManifestHash))
            return new(WccProjectSourceEditStatus.StaleSession, "The published pinned package manifest changed.");
        return null;
    }

    private static WccProjectSourceEditResult? RecheckDisk(WccTextDiskRevision expected)
        => RecheckDiskWithDiagnostics(expected, null);

    private static WccProjectSourceEditResult? RecheckDiskWithDiagnostics(WccTextDiskRevision expected,
        WccSourceSaveDiagnosticCollector? diagnostics)
    {
        using var measurement = diagnostics?.Measure(WccSourceSavePhase.DiskRecheck);
        diagnostics?.Add(WccSourceSaveCount.DiskCaptures);
        var disk = WccTextDiskRevisionCapture.Capture(expected.ProjectRootPath, expected.SourcePath, expected.Text);
        if (!disk.IsCaptured || disk.Revision is null) return DiskRefused(disk);
        diagnostics?.Add(WccSourceSaveCount.CapturedDiskBytes, disk.Revision.RawByteLength);
        return expected.IsSameRevisionAs(disk.Revision) ? null
            : new(WccProjectSourceEditStatus.DiskRevisionRefused, "A source's bytes, file identity or directory route changed after review.");
    }
    private static WccProjectSourceEditResult DiskRefused(WccTextDiskRevisionCaptureResult disk) =>
        new(disk.NativeErrorCode is 32 or 33 ? WccProjectSourceEditStatus.FileInUse : WccProjectSourceEditStatus.DiskRevisionRefused,
            disk.ErrorMessage ?? "An exact pending source disk revision could not be captured.");
    private static WccProjectSourceEditResult? RecheckAbsence(WccTextDiskFileAbsence expected)
    {
        var current = WccTextDiskRevisionCapture.CaptureCreationAbsence(expected.ProjectRootPath, expected.SourcePath);
        return current.IsCaptured && current.Absence is { } absence && expected.IsSameAbsenceAs(absence)
            ? null : new(WccProjectSourceEditStatus.DiskRevisionRefused,
                current.ErrorMessage ?? "A new filename or its directory route changed after review.");
    }
    private static WccProjectSourceEditResult? RecheckCopiedSources(IEnumerable<WccUnitTypeAuthoredCopySource> sources)
    {
        foreach (var source in sources)
            if (WccUnitTypeAuthoredCopyPlanner.VerifySourceFreshness(source) is { } refusal)
                return new(WccProjectSourceEditStatus.DiskRevisionRefused, refusal);
        return null;
    }
    private static WccProjectSourceEditResult? RecheckMaterializedSources(IEnumerable<WccUnitTypeMaterializationPlan> plans)
    {
        foreach (var plan in plans)
            if (WccUnitTypeMaterializationPlanner.VerifySourceFreshness(plan) is { } refusal)
                return new(WccProjectSourceEditStatus.DiskRevisionRefused, refusal);
        return null;
    }
    private static WccProjectSourceEditResult? RecheckBinaryReuses(IEnumerable<WccContentBinaryReuseReceipt> receipts) =>
        WccUnitTypeMaterializationPlanner.VerifyBinaryReuses(receipts) is { } refusal
            ? new(WccProjectSourceEditStatus.DiskRevisionRefused, refusal) : null;
    private static bool SameSource(Wcc.Core.Wml.WmlStaticSource left, Wcc.Core.Wml.WmlStaticSource right) =>
        WccScenarioFieldRouteAnalyzer.SameSource(left, right) && left.IsAuthored && right.IsAuthored;
    private static string? NormalizeOptionalDirectory(string? path) => path is null ? null :
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    private static bool SameOptionalDirectory(string? left, string? right) =>
        PathComparer.Equals(NormalizeOptionalDirectory(left), NormalizeOptionalDirectory(right));
    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    private sealed record Binding(IReadOnlyList<WccScenarioFieldEditSessionState> States,
        IReadOnlyList<WccScenarioFieldEditSessionState> Published, string ManifestHash);
    private sealed record Prepared(Binding Binding, WccProjectPreparedReview Review, IReadOnlyList<WccTextDiskRevision> Disks)
    {
        internal IReadOnlyList<WccBinaryDiskRevision> DeletionDisks { get; init; } = [];
        internal IReadOnlyList<WccTextDiskFileAbsence> DirectoryRemovalRoutes { get; init; } = [];
        internal IReadOnlyList<WccTextDiskFileAbsence> Absences { get; init; } = Array.Empty<WccTextDiskFileAbsence>();
        internal IReadOnlyList<WccUnitTypeAuthoredCopySource> CopiedSources { get; init; } = Array.Empty<WccUnitTypeAuthoredCopySource>();
        internal IReadOnlyList<WccUnitTypeMaterializationPlan> Materializations { get; init; } = Array.Empty<WccUnitTypeMaterializationPlan>();
        internal IReadOnlyList<WccUnitTypeUpdateEdit> UnitUpdates { get; init; } = Array.Empty<WccUnitTypeUpdateEdit>();
        internal IReadOnlyList<WccScenarioStoryEdit> Stories { get; init; } = Array.Empty<WccScenarioStoryEdit>();
    }

    private static WccProjectSourceEditResult? RecheckUnitUpdates(IEnumerable<WccUnitTypeUpdateEdit> edits)
    {
        foreach (var edit in edits)
            if (WccUnitTypeUpdatePlanner.VerifySourceFreshness(edit) is { } refusal)
                return new(WccProjectSourceEditStatus.DiskRevisionRefused, refusal);
        return null;
    }

    private static WccProjectSourceEditResult? RecheckStories(IEnumerable<WccScenarioStoryEdit> edits, bool checkAbsence = true)
    {
        foreach (var edit in edits)
            if (WccScenarioStoryPlanner.VerifyUploadFreshness(edit, checkAbsence) is { } refusal)
                return new(WccProjectSourceEditStatus.DiskRevisionRefused, refusal);
        return null;
    }
}
