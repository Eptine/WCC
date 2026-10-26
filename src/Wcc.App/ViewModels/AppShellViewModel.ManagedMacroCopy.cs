using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Wcc.Core.Library;
using Wcc.Core.Wml;

namespace Wcc.App.ViewModels;

internal sealed record ManagedMacroDuplicateCapture(WccLibrarySnapshot Snapshot,
    WccManagedMacroCopySource Source, WccStaticSourceCaptureResult Capture, Func<bool> IsCurrent);

public partial class AppShellViewModel
{
    private bool _managedMacroDuplicateBusy;

    internal async Task<ManagedMacroDuplicateCapture?> CaptureManagedMacroDuplicateAsync(WccEntityId entityId,
        string expectedRawText, string expectedName, Func<bool> selectedIsCurrent)
    {
        if (LibrarySnapshot is not { } snapshot || !selectedIsCurrent() || !CanInteractWithEditors) return null;
        if (!snapshot.EntitiesById.TryGetValue(entityId, out var selected) || !IsVisibleInActiveLibrary(selected))
        {
            Status = "This macro is not available in the standalone Library source scope. Campaign-only sources remain available in the campaign.";
            return null;
        }
        var installation = _activeWesnothInstallation;
        var project = CampaignEditor.CurrentProject;
        var session = ProjectSession;
        var metadataEpoch = _projectMetadataLoadGeneration;
        var profileIdentity = ManagedMacroCopyProfileIdentity();
        bool Current() => selectedIsCurrent() && ReferenceEquals(snapshot, LibrarySnapshot)
            && ReferenceEquals(installation, _activeWesnothInstallation) && ReferenceEquals(project, CampaignEditor.CurrentProject)
            && session == ProjectSession && metadataEpoch == _projectMetadataLoadGeneration
            && profileIdentity == ManagedMacroCopyProfileIdentity() && CanInteractWithEditors;
        try
        {
            var loaded = await WccLibraryEntityContentLoader.LoadAsync(snapshot, entityId);
            if (!Current() || !loaded.MatchesIndexedContentHash || loaded.EntityRawText != expectedRawText)
                throw new InvalidOperationException("The selected macro source changed. Refresh its package and reopen Clone to WCC.");
            var source = WccManagedMacroCopyPlanner.CaptureSource(snapshot, entityId,
                new WmlStaticSource(loaded.SourceFile.StoragePath!, loaded.SourceFileText));
            if (source.Name != expectedName || source.Definition.Text != expectedRawText)
                throw new InvalidOperationException("The selected macro no longer matches its exact indexed definition. Reopen Clone to WCC.");
            var capture = await WccStaticSourceCapture.CaptureAsync(snapshot, source.RevisionId, source.Source.Path,
                configuredAddonsPath: FindActiveWesnothInstallationProfile()?.AddonsPath, includeCoreUnits: true);
            if (!Current()) throw new InvalidOperationException("The selected source or editing context changed. Reopen Clone to WCC.");
            if (!capture.IsCaptureComplete || !capture.HasCompleteCoreUnitPrelude)
                throw new InvalidOperationException("The selected macro needs a complete captured source and pinned Core before it can be copied.");
            return new(snapshot, source, capture, Current);
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or NotSupportedException)
        { Status = "Could not capture the macro copy: " + failure.Message; return null; }
    }

    internal async Task SaveManagedMacroDuplicateAsync(ManagedMacroDuplicateCapture captured,
        WccManagedLibraryFileKind kind, string rawText, Func<bool> draftIsCurrent)
    {
        if (_managedMacroDuplicateBusy) throw new InvalidOperationException("Another macro copy is still being saved. Keep this draft and retry when it finishes.");
        var ownerContext = SynchronizationContext.Current;
        bool ReadCurrent() => captured.IsCurrent() && draftIsCurrent();
        bool Current()
        {
            if (ownerContext is null || ReferenceEquals(ownerContext, SynchronizationContext.Current)) return ReadCurrent();
            var current = false;
            // Staged and final guards may run on a worker. The complete live
            // selection/context/draft predicate belongs to the opening UI context.
            ownerContext.Send(_ => current = ReadCurrent(), null);
            return current;
        }
        if (!Current()) throw new InvalidOperationException("The selected macro source, editing context or copy draft changed. Reopen Clone to WCC; the draft remains available.");
        _managedMacroDuplicateBusy = true;
        try
        {
            Status = "Checking the macro's helpers and resources...";
            var plan = await Task.Run(() => WccManagedMacroCopyPlanner.Plan(captured.Source, captured.Capture,
                _unitCatalogLibraryRoot, kind, rawText));
            if (!Current()) throw new InvalidOperationException("The selected source or copy draft changed while checking its dependencies. Reopen Clone to WCC.");
            if (!plan.IsComplete) throw new InvalidOperationException(string.Join("\n", plan.Issues));
            var coordinator = new WccManagedMacroCopyCoordinator();
            var prepared = await coordinator.PrepareAsync(captured.Snapshot, plan, Current);
            if (!prepared.IsPrepared) throw new InvalidOperationException(prepared.Diagnostic);
            Status = "Copying the macro, shared helpers and owned resources into the WCC Library...";
            var saved = await coordinator.CommitAsync(prepared.Ticket!, Current);
            if (!saved.IsSuccess) throw new InvalidOperationException(saved.Diagnostic);
            Status = "Created the macro and its required helpers and resources in the WCC Library; refreshing the package index.";
            await RefreshOwnedLibraryPackageIndexAsync();
            Status = "Created the macro and its required helpers and resources in the WCC Library.";
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or NotSupportedException)
        {
            Status = "Could not copy the macro: " + failure.Message;
            // The existing asynchronous RAW host retains its draft when the save callback refuses.
            throw new InvalidOperationException(failure.Message, failure);
        }
        finally { _managedMacroDuplicateBusy = false; }
    }

    private string ManagedMacroCopyProfileIdentity()
    {
        var profile = FindActiveWesnothInstallationProfile();
        return string.Join("\u001f", BuildWesnothSourceResourceProfileIdentity(profile), profile?.InstallPath,
            profile?.AddonsPath, profile?.CompatibilityTarget, profile?.LastDetectedInstallation?.CoreDataRoot,
            profile?.LastDetectedInstallation?.CampaignsRoot, profile?.LastDetectedInstallation?.ExecutablePath);
    }
}
