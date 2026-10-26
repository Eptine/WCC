using Wcc.Core.Campaigns;
using Wcc.Core.Projects.Creation;
using Wcc.Core.Wml;

namespace Wcc.Core.Scenarios;

/// <summary>Candidate cleanup evidence only. The chapter planner checks remaining references and uses the common transaction.</summary>
public sealed record WccCampaignPresetChapterRemovalPlan(IReadOnlyList<Guid> ReceiptIdsToRemove,
    IReadOnlyList<WmlStaticSource> SourceDeletionCandidates, IReadOnlyList<string> RetainedFiles, string? Diagnostic = null)
{
    public bool AuthorizesPersistence => false;
}

public static partial class WccCampaignLocalPresetStoragePlanner
{
    /// <summary>Adding a gameplay gate must not silently change the fixed contexts of existing package-wide presets.</summary>
    public static string? ValidateChapterAdditionCompatibility(WmlStaticSource main,
        IReadOnlyList<WmlStaticSource> capturedSources, WccPackageAuthoringDocument document,
        IReadOnlyList<WmlCampaignEvaluationPlan> packagePlans, IReadOnlyList<string> prospectiveDifficultyDefines)
    {
        ArgumentNullException.ThrowIfNull(main); ArgumentNullException.ThrowIfNull(capturedSources);
        ArgumentNullException.ThrowIfNull(document); ArgumentNullException.ThrowIfNull(packagePlans);
        ArgumentNullException.ThrowIfNull(prospectiveDifficultyDefines);
        try
        {
            ValidateLifecycleCapture(main, capturedSources, document, packagePlans);
            var content = WccPackageContentAuthoringManifest.Read(document);
            // Unknown extension semantics stay intact; they must not turn independent
            // chapter authoring into an unsupported structural-schema mutation.
            var receipts = content.CanMutate ? content.Presets : [];
            var root = Path.GetDirectoryName(main.Path)!;
            var entries = WccCampaignLocalPresetCatalog.ReadPackageDefinitions(capturedSources, packagePlans[0], receipts, root, packagePlans)
                .Where(entry => entry.Scope.IsPackage).ToArray();
            if (entries.Any(entry => !entry.IsSupported))
                return "An existing package-wide preset cannot be proved compatible with another chapter. Preserve its source and use RAW to resolve its context before adding a chapter.";
            if (receipts.Any(receipt => receipt.Scope.IsPackage && WccCampaignLocalPresetCatalog.TryRole(receipt.Kind, out _) &&
                    !entries.Any(entry => entry.Id == receipt.Id && entry.HasContentReceipt)))
                return "A package-wide preset receipt has no exact captured definition. Resolve that stale content receipt before adding a chapter.";
            if (!entries.Any(entry => HasDifficultyValues(entry.Payload!))) return null;
            var nativeDefines = packagePlans[0].Difficulties.Select(difficulty => difficulty.Define).ToArray();
            if (!prospectiveDifficultyDefines.SequenceEqual(nativeDefines, StringComparer.Ordinal) ||
                packagePlans.Any(plan => !plan.Difficulties.Select(difficulty => difficulty.Define).SequenceEqual(nativeDefines, StringComparer.Ordinal)))
                return "Package-wide presets contain difficulty-specific values. Keep the exact fixed difficulty defines in every chapter, or explicitly move those presets to chapter scope with RAW before adding an incompatible chapter.";
            return null;
        }
        catch (Exception issue) when (issue is InvalidOperationException or ArgumentException)
        { return issue.Message; }
    }

    /// <summary>
    /// Retires known receipts belonging to the exact removed chapter. Only its canonical
    /// fully receipted utility can become a file-deletion candidate; paths in metadata
    /// alone never authorize deletion. The caller still checks all surviving references.
    /// </summary>
    public static WccCampaignPresetChapterRemovalPlan InspectChapterRemovalStorage(WmlStaticSource main,
        IReadOnlyList<WmlStaticSource> capturedSources, WccPackageAuthoringDocument document,
        WccCampaignChapterAuthoringEntry chapter, IReadOnlyList<WmlCampaignEvaluationPlan> packagePlans,
        IReadOnlyList<WmlStaticSource>? survivingSources = null, WccCampaignLocalPresetStorageSnapshot? presetSnapshot = null)
    {
        ArgumentNullException.ThrowIfNull(main); ArgumentNullException.ThrowIfNull(capturedSources);
        ArgumentNullException.ThrowIfNull(document); ArgumentNullException.ThrowIfNull(chapter);
        ArgumentNullException.ThrowIfNull(packagePlans);
        var relative = ChapterStorageRelativePath(chapter.Id);
        if (WccCampaignCreationTokenPolicy.ValidateAddonId(chapter.Id) is { } invalidChapter)
            return new([], [], [], invalidChapter);
        try
        {
            ValidateLifecycleCapture(main, capturedSources, document, packagePlans);
            var root = Path.GetDirectoryName(main.Path)!;
            var path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
            var matches = capturedSources.Where(source => PathEquals(source.Path, path)).ToArray();
            var content = WccPackageContentAuthoringManifest.Read(document);
            if (!content.CanMutate)
                return new([], [], matches.Length == 0 ? [] : [relative], content.Issue);
            var targeted = content.Presets.Where(receipt => WccCampaignLocalPresetCatalog.TryRole(receipt.Kind, out _) &&
                TargetsRemovedChapter(receipt.Scope, chapter)).ToArray();
            var ids = Array.AsReadOnly(targeted.Select(receipt => receipt.Id).ToArray());
            var retained = targeted.Where(receipt => receipt.SourcePath != relative).Select(receipt => receipt.SourcePath)
                .ToHashSet(StringComparer.Ordinal);
            var claimed = content.Presets.Where(receipt => string.Equals(receipt.SourcePath, relative, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (matches.Length == 0 && claimed.Length == 0) return Result();
            if (matches.Length != 1 || !matches[0].IsAuthored || matches[0].Path != path ||
                claimed.Length == 0 || claimed.Any(receipt => receipt.SourcePath != relative ||
                    !WccCampaignLocalPresetCatalog.TryRole(receipt.Kind, out _) || !TargetsRemovedChapter(receipt.Scope, chapter)))
                return Retain("The chapter preset utility has missing, conflicting, shared or unsupported ownership; its source is preserved.");
            var selected = packagePlans.Where(plan => plan.CampaignId == chapter.CampaignId && plan.CampaignDefine == chapter.CampaignDefine).ToArray();
            if (selected.Length != 1) return Retain("The removed chapter has no unique exact preset evaluation plan; its utility is preserved.");
            IReadOnlyList<WccCampaignLocalPresetEntry> definitions;
            try
            {
                var units = WccCampaignLocalPresetCatalog.ReadPackageDefinitions(capturedSources, selected[0], content.Presets, root, packagePlans)
                    .Where(entry => entry.Role == WccCampaignLocalPresetRole.Unit && entry.IsSupported).ToArray();
                definitions = ReadScopedStorage(matches[0], selected[0], content.Presets, root, packagePlans, units);
            }
            catch (Exception issue) when (issue is InvalidOperationException or ArgumentException)
            { return Retain("The chapter preset utility is not completely supported and owned; its source is preserved. " + issue.Message); }
            if (definitions.Count != claimed.Length || definitions.Any(entry => !entry.HasContentReceipt ||
                    !TargetsRemovedChapter(entry.Scope, chapter) || !claimed.Any(receipt => receipt.Id == entry.Id && receipt.Symbol == entry.Name)))
                return Retain("The chapter preset utility contains unreceipted or foreign definitions; its source is preserved.");
            var outsideComments = WmlEditorAnalysis.Build(matches[0].Text).Tokens.Where(token =>
                token.Kind == WmlEditorTokenKind.Comment && !definitions.Any(definition =>
                    token.StartOffset >= definition.DefinitionRange.StartOffset &&
                    token.StartOffset + token.Length <= definition.DefinitionRange.StartOffset + definition.DefinitionRange.Length)).ToArray();
            // Definition hashes do not cover file-level comments. Only the exact
            // generated header is known-owned; preserve any other authored notes.
            if (outsideComments.Length != 1 || matches[0].Text.Substring(outsideComments[0].StartOffset, outsideComments[0].Length) != StorageHeader)
                return Retain("The chapter preset utility contains authored comments outside its receipted definitions; its source is preserved.");
            if (survivingSources is not null && (survivingSources.Count > 200_000 || survivingSources.Any(source => source is null)))
                return Retain("The surviving preset consumer inventory is incomplete; its utility is preserved.");
            var callerSources = (survivingSources ?? capturedSources).Where(source => !PathEquals(source.Path, path)).ToArray();
            foreach (var definition in definitions)
            {
                // Complete capture-backed storage snapshots may distinguish a sibling's
                // same-name definition. Without that evidence, keep the existing cheap
                // conservative physical caller proof; metadata never proves a binding.
                var exactSnapshot = presetSnapshot is not null && PathEquals(presetSnapshot.Source.Path, main.Path) &&
                    presetSnapshot.Source.ContentHash == main.ContentHash && presetSnapshot.Entries.Any(entry =>
                        entry.Id == definition.Id && entry.Scope == definition.Scope && PathEquals(entry.SourcePath, definition.SourcePath) &&
                        entry.DefinitionRange.StartOffset == definition.DefinitionRange.StartOffset &&
                        entry.DefinitionRange.Length == definition.DefinitionRange.Length && entry.SourceHash == definition.SourceHash);
                var callers = exactSnapshot ? FindCallers(definition, presetSnapshot!, callerSources) : FindCallers(definition, callerSources);
                if (callers.Count != 0)
                    return Retain("The chapter preset utility still has unproved surviving consumers; its source is preserved. " + string.Join(", ", callers));
            }
            return new(ids, [matches[0]], Array.AsReadOnly(retained.Order(StringComparer.Ordinal).ToArray()));

            WccCampaignPresetChapterRemovalPlan Result(string? diagnostic = null) => new(ids, [],
                Array.AsReadOnly(retained.Order(StringComparer.Ordinal).ToArray()), diagnostic);
            WccCampaignPresetChapterRemovalPlan Retain(string diagnostic) { retained.Add(relative); return Result(diagnostic); }
        }
        catch (Exception issue) when (issue is InvalidOperationException or ArgumentException)
        {
            // No metadata is retired when even the package capture cannot be bound.
            return new([], [], [relative], issue.Message);
        }
    }

    private static bool TargetsRemovedChapter(WccCampaignPresetScope scope, WccCampaignChapterAuthoringEntry chapter) =>
        !scope.IsPackage && scope.CampaignId == chapter.CampaignId && scope.CampaignDefine == chapter.CampaignDefine &&
        (scope.ManagedChapterId is null || scope.ManagedChapterId == chapter.Id);

    private static bool IsLifecyclePackageSource(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path);
        return !Path.IsPathRooted(relative) && relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }

    private static void ValidateLifecycleCapture(WmlStaticSource main, IReadOnlyList<WmlStaticSource> sources,
        WccPackageAuthoringDocument document, IReadOnlyList<WmlCampaignEvaluationPlan> plans)
    {
        WccPackageAuthoringManifest.Validate(document);
        if (!main.IsAuthored || document.ProjectAddonId != Path.GetFileName(Path.GetDirectoryName(main.Path)) ||
            sources.Count > 200_000 || sources.Any(source => source is null) ||
            sources.Count(source => PathEquals(source.Path, main.Path)) != 1 ||
            !sources.Any(source => PathEquals(source.Path, main.Path) && source.IsAuthored && source.ContentHash == main.ContentHash) ||
            plans.Count is < 1 or > 32 || plans.Any(plan => plan is null || plan.CampaignMainSourcePath != main.Path || plan.CampaignMainTextHash != main.ContentHash) ||
            plans.Select(plan => plan.CampaignId).Distinct(StringComparer.Ordinal).Count() != plans.Count ||
            plans.Select(plan => plan.CampaignDefine).Distinct(StringComparer.Ordinal).Count() != plans.Count)
            throw new InvalidOperationException("Preset chapter lifecycle needs the exact captured package root and campaign plans.");
    }
}
