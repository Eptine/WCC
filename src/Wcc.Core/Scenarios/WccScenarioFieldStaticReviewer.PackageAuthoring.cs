using System.Text.Json;
using Wcc.Core.Campaigns;
using Wcc.Core.Library;
using Wcc.Core.Projects;
using Wcc.Core.Units;
using Wcc.Core.Wml;

namespace Wcc.Core.Scenarios;

internal static partial class WccScenarioFieldStaticReviewer
{
    internal static bool IsManagedPackageOnly(WccProjectSourceEditRequest request) => request.PackageEdit is not null
        && request.Fields.Count == 0 && request.RawPatches.Count == 0 && request.InitialSettings is null
        && request.CampaignStructure is null && request.CampaignCredits is null && request.DifficultyFields.Count == 0
        && request.SideTopology is null && request.LeaderAdditions.Count == 0
        && request.PresetStorage is null && request.ScenarioCreation is null && request.UnitTypeCreation is null
        && request.UnitTypeUpdate is null && request.StoryEdit is null && request.UnitInstances.Count == 0;

    private static async Task<WccProjectPreparedReview> ReviewManagedPackageAsync(
        IReadOnlyList<WccScenarioFieldEditSessionState> states, IReadOnlyList<WccProjectSourceEditRequest> requests,
        CancellationToken cancellationToken, WccSourceSaveDiagnosticCollector? diagnostics)
    {
        if (states.Count != 1 || requests.Count != 1 || !IsManagedPackageOnly(requests[0]))
            throw new InvalidOperationException("Save other pending edits before changing package dependencies or chapters.");
        var state = states[0]; var edit = requests[0].PackageEdit!;
        if (!state.IsWritable || state.ReviewScope != WccScenarioFieldReviewScope.CampaignMenu || CheckScope(state) is { })
            throw new InvalidOperationException(CheckScope(state) ?? "Package authoring requires a writable campaign menu session.");
        if (!WccCampaignPackageEdit.Same(state.Source, edit.Source) || state.Plan is not { } oldPlan ||
            !PathComparer.Equals(edit.Source.Path, oldPlan.CampaignMainSourcePath))
            throw new InvalidOperationException("The package recipe does not belong to this exact main source and selected campaign.");
        var snapshot = ManagedPackageSnapshot(state, edit);
        var capture = await CaptureManagedPackageAsync(state, snapshot, [], [], cancellationToken, diagnostics).ConfigureAwait(false);
        if (CheckCapture(state, capture) is { } captureProblem) throw new InvalidOperationException(captureProblem);
        var catalog = WccCampaignEntryCatalog.Project(capture, state.Plan, cancellationToken);
        var selected = state.EntryReview is { } intent ? catalog.Entries.SingleOrDefault(entry => entry.Identity == intent.SelectedEntryIdentity)
            : catalog.Entries.SingleOrDefault(entry => entry.Plan?.CampaignDefine == oldPlan.CampaignDefine);
        if (!catalog.Menu.IsComplete || selected is not { IsContextComplete: true })
            throw new InvalidOperationException("The selected campaign menu and its declared contexts must be complete before package authoring.");
        var local = capture.Sources.Where(source => source.IsAuthored && Contains(state.ProjectRootPath, source.Path)).ToArray();
        var fresh = edit.Rebuild(local);
        RequireSameManagedPackageEdit(edit, fresh);
        if (fresh.Chapter is { } freshChapter && WccCampaignChapterAuthoringPlanner.VerifyUploadFreshness(freshChapter) is { } uploadProblem)
            throw new InvalidOperationException(uploadProblem);
        WccUnitTypeMaterializationPlanner.ValidateBinaryReuseMembership(fresh.BinaryDestinations.Reuses, capture, state.RootRevisionId);
        if (WccUnitTypeMaterializationPlanner.VerifyBinaryReuses(fresh.BinaryDestinations.Reuses) is { } binaryReuseProblem)
            throw new InvalidOperationException(binaryReuseProblem);
        var files = fresh.SourceReplacements.Select(replacement =>
        {
            var candidate = new WccScenarioFieldBatchCandidate(replacement.Source, replacement.CandidateSource, [],
                [new(0, replacement.Source.Text.Length, replacement.Candidate)]);
            return new WccProjectComposedFile(new(replacement.Source), candidate, []);
        }).ToArray();
        if (files.Length == 0 && fresh.SourceCreates.Count == 0 && fresh.SourceDeletes.Count == 0 &&
            fresh.BinaryDestinations.Creates.Count == 0 && fresh.BinaryDeletes.Count == 0)
            throw new InvalidOperationException("This package recipe makes no source or resource change.");
        var binaries = fresh.BinaryDestinations.Creates.Select(file => new WccCapturedBinaryCandidate(file.DestinationPath, file.SourceRevision)).ToArray();
        var directories = CaptureCreationDirectories(state.ProjectRootPath,
            fresh.SourceCreates.Select(source => source.Path).Concat(binaries.Select(file => file.DestinationPath)));
        var proposed = WccStaticSourceCapture.CreatePackageCandidateCapture(capture, state.RootRevisionId,
            files.Select(file => file.Candidate.CandidateSource).ToArray(), fresh.SourceCreates, binaries,
            directories.Select(path => new WccCapturedDirectoryCandidate(path)).ToArray(),
            fresh.SourceDeletes.Select(source => source.Path).Concat(fresh.BinaryDeletes.Select(file => file.Path)).ToArray());
        var proposedCatalog = WccCampaignEntryCatalog.Project(proposed, state.Plan, cancellationToken);
        if (!proposedCatalog.Menu.IsComplete)
            throw new InvalidOperationException("The proposed dependency or chapter loading code leaves an unresolved or malformed campaign menu.");
        if (!WmlStaticSingleplayerEnvironment.TryCreate(capture.CoreVersion, out var environment))
            throw new InvalidOperationException("The exact pinned Core cannot provide the singleplayer environment.");
        // A topology edit intentionally changes output. Validate the selected campaign and any
        // newly added entry using the normal evaluator; sibling gameplay is the author's responsibility.
        var targetDefines = fresh.Chapter?.AddedChapter is not null
            ? proposedCatalog.Entries.Where(entry => !catalog.Entries.Any(old => old.Plan?.CampaignDefine == entry.Plan?.CampaignDefine))
                .Select(entry => entry.Plan?.CampaignDefine).Append(oldPlan.CampaignDefine)
            : new[] { oldPlan.CampaignDefine }.AsEnumerable();
        var affected = targetDefines.Where(define => define is not null).Distinct(StringComparer.Ordinal).ToArray();
        foreach (var define in affected)
        {
            var candidates = proposedCatalog.Entries.Where(entry => entry.Plan?.CampaignDefine == define).ToArray();
            if (candidates.Length == 0 && fresh.Chapter?.Operation == "Remove") continue;
            if (candidates.Length != 1 || !candidates[0].IsContextComplete)
                throw new InvalidOperationException("A selected or newly created chapter has no unique complete context plan.");
            var entry = candidates[0];
            foreach (var context in entry.Plan!.Contexts)
            {
                cancellationToken.ThrowIfCancellationRequested();
                RequireComplete(EvaluateWithDiagnostics(proposed, context, entry.Plan, environment!, null, cancellationToken, diagnostics),
                    context, "Proposed package loading code");
            }
        }
        if (proposedCatalog.Entries.Count == 0)
            throw new InvalidOperationException("Keep at least one selectable campaign in the package.");
        var closure = ClosureIdentity(capture);
        var final = await CaptureManagedPackageAsync(state, snapshot, [], [], cancellationToken, diagnostics).ConfigureAwait(false);
        var finalProblem = CheckCapture(state, final);
        if (finalProblem is not null || ClosureIdentity(final) != closure)
            throw new InvalidOperationException(finalProblem ?? "The package source closure changed during review.");
        return new(new WccProjectSourceEditCandidate(files.Select(file => new WccProjectSourceFileCandidate(
                file.Candidate.Source, file.Candidate.CandidateSource, file.Candidate.Patches)))
            { SourceCreates = fresh.SourceCreates, SourceDeletes = fresh.SourceDeletes, BinaryCreates = fresh.BinaryDestinations.Creates,
                BinaryReuses = fresh.BinaryDestinations.Reuses, BinaryDeletes = fresh.BinaryDeletes, DirectoryCreates = directories, DirectoryRemovals = fresh.DirectoryRemovals }, files, closure, [ScopeIdentity(state)])
        { PackageEdit = fresh, PackageSnapshot = snapshot };
    }

    private static async Task<string?> VerifyManagedPackageAsync(IReadOnlyList<WccScenarioFieldEditSessionState> states,
        WccProjectPreparedReview proof, IReadOnlyList<WccTextDiskRevision> stages, IReadOnlyList<WccBinaryDiskRevision> binaries,
        CancellationToken cancellationToken, WccSourceSaveDiagnosticCollector? diagnostics)
    {
        if (states.Count != 1 || proof.PackageEdit is not { } edit || proof.PackageSnapshot is not { } snapshot ||
            !states.Select(ScopeIdentity).SequenceEqual(proof.ScopeIdentities, StringComparer.Ordinal))
            return "The managed package source/session scope changed after review.";
        if (CheckScope(states[0]) is { } scopeProblem) return scopeProblem;
        if (edit.Chapter is { } chapter && WccCampaignChapterAuthoringPlanner.VerifyUploadFreshness(chapter) is { } uploadProblem) return uploadProblem;
        if (WccUnitTypeMaterializationPlanner.VerifyBinaryReuses(edit.BinaryDestinations.Reuses) is { } reuseProblem) return reuseProblem;
        var capture = await CaptureManagedPackageAsync(states[0], snapshot, stages, binaries, cancellationToken, diagnostics).ConfigureAwait(false);
        if (CheckCapture(states[0], capture) is { } captureProblem) return captureProblem;
        if (ClosureIdentity(capture) != proof.ClosureIdentity) return "The package or dependency source closure changed after review.";
        WccUnitTypeMaterializationPlanner.ValidateBinaryReuseMembership(edit.BinaryDestinations.Reuses, capture, states[0].RootRevisionId);
        RequireSameManagedPackageEdit(edit, edit.Rebuild(capture.Sources.Where(source => source.IsAuthored &&
            Contains(states[0].ProjectRootPath, source.Path)).ToArray()));
        return null;
    }

    private static Task<WccStaticSourceCaptureResult> CaptureManagedPackageAsync(WccScenarioFieldEditSessionState state,
        WccLibrarySnapshot snapshot, IReadOnlyList<WccTextDiskRevision> stages, IReadOnlyList<WccBinaryDiskRevision> binaries, CancellationToken cancellationToken,
        WccSourceSaveDiagnosticCollector? diagnostics)
    {
        diagnostics?.Add(WccSourceSaveCount.SourceCaptures);
        return stages.Count == 0 && binaries.Count == 0 ? WccStaticSourceCapture.CaptureAsync(snapshot, state.RootRevisionId, state.Plan!.CampaignMainSourcePath,
            cancellationToken: cancellationToken, configuredAddonsPath: state.ConfiguredAddonsPath)
            : WccStaticSourceCapture.CaptureForStagedStoryAsync(snapshot, state.RootRevisionId, state.Plan!.CampaignMainSourcePath,
                stages, binaries, cancellationToken, state.ConfiguredAddonsPath);
    }

    private static void RequireSameManagedPackageEdit(WccCampaignPackageEdit original, WccCampaignPackageEdit fresh)
    {
        if (original.Identity != fresh.Identity || !original.SourceReplacements.Select(file => (file.Source.Path, file.Source.ContentHash, file.Candidate))
                .SequenceEqual(fresh.SourceReplacements.Select(file => (file.Source.Path, file.Source.ContentHash, file.Candidate))) ||
            !original.SourceCreates.Select(file => (file.Path, file.Text)).SequenceEqual(fresh.SourceCreates.Select(file => (file.Path, file.Text))) ||
            !original.SourceDeletes.Select(file => (file.Path, file.ContentHash, file.Text)).SequenceEqual(fresh.SourceDeletes.Select(file => (file.Path, file.ContentHash, file.Text))) ||
            !original.BinaryDeletes.SequenceEqual(fresh.BinaryDeletes) ||
            !original.DirectoryRemovals.SequenceEqual(fresh.DirectoryRemovals, StringComparer.Ordinal) ||
            original.BinaryDestinations.Creates.Count != fresh.BinaryDestinations.Creates.Count ||
            original.BinaryDestinations.Creates.Zip(fresh.BinaryDestinations.Creates).Any(pair => pair.First.DestinationPath != pair.Second.DestinationPath ||
                !pair.First.SourceRevision.IsSameRevisionAs(pair.Second.SourceRevision)) ||
            original.BinaryDestinations.Reuses.Count != fresh.BinaryDestinations.Reuses.Count ||
            original.BinaryDestinations.Reuses.Zip(fresh.BinaryDestinations.Reuses).Any(pair => !pair.First.IsSameReceiptAs(pair.Second)))
            throw new InvalidOperationException("The managed package proposal no longer rebuilds from its exact captured sources.");
    }

    private static WccLibrarySnapshot ManagedPackageSnapshot(WccScenarioFieldEditSessionState state, WccCampaignPackageEdit edit)
    {
        if (edit.SourcePins.Count == 0) return state.Snapshot;
        var snapshot = state.Snapshot;
        var rootClosure = WccLibraryRevisionVisibility.GetVisibleRevisionIds(snapshot, state.RootRevisionId);
        var corePins = rootClosure.Where(id => snapshot.RevisionsById.TryGetValue(id, out var revision) &&
            snapshot.PackagesById.TryGetValue(revision.PackageId, out var package) && package.ParentPackageId is null &&
            (package.SourceKind == WccPackageSourceKinds.WesnothCore || package.SourceKind == WccPackageSourceKinds.WesnothMainline)).ToHashSet();
        foreach (var pin in edit.SourcePins)
        {
            if (edit.Dependency is null || !edit.Dependency.DesiredSpecs.Any(spec => spec.AddonId == pin.AddonId) ||
                !snapshot.RevisionsById.TryGetValue(pin.RevisionId, out var revision) ||
                !snapshot.PackagesById.TryGetValue(revision.PackageId, out var package) ||
                !package.IsAvailable || package.SourceKind != WccPackageSourceKinds.WesnothAddon ||
                package.CurrentRevisionId != revision.Id || string.IsNullOrWhiteSpace(package.SourceLocation) ||
                (package.SourceIdentifier ?? revision.AddonMetadata?.AddonId ?? Path.GetFileName(package.SourceLocation)) != pin.AddonId)
                throw new InvalidOperationException("A newly attached dependency has no exact current indexed provider revision.");
            var providerClosure = WccLibraryRevisionVisibility.GetVisibleRevisionIds(snapshot, revision.Id);
            if (!corePins.IsSubsetOf(providerClosure) || providerClosure.Any(id => snapshot.RevisionsById.TryGetValue(id, out var dep) &&
                    snapshot.PackagesById.TryGetValue(dep.PackageId, out var owner) && owner.ParentPackageId is null &&
                    (owner.SourceKind == WccPackageSourceKinds.WesnothCore || owner.SourceKind == WccPackageSourceKinds.WesnothMainline) && !corePins.Contains(id)))
                throw new InvalidOperationException("The dependency is pinned to a different Core environment.");
        }
        // Freeze a review-only closure. Nothing is published to the live Library before Commit.
        var manifest = JsonSerializer.Deserialize<WccLibraryManifest>(JsonSerializer.Serialize(snapshot.Manifest))!;
        var root = manifest.PackageRevisions.Single(revision => revision.Id == state.RootRevisionId);
        root.DependencyRevisionIds.AddRange(edit.SourcePins.Select(pin => pin.RevisionId).Where(id => !root.DependencyRevisionIds.Contains(id)));
        return WccLibrarySnapshotBuilder.Build(manifest, []);
    }
}
