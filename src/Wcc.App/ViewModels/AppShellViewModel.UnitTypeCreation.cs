using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Wcc.App.ViewModels.Wcc;
using Wcc.Core.Library;
using Wcc.Core.Compatibility;
using Wcc.Core.Projects;
using Wcc.Core.Units;
using Wcc.Core.Wml;

namespace Wcc.App.ViewModels;

public partial class AppShellViewModel
{
    private bool _isUnitTypeCreationBusy;
    private Task<bool>? _unitTypeCreationTask;
    public bool IsUnitTypeCreationBusy => _isUnitTypeCreationBusy;
    public bool CanCreateProjectUnitType => _unitTypeCreationTask is not { IsCompleted: false } && CanInteractWithEditors &&
        IsProjectUnitWorkspace && ProjectSession.CanWrite && CampaignEditor.CanCreateCampaignUnitType;
    public bool CanCreateLibraryUnitType => _unitTypeCreationTask is not { IsCompleted: false } && CanInteractWithEditors &&
        _activeWesnothInstallation is { } installation && WccCompatibilityPolicy.CanCreateFor(installation.CompatibilityTarget) &&
        LibrarySnapshot is { } snapshot && TryFindActiveCoreRevision(snapshot, out _, out _);
    public string UnitTypeCreationGuidance => ProjectSession.CanWrite
        ? CampaignEditor.CampaignUnitTypeCreationProblem ?? "Create a unit type from scratch, inherit Core values, or copy authored Core and WCC Library definitions."
        : "Open a writable WCC-created campaign to create a project unit type.";

    public UnitTypeCreationViewModel? CreateProjectUnitTypeWizardModel()
    {
        if (!CanCreateProjectUnitType || CampaignEditor.CaptureCampaignUnitTypeCreationBinding() is not { } binding) return null;
        var core = FindUnitCreationCore(binding.LibrarySnapshot, binding.RootRevision);
        if (core is null) { Status = "The campaign needs its exact available pinned Core revision."; return null; }
        var project = CampaignEditor.CurrentProject; var session = ProjectSession; var installation = _activeWesnothInstallation;
        var profile = ResolvePinnedProjectSourceResourceProfile();
        var profileIdentity = BuildWesnothSourceResourceProfileIdentity(profile);
        bool SameContext() => ReferenceEquals(project, CampaignEditor.CurrentProject) && session == ProjectSession &&
            ReferenceEquals(installation, _activeWesnothInstallation) && binding.IsSameContext() &&
            profileIdentity == BuildWesnothSourceResourceProfileIdentity(ResolvePinnedProjectSourceResourceProfile());
        bool Current() => SameContext() && binding.IsCurrent() && CanInteractWithEditors;
        string? Validate(WccUnitTypeCreationSpec spec)
        {
            try
            {
                _ = WccUnitTypeCreationPlanner.CreateEdit(binding.Snapshot, spec);
                return UnitCreationCollision(binding.LibrarySnapshot,
                    WccLibraryRevisionVisibility.GetVisibleRevisionIds(binding.LibrarySnapshot, binding.RootRevision), spec,
                    Path.Combine(session.RootPath!, "units"));
            }
            catch (Exception refusal) when (refusal is ArgumentException or InvalidOperationException) { return refusal.Message; }
        }
        var setup = CreateUnitCreationSetup(binding.LibrarySnapshot, core.Value, binding.Sources,
            "Current campaign", Path.Combine(session.RootPath!, "units"), Validate);
        return new(WithUnitCopyChoices(setup, binding.LibrarySnapshot, core.Value, binding.Sources,
            binding.Contexts, Current, profile?.AddonsPath), Current, SameContext, this);
    }

    public async Task<UnitTypeCreationViewModel?> CreateLibraryUnitTypeWizardModelAsync(CancellationToken cancellationToken = default)
    {
        if (!CanCreateLibraryUnitType || LibrarySnapshot is not { } snapshot ||
            !TryFindActiveCoreRevision(snapshot, out var package, out var revision) || package.SourceLocation is null) return null;
        var installation = _activeWesnothInstallation;
        var profile = FindActiveWesnothInstallationProfile();
        var profileIdentity = BuildWesnothSourceResourceProfileIdentity(profile);
        bool SameContext() => ReferenceEquals(installation, _activeWesnothInstallation) &&
            LibrarySnapshot is { } current && TryFindActiveCoreRevision(current, out _, out var selected) && selected.Id == revision.Id &&
            profileIdentity == BuildWesnothSourceResourceProfileIdentity(FindActiveWesnothInstallationProfile());
        bool Current() => SameContext() && ReferenceEquals(snapshot, LibrarySnapshot) && CanInteractWithEditors;
        try
        {
            var capture = await WccStaticSourceCapture.CaptureAsync(snapshot, revision.Id,
                Path.Combine(package.SourceLocation, "units.cfg"), cancellationToken: cancellationToken,
                configuredAddonsPath: profile?.AddonsPath, includeCoreUnits: true);
            if (!Current() || !capture.IsCaptureComplete || !capture.HasCompleteCoreUnitPrelude)
            { Status = "Wait for complete data from the selected Core installation, then reopen the unit type wizard."; return null; }
            var directory = WccManagedLibraryFileStore.GetKindDirectory(_unitCatalogLibraryRoot, WccManagedLibraryFileKind.UnitType);
            string? Validate(WccUnitTypeCreationSpec spec) => UnitCreationCollision(snapshot, _activeLibraryRevisionIds, spec, directory);
            var setup = CreateUnitCreationSetup(snapshot, revision.Id, capture.Sources, "WCC Library", directory, Validate);
            return new(WithUnitCopyChoices(setup, snapshot, revision.Id, capture.Sources, [new WmlEvaluationContext()], Current,
                profile?.AddonsPath, capture), Current, SameContext, this);
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        { Status = "Could not capture Core choices: " + failure.Message; return null; }
    }

    private static WccPackageRevisionId? FindUnitCreationCore(WccLibrarySnapshot snapshot, WccPackageRevisionId root)
    {
        var matches = WccLibraryRevisionVisibility.GetVisibleRevisionIds(snapshot, root).Where(id => snapshot.RevisionsById.TryGetValue(id, out var revision)
            && snapshot.PackagesById.TryGetValue(revision.PackageId, out var package)
            && package.ParentPackageId is null && package.StorageMode == WccPackageStorageMode.ExternalReadOnly && package.IsAvailable
            && (package.SourceKind == WccPackageSourceKinds.WesnothCore && package.ContentKind is var kind
                && (kind == WccPackageContentKinds.Core || kind == WccPackageContentKinds.ResourcePack)
                || package.SourceKind == WccPackageSourceKinds.WesnothMainline && package.ContentKind == WccPackageContentKinds.ResourcePack))
            .Take(2).ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }

    private static UnitTypeCreationWizardSetup CreateUnitCreationSetup(WccLibrarySnapshot snapshot, WccPackageRevisionId core,
        IReadOnlyList<WmlStaticSource> capturedSources, string destination, string directory, Func<WccUnitTypeCreationSpec, string?> validate)
    {
        var bases = new List<WccUnitTypeCreationBase>();
        foreach (var entity in snapshot.FindEntities(WccEntityTypes.UnitType).Where(entity => entity.ParentEntityId is null))
        {
            if (!snapshot.SourceFilesById.TryGetValue(entity.SourceFileId, out var file) || file.PackageRevisionId != core || file.StoragePath is null) continue;
            var sources = capturedSources.Where(source => PathsEqual(source.Path, file.StoragePath)).ToArray();
            if (sources.Length != 1) continue;
            try { bases.Add(WccUnitTypeCreationPlanner.CaptureCoreBase(snapshot, core, entity.Id, sources[0])); }
            catch (ArgumentException) { /* Unsupported or changed source remains available through ordinary Library inspection. */ }
        }
        CatalogPickerOptionVm[] Options(WccEntityTypeId type) => WccLibraryCatalogProjection.Query(snapshot,
            new() { EntityType = type, VisibleRevisionIds = new HashSet<WccPackageRevisionId> { core }, Take = int.MaxValue }).Items
            .Where(item => !string.IsNullOrWhiteSpace(item.ExportSymbol)).Select(item => new CatalogPickerOptionVm
            {
                ValueId = item.ExportSymbol!, DisplayName = item.DisplayName, LibraryEntityId = item.EntityId,
                LibraryPackageId = item.PackageId, LibrarySourceFileId = item.SourceFileId, LibraryPackageRevisionId = item.PackageRevisionId,
                LibraryContentHash = snapshot.SourceFilesById[item.SourceFileId].ContentHash,
                EmissionKind = CatalogPickerEmissionKind.RegistryId, ResourceSourceLabel = "Core", ResourceSourceHint = item.SourceRelativePath
            }).ToArray();
        return new(bases.OrderBy(basis => basis.Id, StringComparer.Ordinal).ToArray(), Options(WccEntityTypes.Race),
            Options(WccEntityTypes.MovementType), destination, directory, validate)
            { Destination = destination == "WCC Library" ? UnitTypeCreationDestination.Library : UnitTypeCreationDestination.Campaign };
    }

    private static string? UnitCreationCollision(WccLibrarySnapshot snapshot, IReadOnlySet<WccPackageRevisionId> visible,
        WccUnitTypeCreationSpec spec, string directory)
    {
        WccUnitTypeCreationPlanner.ValidateSpec(spec);
        if (snapshot.FindEntities(WccEntityTypes.UnitType).Any(entity => UnitCreationIdMatches(entity.ExportSymbol, spec.Id) &&
            snapshot.SourceFilesById.TryGetValue(entity.SourceFileId, out var file) && visible.Contains(file.PackageRevisionId)))
            return "This unit type ID already exists. Choose a new ID.";
        if (File.Exists(Path.Combine(directory, (spec.FileNameStem ?? spec.Id) + ".cfg")) ||
            Directory.Exists(Path.Combine(directory, (spec.FileNameStem ?? spec.Id) + ".cfg")))
            return "The destination filename already exists. Choose a different ID or filename.";
        return null;
    }

    private static bool UnitCreationIdMatches(string? authoredId, string requestedId) =>
        WmlResourceValueTextDecoder.TryDecode(authoredId, out var literalId, out _) && literalId == requestedId;

    public Task<bool> CreateProjectUnitTypeAsync(UnitTypeCreationRequest request, CancellationToken cancellationToken = default) =>
        StartUnitTypeCreation(request, library: false, cancellationToken);
    public Task<bool> CreateLibraryUnitTypeAsync(UnitTypeCreationRequest request, CancellationToken cancellationToken = default) =>
        StartUnitTypeCreation(request, library: true, cancellationToken);
    private Task<bool> StartUnitTypeCreation(UnitTypeCreationRequest request, bool library, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Destination != (library ? UnitTypeCreationDestination.Library : UnitTypeCreationDestination.Campaign) ||
            library && request.Spec.Mode == WccUnitTypeCreationMode.MaterializeLibrary ||
            (library ? !CanCreateLibraryUnitType : !CanCreateProjectUnitType) || !request.IsCurrent())
        { Status = "The source or context changed. Reopen the unit type wizard; existing drafts remain."; return Task.FromResult(false); }
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _unitTypeCreationTask = completion.Task;
        _ = CompleteUnitTypeCreationAsync(request, library, completion, cancellationToken);
        return completion.Task;
    }
    private async Task CompleteUnitTypeCreationAsync(UnitTypeCreationRequest request, bool library,
        TaskCompletionSource<bool> completion, CancellationToken cancellationToken)
    {
        var succeeded = false;
        var project = CampaignEditor.CurrentProject; var session = ProjectSession; var installation = _activeWesnothInstallation;
        try
        {
            if (!request.IsCurrent() || !await ConfirmSourceNavigationAsync(cancellationToken)) return;
            await CampaignEditor.WaitForCampaignFieldProjectionAsync(cancellationToken);
            await ScenarioEditor.WaitForScenarioUiLoadingAsync(cancellationToken);
            bool SameContext() => ReferenceEquals(installation, _activeWesnothInstallation) && request.IsSameContext() &&
                (library || ReferenceEquals(project, CampaignEditor.CurrentProject) && session == ProjectSession);
            if (!SameContext() || HasPendingEditorChanges || !CanInteractWithEditors)
            { Status = "The editing context changed. Reopen the unit type wizard; existing drafts remain."; return; }
            _isUnitTypeCreationBusy = true; OnPropertyChanged(nameof(IsUnitTypeCreationBusy)); RaiseShellPresentation();
            if (library)
            {
                if (LibrarySnapshot is not { } snapshot || !TryFindActiveCoreRevision(snapshot, out var package, out var core) || package.SourceLocation is null) return;
                Status = "Reading captured Core definitions...";
                var capture = await WccStaticSourceCapture.CaptureAsync(snapshot, core.Id, Path.Combine(package.SourceLocation, "units.cfg"),
                    cancellationToken: cancellationToken, configuredAddonsPath: FindActiveWesnothInstallationProfile()?.AddonsPath, includeCoreUnits: true);
                var spec = request.Spec;
                if (spec.Base is { } basis) _ = WccUnitTypeCreationPlanner.CaptureCoreBase(snapshot, core.Id, basis.EntityId, basis.Source);
                if (spec.Copy is { } copy) WccUnitTypeAuthoredCopyPlanner.ValidateSourceEvidence(snapshot, copy);
                var text = WccUnitTypeCreationPlanner.Render(spec, "wesnoth-wcc-library");
                var runtimeEntry = new WmlStaticSource("wcc-static://unit-creation/library.cfg", "[units]\n" + text + "[/units]\n", false);
                Status = "Checking the new unit type against the captured Core registry...";
                await Task.Run(() => WccUnitTypeCreationRuntime.Validate(spec, capture, new WmlEvaluationContext(), expectCreatedType: true,
                    entry: runtimeEntry, cancellationToken: cancellationToken), cancellationToken);
                Status = "Verifying Core source freshness...";
                var final = await WccStaticSourceCapture.CaptureAsync(snapshot, core.Id, Path.Combine(package.SourceLocation, "units.cfg"),
                    cancellationToken: cancellationToken, configuredAddonsPath: FindActiveWesnothInstallationProfile()?.AddonsPath, includeCoreUnits: true);
                if (!SameContext() || !ReferenceEquals(snapshot, LibrarySnapshot) || HasPendingEditorChanges || !final.IsCaptureComplete ||
                    !capture.Sources.Select(source => (source.Path, source.ContentHash)).SequenceEqual(final.Sources.Select(source => (source.Path, source.ContentHash))))
                { Status = "The library or Core sources changed. Reopen the unit type wizard."; return; }
                if (spec.Copy is not null)
                    await Task.Run(() => WccUnitTypeCreationRuntime.Validate(spec, final, new WmlEvaluationContext(),
                        expectCreatedType: true, entry: runtimeEntry, cancellationToken: cancellationToken), cancellationToken);
                var directory = WccManagedLibraryFileStore.GetKindDirectory(_unitCatalogLibraryRoot, WccManagedLibraryFileKind.UnitType);
                if (UnitCreationCollision(snapshot, _activeLibraryRevisionIds, spec, directory) is { } collision)
                { Status = collision; return; }
                cancellationToken.ThrowIfCancellationRequested();
                Status = "Creating the WCC Library definition...";
                if (!SameContext() || HasPendingEditorChanges || !ReferenceEquals(snapshot, LibrarySnapshot)) return;
                if (spec.Copy is { } finalCopy && WccUnitTypeAuthoredCopyPlanner.VerifySourceFreshness(finalCopy) is { } staleCopy)
                { Status = staleCopy; return; }
                var result = WccManagedLibraryFileStore.Create(_unitCatalogLibraryRoot, WccManagedLibraryFileKind.UnitType,
                    text, spec.FileNameStem ?? spec.Id);
                if (!result.IsSuccess || result.SourcePath is null)
                { Status = FormatManagedLibraryMutationFailure(result, "create the WCC Library unit type"); return; }
                succeeded = true;
                if (!SameContext() || HasPendingEditorChanges)
                { Status = "Unit type saved at " + result.SourcePath + ". Open it after finishing current edits."; return; }
                var asset = new UnitFileAsset { FileName = Path.GetFileName(result.SourcePath), SourcePath = result.SourcePath, RawTextSnapshot = text };
                asset.Documents.AddRange(UnitTypeImporter.ImportAllFromText(text));
                var managed = CreateManagedUnitAsset(asset, result.SourcePath);
                _pendingManagedInspectorUnitSourcePathSelection = result.SourcePath;
                LibraryUnitEditor.LoadFromExistingUnitAsset(managed);
                LibraryUnitSession = new(WccEditorSessionScope.Library, WccEditorAccessMode.Writable,
                    Path.GetDirectoryName(result.SourcePath), "WCC Library unit: " + spec.Id, _unitCatalogLibraryRoot);
                ClearLibraryUnitDirty(); RaiseLibraryUnitEditorStateChanged();
                await RefreshOwnedLibraryPackageIndexAsync();
                Status = "Created WCC Library unit type: " + spec.Id;
            }
            else
            {
                Status = "Staging the new campaign unit type...";
                if (!SameContext() || !CampaignEditor.StageCampaignUnitTypeCreation(request.Spec))
                { Status = CampaignEditor.Status; return; }
                var path = Path.Combine(session.RootPath!, "units", (request.Spec.FileNameStem ?? request.Spec.Id) + ".cfg");
                if (!SameContext() || !await SavePendingEditorChangesAsync(cancellationToken)) return;
                succeeded = true;
                if (!SameContext() || HasPendingEditorChanges)
                { Status = "Unit type saved at " + path + ". Open it after finishing current edits."; return; }
                Status = "Opening the saved campaign unit type...";
                RefreshProjectUnitWorkspaceEntries();
                var entry = ProjectOwnedUnitEntries.FirstOrDefault(entry => UnitCreationIdMatches(entry.UnitTypeId, request.Spec.Id) &&
                    entry.OwnedAsset?.SourcePath is { } sourcePath && PathsEqual(sourcePath, path));
                if (entry is null) { Status = "Unit type saved at " + path + ". Open its file to continue."; return; }
                SelectedProjectUnitEntry = entry; LoadOwnedProjectUnitDocument(entry); SelectedTabIndex = 2;
                Status = "Created project unit type: " + request.Spec.Id;
            }
        }
        catch (OperationCanceledException) { Status = "Unit type creation cancelled. Any staged changes remain in global Save."; }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        { Status = "Could not finish unit type creation: " + failure.Message + ". Any staged changes remain in global Save."; }
        finally
        {
            _isUnitTypeCreationBusy = false; OnPropertyChanged(nameof(IsUnitTypeCreationBusy));
            completion.TrySetResult(succeeded); RaiseShellPresentation();
        }
    }
}
