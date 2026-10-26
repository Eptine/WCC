using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Wcc.App.Preview;
using Wcc.App.ViewModels.Wcc;
using Wcc.Core.Library;
using Wcc.Core.Projects;
using Wcc.Core.References;
using Wcc.Core.Units;
using Wcc.Core.Wml;

namespace Wcc.App.ViewModels;

public partial class AppShellViewModel
{
    private long _unitCopyWizardVersion;
    private UnitTypeCreationWizardSetup WithUnitCopyChoices(UnitTypeCreationWizardSetup setup,
        WccLibrarySnapshot snapshot, WccPackageRevisionId core, IReadOnlyList<WmlStaticSource> sources,
        IReadOnlyList<WmlEvaluationContext> contexts, Func<bool> current, string? addonsPath,
        WccStaticSourceCaptureResult? readyCapture = null)
    {
        var corePackage = snapshot.PackagesById[snapshot.RevisionsById[core].PackageId];
        var visible = setup.Destination == UnitTypeCreationDestination.Library
            ? WccStandaloneLibraryRevisionScope.GetVisibleRevisionIds(snapshot, corePackage.Id, core, addonsPath)
            : WccLibraryProfileRevisionScope.GetVisibleRevisionIds(snapshot, corePackage.Id, core);
        var installPath = WesnothInstallPath;
        CatalogPickerOptionVm UnitOption(WccEntityRecord entity, string id, string sourceLabel, string sourceHint)
        {
            var file = snapshot.SourceFilesById[entity.SourceFileId];
            var package = snapshot.PackagesById[snapshot.RevisionsById[file.PackageRevisionId].PackageId];
            var inherited = CachedIndexedUnitSummary(entity, file);
            return new()
            {
                ValueId = id, DisplayName = WmlDisplayStringNormalizer.Normalize(entity.DisplayName) ?? id,
                EmissionKind = CatalogPickerEmissionKind.RegistryId,
                LibraryEntityId = entity.Id, LibraryPackageId = package.Id,
                LibrarySourceFileId = file.Id, LibraryPackageRevisionId = file.PackageRevisionId,
                LibraryContentHash = file.ContentHash, ResourceSourceLabel = sourceLabel, ResourceSourceHint = sourceHint,
                RaceId = inherited?.EffectiveRace ?? entity.Summary.GetValueOrDefault("race") ?? "",
                PreviewPathResolver = inherited?.PreviewDocument is { } preview
                    ? () => WccUnitPreviewPathResolver.Resolve(file.StoragePath, WccSourceOrigin.WccGenerated,
                        preview, installPath).ResolvedAbsolutePath
                    : WccUnitCatalogPreview.CreateResolver(entity, file, package, installPath)
            };
        }
        var coreOptions = setup.CoreBases.Select(basis =>
            UnitOption(snapshot.EntitiesById[basis.EntityId], basis.Id, "Core", basis.Source.Path)).ToArray();
        var libraryOptions = WccLibraryCatalogProjection.Query(snapshot,
                new() { EntityType = WccEntityTypes.UnitType, VisibleRevisionIds = visible, Take = int.MaxValue }).Items
            .Where(item => item.PackageId is { } packageId && snapshot.PackagesById.TryGetValue(packageId, out var package)
                && package.SourceKind == WccPackageSourceKinds.WccManagedLibrary
                && package.StorageMode == WccPackageStorageMode.ManagedSnapshot && package.IsAvailable
                && !string.IsNullOrWhiteSpace(item.ExportSymbol)
                && snapshot.EntitiesById[item.EntityId].ParentEntityId is null)
            .Select(item => UnitOption(snapshot.EntitiesById[item.EntityId], item.ExportSymbol!,
                "WCC Library", item.SourceRelativePath)).ToArray();
        Task<WccStaticSourceCaptureResult>? coreCapture = readyCapture is null ? null : Task.FromResult(readyCapture);
        async Task<UnitTypeCopyChoice> Capture(CatalogPickerSelection selection)
        {
            if (!current()) throw new InvalidOperationException("The source or editing context changed. Reopen the wizard.");
            var matches = coreOptions.Concat(libraryOptions).Where(option => option.ToSelection() == selection).ToArray();
            if (matches.Length != 1 || matches[0].LibraryEntityId is not { } entity ||
                matches[0].LibraryPackageRevisionId is not { } revision || matches[0].LibrarySourceFileId is not { } fileId)
                throw new ArgumentException("Choose one exact captured Core or WCC Library unit.");
            WmlStaticSource source;
            if (revision == core)
            {
                var path = snapshot.SourceFilesById[fileId].StoragePath;
                var captured = sources.Where(item => path is not null && PathsEqual(item.Path, path)).ToArray();
                if (captured.Length != 1) throw new InvalidOperationException("The exact Core unit source is unavailable. Reopen the wizard.");
                source = captured[0];
            }
            else
            {
                var content = await WccLibraryEntityContentLoader.LoadAsync(snapshot, entity);
                if (!content.MatchesIndexedContentHash || string.IsNullOrWhiteSpace(content.SourceFile.StoragePath))
                    throw new InvalidOperationException("The Library source changed. Refresh the catalog and reopen the wizard.");
                source = new(content.SourceFile.StoragePath, content.SourceFileText);
            }
            if (!current()) throw new InvalidOperationException("The source or editing context changed. Reopen the wizard.");
            var receipt = WccUnitTypeAuthoredCopyPlanner.CaptureSource(snapshot, revision, entity, source);
            if (WccUnitTypeAuthoredCopyPlanner.VerifySourceFreshness(receipt) is { } stale)
                throw new InvalidOperationException(stale);
            if (setup.Destination == UnitTypeCreationDestination.Campaign && !receipt.IsCore)
            {
                // This capture inventories the exact managed package and pinned Core.
                // The planner activates only this selected definition and its proved helpers.
                var managed = await WccStaticSourceCapture.CaptureAsync(snapshot, revision, source.Path,
                    configuredAddonsPath: addonsPath, includeCoreUnits: true);
                if (!current()) throw new InvalidOperationException("The source or editing context changed. Reopen the wizard.");
                // Original campaign helpers need their own pinned source namespace;
                // keeping a picker reference does not activate that campaign in the game.
                var originalHelpers = await WccUnitTypeMaterializationPlanner.CaptureCoreCampaignHelpersAsync(
                    snapshot, receipt, managed, visible);
                if (!current()) throw new InvalidOperationException("The source or editing context changed. Reopen the wizard.");
                var result = await Task.Run(() =>
                {
                    if (contexts.Count == 0)
                        return (Plan: (WccUnitTypeMaterializationPlan?)null, Problem: (string?)"The complete destination evaluation contexts are unavailable.");
                    var plans = contexts.Select(context => (Context: context,
                        Plan: WccUnitTypeMaterializationPlanner.Plan(receipt, managed, context, originalHelpers))).ToArray();
                    var issues = DescribeUnitCopyIssues(plans.Select(item => (item.Context,
                        Issues: item.Plan.IsComplete ? Array.Empty<string>() : item.Plan.Issues.Count == 0
                            ? new[] { "The captured helper and asset closure is incomplete." }
                            : item.Plan.Issues.Select(issue => issue.Diagnostic).ToArray())).ToArray());
                    if (issues.Length > 0) return (Plan: (WccUnitTypeMaterializationPlan?)null, Problem: (string?)string.Join("\n", issues));
                    if (plans.Select(item => item.Plan.Identity).Distinct(StringComparer.Ordinal).Count() != 1)
                        return (Plan: (WccUnitTypeMaterializationPlan?)null,
                            Problem: (string?)"The required Library helpers or assets differ between difficulties. This copy needs one identical plan for every difficulty.");
                    return (Plan: (WccUnitTypeMaterializationPlan?)plans[0].Plan, Problem: (string?)null);
                });
                if (!current()) throw new InvalidOperationException("The source or editing context changed. Reopen the wizard.");
                return new(receipt, result.Problem) { Materialization = result.Plan };
            }
            coreCapture ??= WccStaticSourceCapture.CaptureAsync(snapshot, core, Path.Combine(corePackage.SourceLocation!, "units.cfg"),
                configuredAddonsPath: addonsPath, includeCoreUnits: true);
            var capture = await coreCapture;
            if (!current()) throw new InvalidOperationException("The source or editing context changed. Reopen the wizard.");
            var problem = await Task.Run(() =>
            {
                if (contexts.Count == 0) return "The complete destination evaluation contexts are unavailable.";
                var issues = DescribeUnitCopyIssues(contexts.Select(context =>
                {
                    var plan = WccUnitTypeDependencyPlanner.Plan(receipt, capture, context);
                    return (Context: context, Issues: plan.IsComplete ? Array.Empty<string>() : plan.Issues.Count == 0
                        ? new[] { "The captured dependency closure is incomplete." }
                        : plan.Issues.Select(issue => issue.Diagnostic).ToArray());
                }).ToArray());
                return issues.Length == 0 ? null : string.Join("\n", issues);
            });
            if (!current()) throw new InvalidOperationException("The source or editing context changed. Reopen the wizard.");
            return new(receipt, problem);
        }
        return new(setup.CoreBases, setup.Races, setup.MovementTypes, setup.DestinationLabel, setup.OutputDirectory, setup.Validate)
        {
            Destination = setup.Destination, CopyCoreOptions = coreOptions, CopyLibraryOptions = libraryOptions,
            CaptureCopySource = Capture
        };
    }

    private static string[] DescribeUnitCopyIssues((WmlEvaluationContext Context, string[] Issues)[] contexts) =>
        contexts.SelectMany((item, index) => item.Issues.Select(diagnostic => (Index: index, item.Context, Diagnostic: diagnostic)))
            .GroupBy(item => item.Diagnostic, StringComparer.Ordinal)
            .Select(group => (contexts.Length > 1 && group.Select(item => item.Index).Distinct().Count() == contexts.Length
                    ? "All difficulties"
                    : string.Join(", ", group.Select(item => item.Context.DisplayName).Distinct(StringComparer.Ordinal)))
                + ": " + group.Key).ToArray();

    public async Task<UnitTypeCreationViewModel?> CreateSelectedUnitCopyWizardModelAsync(bool libraryInspector)
    {
        var editor = libraryInspector ? LibraryUnitEditor : UnitEditor;
        var originalPath = editor.CurrentUnitAsset?.SourcePath;
        var originalId = editor.CurrentSelectedUnitTypeId;
        var originalIndex = editor.SelectedUnit is null ? -1 : editor.Units.IndexOf(editor.SelectedUnit);
        var requestVersion = ++_unitCopyWizardVersion;
        var profile = BuildWesnothSourceResourceProfileIdentity(FindActiveWesnothInstallationProfile());
        if (originalPath is null || originalId is null || originalIndex < 0) return null;
        if (!await ConfirmSourceNavigationAsync()) return null;
        if (requestVersion != _unitCopyWizardVersion || editor.CurrentUnitAsset?.SourcePath is not { } currentPath ||
            !PathsEqual(originalPath, currentPath) || originalId != editor.CurrentSelectedUnitTypeId ||
            originalIndex != (editor.SelectedUnit is null ? -1 : editor.Units.IndexOf(editor.SelectedUnit)) ||
            profile != BuildWesnothSourceResourceProfileIdentity(FindActiveWesnothInstallationProfile()))
        { Status = "The selected unit changed. Reopen the copy wizard."; return null; }
        var document = editor.SelectedUnit?.Document;
        var sourcePath = editor.CurrentUnitAsset?.SourcePath;
        var identity = editor.EditVersion;
        if (document is null || sourcePath is null) return null;
        var model = await CreateLibraryUnitTypeWizardModelAsync();
        if (model is null) return null;
        if (requestVersion != _unitCopyWizardVersion || !ReferenceEquals(document, editor.SelectedUnit?.Document) || editor.EditVersion != identity)
        { model.Dispose(); Status = "The selected unit changed. Reopen the copy wizard."; return null; }
        var item = (libraryInspector ? SelectedInspectorUnitCatalogEntry : SelectedUnitCatalogEntry)?.LibraryItem;
        if (LibrarySnapshot is not { } snapshot || item is null || item.PackageId is not { } packageId ||
            item.PackageRevisionId is not { } revisionId || !snapshot.PackagesById.TryGetValue(packageId, out var package)
            || !snapshot.SourceFilesById.TryGetValue(item.SourceFileId, out var file) || file.StoragePath is null
            || !PathsEqual(file.StoragePath, sourcePath) || item.ExportSymbol != document.Unit.Id
            || package.SourceKind != WccPackageSourceKinds.WccManagedLibrary && FindUnitCreationCore(snapshot, revisionId) != revisionId)
        { model.Dispose(); Status = "Editable copies currently support saved Core and WCC Library unit sources. Use Create Unit Type for other starting points."; return null; }
        model.IsCopyCoreMode = package.SourceKind != WccPackageSourceKinds.WccManagedLibrary;
        model.IsCopyLibraryMode = package.SourceKind == WccPackageSourceKinds.WccManagedLibrary;
        var selected = await model.PickCopySourceAsync(picker => Task.FromResult<CatalogPickerSelection?>(
            picker.FilteredOptions.FirstOrDefault(option => option.LibraryEntityId == item.EntityId)?.ToSelection()));
        if (!selected)
        { Status = model.Validation ?? "The selected source is unavailable for copying."; model.Dispose(); return null; }
        // Only the indexed saved source is captured after the ordinary Save/Discard/Cancel choice.
        return model;
    }
}
