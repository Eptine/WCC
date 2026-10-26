using System.Text;
using Wcc.Core.Campaigns;
using Wcc.Core.Library;
using Wcc.Core.Projects;
using Wcc.Core.Wml;

namespace Wcc.Core.Scenarios;

internal static partial class WccScenarioFieldStaticReviewer
{
    private sealed record PackageRawPhase(string Identity, string Label, WmlEvaluationContext Context,
        WmlCampaignEvaluationPlan Plan);

    private static async Task<WccProjectPreparedReview> ReviewPackageRawProjectAsync(
        IReadOnlyList<WccScenarioFieldEditSessionState> states, IReadOnlyList<WccProjectSourceEditRequest> requests,
        CancellationToken cancellationToken, WccSourceSaveDiagnosticCollector? diagnostics)
    {
        var primary = states[0];
        foreach (var state in states)
            if (CheckPackageRawScope(state) is { } refusal) throw new InvalidOperationException(refusal);
        var intent = primary.EntryReview!;
        if (requests.Any(request => !IsPackageRawOnly(request)))
            throw new InvalidOperationException("Whole-file RAW review accepts one pristine-file proposal per source; ordinary recipes use their shared collected review.");
        var capture = await CapturePackageRawAsync(primary, [], cancellationToken, diagnostics).ConfigureAwait(false);
        foreach (var state in states)
            if (CheckCapture(state, capture) is { } refusal) throw new InvalidOperationException(refusal);
        var catalog = RequirePackageRawCatalog(capture, primary, cancellationToken);
        var selected = catalog.Entries.Single(entry => entry.Identity == intent.SelectedEntryIdentity);
        if (!WmlStaticSingleplayerEnvironment.TryCreate(capture.CoreVersion, out var environment))
            throw new InvalidOperationException("The pinned Core cannot provide the exact singleplayer environment.");
        var menuContext = catalog.Menu.Context;
        var phases = new[] { new PackageRawPhase("menu", "Campaign menu", menuContext, selected.Plan!) }
            .Concat(selected.Plan!.Contexts.Select(context => new PackageRawPhase(
                selected.Identity + "\u001f" + context.SemanticKey, selected.Name + ": " + context.DisplayName, context, selected.Plan!))).ToArray();
        diagnostics?.Add(WccSourceSaveCount.ReviewContexts, phases.Length);
        var baselines = new Dictionary<string, PairedEvaluation>(StringComparer.Ordinal);
        foreach (var phase in phases)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var baseline = EvaluateWithDiagnostics(capture, phase.Context, phase.Plan, environment!, null, cancellationToken, diagnostics);
            RequireComplete(baseline, phase.Context, phase.Label);
            baselines.Add(phase.Identity, ScopeSelectedCampaignMenu(baseline, selected.Owner, null));
        }
        var selectedOwners = new Dictionary<string, WmlStaticScalarOwner>(PathComparer);
        foreach (var state in states)
        {
            if (state.ReviewScope == WccScenarioFieldReviewScope.CampaignMenu && state.Context.SemanticKey != menuContext.SemanticKey)
                throw new InvalidOperationException("The RAW campaign session does not use this exact menu context.");
            var phase = state.ReviewScope == WccScenarioFieldReviewScope.CampaignMenu ? phases[0]
                : phases.SingleOrDefault(phase => phase.Identity == selected.Identity + "\u001f" + state.Context.SemanticKey)
                    ?? throw new InvalidOperationException("The RAW source session is outside the selected entry's exact difficulty scope.");
            var owner = FindOwner(baselines[phase.Identity].Projection, state.Owner, null)
                ?? throw new InvalidOperationException("The RAW source owner cannot be rebound to its unique captured occurrence.");
            if (state.ReviewScope == WccScenarioFieldReviewScope.CampaignMenu
                && !ReferenceEquals(owner, FindOwner(baselines[phase.Identity].Projection, selected.Owner, null)))
                throw new InvalidOperationException("The RAW menu session does not select this exact physical campaign entry.");
            RequirePackageRawLiteralOwner(owner, capture);
            if (WccScenarioFieldContentScope.CheckOwner(owner) is { } contentRefusal)
                throw new InvalidOperationException(contentRefusal);
            if (!PathComparer.Equals(state.Source.Path, intent.CampaignMainSource.Path)
                && (owner.OpeningOrigin is not { } opening || !WccScenarioFieldRouteAnalyzer.SameSource(opening.Source, state.Source)))
                throw new InvalidOperationException("RAW editing is limited to the package main or the selected owner's physical source file.");
            selectedOwners.Add(state.Source.Path, owner);
        }
        var files = requests.Select(ComposePackageRawFile).ToArray();
        var change = new WccScenarioFieldSourceChange(files.Select(file => file.Candidate));
        var candidateCapture = await WccStaticSourceCapture.CaptureAsync(primary.Snapshot, primary.RootRevisionId,
            intent.CampaignMainSource.Path, files.Select(file => file.Candidate.CandidateSource).ToArray(), cancellationToken,
            primary.ConfiguredAddonsPath).ConfigureAwait(false);
        if (!candidateCapture.IsCaptureComplete)
            throw new InvalidOperationException("The proposed RAW package source closure is incomplete: " + string.Join(" ", candidateCapture.Diagnostics));
        var proposedCatalog = WccCampaignEntryCatalog.Project(candidateCapture, primary.Plan, cancellationToken);
        RequireUnchangedSelectedRawTopology(selected, proposedCatalog, change);
        var identity = new StringBuilder();
        var catalogIdentity = SelectedRawCatalogIdentity(catalog, selected);
        var closure = ClosureIdentity(capture);
        Add(identity, catalogIdentity); Add(identity, closure);
        foreach (var state in states) Add(identity, ScopeIdentity(state));
        foreach (var phase in phases)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var before = baselines[phase.Identity];
            var after = EvaluateWithDiagnostics(candidateCapture, phase.Context, phase.Plan, environment!, null, cancellationToken, diagnostics);
            RequireComplete(after, phase.Context, phase.Label + " proposed RAW source");
            after = ScopeSelectedCampaignMenu(after, selected.Owner, change);
            // Translation directives are stateful across output regions. Do not mask a domain change
            // and thereby mistake unchanged spelling in a later file for unchanged translated text.
            if (!PackageRawTextDomains(before.Evaluation).SequenceEqual(PackageRawTextDomains(after.Evaluation), StringComparer.Ordinal))
                throw new InvalidOperationException("Package RAW editing cannot change the expanded translation-domain sequence in this slice.");
            var oldMasked = MaskProjectRegionsWithDiagnostics(before, files.SelectMany(file => file.Regions).Select(region => region.Before).ToArray(), diagnostics);
            var newMasked = MaskProjectRegionsWithDiagnostics(after, files.SelectMany(file => file.Regions).Select(region => region.After).ToArray(), diagnostics);
            RequireComplete(oldMasked, phase.Context, phase.Label + " outside RAW source");
            RequireComplete(newMasked, phase.Context, phase.Label + " proposed outside RAW source");
            CompareOwners(oldMasked.Projection, newMasked.Projection, [], change,
                oldMasked.Evaluation.Text, newMasked.Evaluation.Text, out var ownerRefusal);
            if (ownerRefusal is not null) throw new InvalidOperationException(phase.Label + ": " + ownerRefusal);
            if (CheckProjectFieldHistories(oldMasked, newMasked, [], change) is { } historyRefusal)
                throw new InvalidOperationException(phase.Label + ": " + historyRefusal);
            if (HasUntrackedOutputChange(oldMasked, newMasked))
                throw new InvalidOperationException(phase.Label + ": The RAW proposal changes expanded output outside its exact authored footprint.");
            Add(identity, phase.Identity); Add(identity, phase.Context.SemanticKey);
            Add(identity, WccContentHash.ComputeText(before.Evaluation.Text)); Add(identity, WccContentHash.ComputeText(after.Evaluation.Text));
        }
        var finalCapture = await CapturePackageRawAsync(primary, [], cancellationToken, diagnostics).ConfigureAwait(false);
        foreach (var state in states)
            if (CheckCapture(state, finalCapture) is { } refusal) throw new InvalidOperationException(refusal);
        var finalCatalog = RequirePackageRawCatalog(finalCapture, primary, cancellationToken);
        if (ClosureIdentity(finalCapture) != closure || SelectedRawCatalogIdentity(finalCatalog,
                finalCatalog.Entries.Single(entry => entry.Identity == intent.SelectedEntryIdentity)) != catalogIdentity)
            throw new InvalidOperationException("The package source closure or campaign-entry scope changed during RAW review.");
        foreach (var file in files) { Add(identity, file.Candidate.Source.Path); Add(identity, file.Candidate.CandidateSource.ContentHash); }
        return new(new WccProjectSourceEditCandidate(files.Select(file => new WccProjectSourceFileCandidate(
                file.Candidate.Source, file.Candidate.CandidateSource, file.Candidate.Patches))),
            Array.AsReadOnly(files), closure, Array.AsReadOnly(states.Select(ScopeIdentity).ToArray()))
        { PackageRawCatalogIdentity = catalogIdentity, PackageRawReviewIdentity = WccContentHash.ComputeText(identity.ToString()) };
    }

    private static async Task<string?> VerifyPackageRawProjectPreparedAsync(IReadOnlyList<WccScenarioFieldEditSessionState> states,
        WccProjectPreparedReview proof, IReadOnlyList<WccTextDiskRevision> ownedStages,
        IReadOnlyList<WccBinaryDiskRevision> binaryStages, CancellationToken cancellationToken, WccSourceSaveDiagnosticCollector? diagnostics)
    {
        if (binaryStages.Count != 0 || proof.Candidate.SourceCreates.Count != 0 || proof.Candidate.BinaryCreates.Count != 0
            || proof.Candidate.BinaryReuses.Count != 0 || proof.PackageRawCatalogIdentity is null
            || !states.Select(ScopeIdentity).SequenceEqual(proof.ScopeIdentities, StringComparer.Ordinal))
            return "The package RAW source/session scope changed after review.";
        foreach (var state in states) if (CheckPackageRawScope(state) is { } refusal) return refusal;
        var capture = await CapturePackageRawAsync(states[0], ownedStages, cancellationToken, diagnostics).ConfigureAwait(false);
        foreach (var state in states) if (CheckCapture(state, capture) is { } refusal) return refusal;
        if (ClosureIdentity(capture) != proof.ClosureIdentity) return "The pinned package closure or namespace changed after RAW review.";
        var catalog = RequirePackageRawCatalog(capture, states[0], cancellationToken);
        return SelectedRawCatalogIdentity(catalog, catalog.Entries.Single(entry => entry.Identity == states[0].EntryReview!.SelectedEntryIdentity)) == proof.PackageRawCatalogIdentity ? null
            : "The complete campaign-entry scope changed after RAW review.";
    }

    private static async Task<WccStaticSourceCaptureResult> CapturePackageRawAsync(WccScenarioFieldEditSessionState state,
        IReadOnlyList<WccTextDiskRevision> stages, CancellationToken cancellationToken, WccSourceSaveDiagnosticCollector? diagnostics)
    {
        diagnostics?.Add(WccSourceSaveCount.SourceCaptures);
        var intent = state.EntryReview ?? throw new InvalidOperationException("No package RAW review intent was published.");
        WccStaticSourceCaptureResult capture;
        using (diagnostics?.Measure(WccSourceSavePhase.SourceCapture))
            capture = stages.Count == 0
                ? await WccStaticSourceCapture.CaptureAsync(state.Snapshot, state.RootRevisionId, intent.CampaignMainSource.Path,
                    cancellationToken: cancellationToken, configuredAddonsPath: state.ConfiguredAddonsPath).ConfigureAwait(false)
                : await WccStaticSourceCapture.CaptureForStagedCommitAsync(state.Snapshot, state.RootRevisionId,
                    intent.CampaignMainSource.Path, stages, cancellationToken, state.ConfiguredAddonsPath).ConfigureAwait(false);
        diagnostics?.ObserveCapture(capture); return capture;
    }

    private static string? CheckPackageRawScope(WccScenarioFieldEditSessionState state)
    {
        if (!state.IsWritable) return "The current project RAW source is read-only.";
        if (state.EntryReview is not { } intent || state.Plan is not { } plan || !Enum.IsDefined(state.ReviewScope)
            || state.ReviewScope == WccScenarioFieldReviewScope.CampaignMenu && state.Owner.TagName != "campaign"
            || state.ReviewScope == WccScenarioFieldReviewScope.Scenario && state.Owner.TagName != "scenario")
            return "Package RAW editing requires an explicit campaign/scenario owner and complete entry review intent.";
        if (!state.Source.IsAuthored || !intent.CampaignMainSource.IsAuthored || !Contains(state.ProjectRootPath, state.Source.Path)
            || !Contains(state.ProjectRootPath, intent.CampaignMainSource.Path)
            || !PathComparer.Equals(plan.CampaignMainSourcePath, intent.CampaignMainSource.Path)
            || plan.CampaignMainTextHash != intent.CampaignMainSource.ContentHash)
            return "The RAW source and selected plan do not share one exact authored package entrypoint.";
        if (!state.Snapshot.RevisionsById.TryGetValue(state.RootRevisionId, out var revision)
            || !state.Snapshot.PackagesById.TryGetValue(revision.PackageId, out var package)
            || package.SourceKind != WccPackageSourceKinds.WccProject || package.StorageMode != WccPackageStorageMode.WorkingCopy
            || package.SourceLocation is null || !PathComparer.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(package.SourceLocation)), state.ProjectRootPath))
            return "The indexed RAW source root is not the current writable project.";
        if (state.ReviewScope == WccScenarioFieldReviewScope.CampaignMenu
            ? state.Context.DefinedSymbols.Count != 0
            : !plan.Contexts.Any(context => context.SemanticKey == state.Context.SemanticKey))
            return "The RAW source session is outside the selected ordinary menu/play context.";
        foreach (var source in new[] { state.Source, intent.CampaignMainSource })
            if (state.Snapshot.SourceFilesById.Values.Count(file => file.PackageRevisionId == state.RootRevisionId
                    && file.StoragePath is { } path && PathComparer.Equals(path, source.Path)
                    && string.Equals(file.ContentHash, source.ContentHash, StringComparison.OrdinalIgnoreCase)) != 1)
                return "A package RAW source has no exact unique indexed revision.";
        return null;
    }

    private static WccCampaignEntryCatalogProjection RequirePackageRawCatalog(WccStaticSourceCaptureResult capture,
        WccScenarioFieldEditSessionState state, CancellationToken cancellationToken)
    {
        var intent = state.EntryReview!;
        if (!capture.IsCaptureComplete || capture.EntrySource is not { } root
            || !WccScenarioFieldRouteAnalyzer.SameSource(root, intent.CampaignMainSource) || root.Text != intent.CampaignMainSource.Text)
            throw new InvalidOperationException("The package RAW intent no longer matches the complete captured entrypoint.");
        var selected = WccSelectedCampaignEntryAdapter.Create(capture, intent.SelectedEntryIdentity, state.Plan, cancellationToken);
        if (!Contains(state.ProjectRootPath, selected.OwnerSource.Path))
            throw new InvalidOperationException("The selected menu is outside this writable project.");
        return selected.Catalog;
    }

    private static void RequirePackageRawLiteralOwner(WmlStaticScalarOwner owner, WccStaticSourceCaptureResult capture)
    {
        if (owner.OpeningOrigin is not { Source.IsAuthored: true } opening || opening.Text != "[" + owner.TagName + "]"
            || !capture.Sources.Any(source => WccScenarioFieldRouteAnalyzer.SameSource(source, opening.Source)))
            throw new InvalidOperationException("The RAW owner has no exact captured physical opening.");
        var structure = WmlAuthoredStructureScanner.Scan(opening.Source.Text);
        if (structure.Problem is not null || opening.StartOffset < 0 || opening.StartOffset > structure.MaskedText.Length - opening.Length
            || structure.MaskedText.Substring(opening.StartOffset, opening.Length) != opening.Text)
            throw new InvalidOperationException("A generated macro-definition owner is not an authored RAW file selection.");
    }

    private static bool IsPackageRawOnly(WccProjectSourceEditRequest request) => request.RawPatches.Count == 1
        && request.RawPatches[0] is { Range.StartOffset: 0 } patch && patch.Range.Length == request.Source.Text.Length
        && WccScenarioFieldRouteAnalyzer.SameSource(patch.Range.Source, request.Source)
        && patch.ReplacementText is not null && patch.ReplacementText.Length <= 8 * 1024 * 1024
        && request.Fields.Count == 0 && request.InitialSettings is null && request.CampaignStructure is null
        && request.CampaignCredits is null && request.DifficultyFields.Count == 0 && request.SideTopology is null
        && request.LeaderAdditions.Count == 0 && request.PresetStorage is null
        && request.ScenarioCreation is null && request.UnitTypeCreation is null && request.UnitTypeUpdate is null
        && request.StoryEdit is null && request.UnitInstances.Count == 0 && request.PackageEdit is null;

    private static WccProjectComposedFile ComposePackageRawFile(WccProjectSourceEditRequest request)
    {
        var before = request.Source.Text; var after = request.RawPatches[0].ReplacementText;
        if (before == after) return WccProjectSourceEditComposer.Compose(request);
        var hunks = PackageRawChangedLines(before, after);
        foreach (var proposed in new[] { false, true })
        {
            var text = proposed ? after : before;
            var structure = WmlAuthoredStructureScanner.Scan(text);
            var analysis = WmlEditorAnalysis.Build(text);
            if (structure.Problem is not null || analysis.Tokens.Any(token => token.Kind == WmlEditorTokenKind.Directive
                    && hunks.Any(hunk => token.StartOffset < (proposed ? hunk.AfterStart + hunk.AfterLength : hunk.BeforeStart + hunk.BeforeLength)
                        && token.StartOffset + token.Length > (proposed ? hunk.AfterStart : hunk.BeforeStart))))
                throw new InvalidOperationException("RAW changes crossing preprocessor directives require a separate state/context-topology contract.");
        }
        // Keep untouched tags and invocations between edits in the outside-output proof. A single
        // first-to-last bounding patch can otherwise hide only one end of an expanded nested owner.
        var composed = WccProjectSourceEditComposer.Compose(new(request.Source,
            rawPatches: hunks.Select(hunk => new WccAuthoredSourcePatch(
                new(request.Source, hunk.BeforeStart, hunk.BeforeLength), after.Substring(hunk.AfterStart, hunk.AfterLength)))));
        if (composed.Candidate.CandidateSource.Text != after)
            throw new InvalidOperationException("The bounded RAW proposal does not reconstruct the exact requested pristine-file candidate.");
        return composed;
    }

    private static IEnumerable<string> PackageRawTextDomains(WmlStaticEvaluationResult evaluation)
    {
        var structure = WmlAuthoredStructureScanner.Scan(evaluation.Text);
        if (structure.Problem is not null) throw new InvalidOperationException("The expanded RAW source has no complete lexical directive inventory.");
        return structure.Directives.Where(directive => directive.Name == "textdomain").Select(directive =>
        {
            var line = structure.Lines[directive.Line];
            return evaluation.Text[line.StartOffset..line.ContentEndOffset].Trim();
        }).ToArray();
    }
}
