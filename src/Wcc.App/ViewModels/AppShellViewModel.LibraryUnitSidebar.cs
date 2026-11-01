using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Text.RegularExpressions;
using Wcc.Core.Library;
using Wcc.Core.Projects;
using Wcc.Core.References;
using Wcc.Core.Wml;

namespace Wcc.App.ViewModels;

public partial class AppShellViewModel
{
    // Only detached summaries survive this background job. Captured sources,
    // projections and effective-owner graphs are released after each batch.
    private LibraryUnitSidebarPreparation? _libraryUnitSidebarPreparation;
    private static readonly Regex LibraryUnitLocalDirective = new(@"^[ \t]*#(define|undef)\b",
        RegexOptions.Multiline | RegexOptions.CultureInvariant);

    private string LibraryUnitSidebarProfileIdentity()
    {
        var profile = FindActiveWesnothInstallationProfile();
        return BuildWesnothSourceResourceProfileIdentity(profile) + "\u001f" + profile?.AddonsPath;
    }

    private sealed class LibraryUnitSidebarPreparation(WccLibrarySnapshot snapshot, long epoch,
        string profileIdentity, IEnumerable<WccPackageRevisionId> revisions, SynchronizationContext? owner)
    {
        public WccLibrarySnapshot Snapshot { get; } = snapshot;
        public long Epoch { get; } = epoch;
        public string ProfileIdentity { get; } = profileIdentity;
        public HashSet<WccPackageRevisionId> Revisions { get; } = revisions.ToHashSet();
        public SynchronizationContext? Owner { get; } = owner;
        public UnitDisplayWorkLifetime Lifetime { get; } = new();
    }

    private sealed record LibraryUnitSidebarFile(UnitDisplayBinding Binding,
        WccSourceFileRecord Source, WccEntityRecord[] Entities, UnitDisplayLookup[] Lookups);

    private void QueueLibraryUnitSidebarPreparation()
    {
        if (LibrarySnapshot is not { } snapshot) return;
        if (!ReferenceEquals(snapshot, _unitDisplaySnapshot)) ResetUnitDisplayScopes();
        LibraryUnitSidebarPreparation work;
        LibraryUnitSidebarFile[] files;
        lock (_unitDisplaySync)
        {
            var profile = LibraryUnitSidebarProfileIdentity();
            if (_libraryUnitSidebarPreparation is { } existing && !existing.Lifetime.IsRetired &&
                ReferenceEquals(existing.Snapshot, snapshot) && existing.Epoch == _unitDisplayEpoch &&
                existing.ProfileIdentity == profile && existing.Revisions.SetEquals(_activeLibraryRevisionIds)) return;
            RetireLibraryUnitSidebarPreparation();
            var candidates = snapshot.EntitiesById.Values.Where(entity => entity.EntityType == WccEntityTypes.UnitType &&
                snapshot.SourceFilesById.TryGetValue(entity.SourceFileId, out var source) &&
                _activeLibraryRevisionIds.Contains(source.PackageRevisionId) && source.StoragePath is not null &&
                source.ContentHash is not null && entity.SourceSpan?.StartOffset is not null &&
                entity.SourceSpan.EndOffsetExclusive is not null &&
                snapshot.PackagesById.TryGetValue(snapshot.RevisionsById[source.PackageRevisionId].PackageId, out var package) &&
                package.SourceKind != WccPackageSourceKinds.WccProject &&
                package.SourceKind != WccPackageSourceKinds.WccManagedLibrary);
            files = candidates.GroupBy(entity => entity.SourceFileId).Select(group =>
            {
                var source = snapshot.SourceFilesById[group.Key];
                var binding = CreateUnitDisplayBinding(snapshot, source.PackageRevisionId, source.StoragePath!, false);
                return binding is null ? null : new LibraryUnitSidebarFile(binding, source, group.ToArray(),
                    group.Select(entity => new UnitDisplayLookup(source.StoragePath!, source.ContentHash!,
                        entity.SourceSpan!.StartOffset!.Value,
                        entity.SourceSpan.EndOffsetExclusive!.Value - entity.SourceSpan.StartOffset.Value)).Distinct().ToArray());
            }).OfType<LibraryUnitSidebarFile>().ToArray();
            if (files.Length == 0) return;
            work = new(snapshot, _unitDisplayEpoch, profile, _activeLibraryRevisionIds, SynchronizationContext.Current);
            _libraryUnitSidebarPreparation = work;
            TrackUnitDisplayWorker(Task.Run(() => PrepareLibraryUnitSidebarAsync(work, files)));
        }
    }

    private void RetireLibraryUnitSidebarPreparation()
    {
        lock (_unitDisplaySync)
        {
            if (_libraryUnitSidebarPreparation is not { } work) return;
            _libraryUnitSidebarPreparation = null;
            work.Lifetime.Dispose();
            TrackUnitDisplayWorker(work.Lifetime.CancellationCompletion);
        }
    }

    private bool IsCurrentLibraryUnitSidebarPreparation(LibraryUnitSidebarPreparation work) =>
        !work.Lifetime.IsRetired && ReferenceEquals(work, _libraryUnitSidebarPreparation) &&
        ReferenceEquals(work.Snapshot, LibrarySnapshot) && work.Epoch == _unitDisplayEpoch &&
        work.Revisions.SetEquals(_activeLibraryRevisionIds) &&
        work.ProfileIdentity == LibraryUnitSidebarProfileIdentity();

    private async Task PrepareLibraryUnitSidebarAsync(LibraryUnitSidebarPreparation work, LibraryUnitSidebarFile[] files)
    {
        var token = work.Lifetime.Token;
        try
        {
            foreach (var package in files.GroupBy(file => file.Binding.Revision))
            {
                token.ThrowIfCancellationRequested();
                lock (_unitDisplaySync) if (!IsCurrentLibraryUnitSidebarPreparation(work)) return;
                var pending = package.Where(NeedsPreparation).ToArray();
                if (pending.Length == 0) continue;
                WccStaticSourceCaptureResult capture;
                await _unitDisplayCaptureGate.WaitAsync(token).ConfigureAwait(false);
                try
                {
                    var first = pending[0].Binding;
                    capture = await WccStaticSourceCapture.CaptureAsync(work.Snapshot, first.Revision, first.EntryPath,
                        cancellationToken: token, configuredAddonsPath: first.AddonsPath, includeCoreUnits: true).ConfigureAwait(false);
                }
                catch (Exception failure) when (failure is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
                {
                    continue;
                }
                finally { _unitDisplayCaptureGate.Release(); }
                var cache = new WccUnitSidebarSummaryCache(Path.Combine(
                    Path.GetDirectoryName(Path.GetFullPath(_unitCatalogLibraryRoot))!, "Cache", "unit-sidebar-v1"));
                var firstBinding = pending[0].Binding;
                var cacheRequest = WccUnitSidebarSummaryCache.CreateRequest(capture,
                    pending.SelectMany(file => file.Lookups).Distinct().Select(lookup =>
                        new WccUnitSidebarLookup(lookup.Path, lookup.Hash, lookup.Start, lookup.Length)).ToArray(),
                    firstBinding.Context.TargetCompatibility ?? "unspecified", work.ProfileIdentity,
                    firstBinding.Revision.Value + "\u001f" + firstBinding.Context.SemanticKey + "\u001fcore=" + firstBinding.Core,
                    typeof(AppShellViewModel).Module.ModuleVersionId + "\u001f" + typeof(WccStaticSourceCapture).Module.ModuleVersionId, token);
                if (cacheRequest is not null && await cache.TryLoadAsync(cacheRequest, token).ConfigureAwait(false) is { } cached)
                {
                    var entries = cached.ToDictionary(entry => new UnitDisplayLookup(entry.Lookup.Path,
                        entry.Lookup.ContentHash, entry.Lookup.Start, entry.Lookup.Length));
                    var groups = new Dictionary<string, UnitDisplayGrouping>(StringComparer.Ordinal);
                    foreach (var filesByKey in pending.GroupBy(file => file.Binding.Key))
                    {
                        var binding = filesByKey.First().Binding;
                        groups.Add(binding.Key, new(binding, filesByKey.SelectMany(file => file.Lookups).Distinct().ToDictionary(
                            lookup => lookup, lookup => entries[lookup] is { Available: true } entry
                                ? new UnitDisplaySummary(entry.EffectiveRace, null, entry.PreviewPath, entry.RequiresCapturedImages) : null)));
                    }
                    // Cache reuse can cover a large add-on. Keep owner-thread
                    // publications as small as the ordinary preparation batches.
                    foreach (var chunk in groups.Chunk(WccLibraryUnitPreviewPlan.MaximumBatchFiles))
                        await PublishAsync(chunk.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal)).ConfigureAwait(false);
                    continue;
                }
                var summaries = new Dictionary<string, UnitDisplayGrouping>(StringComparer.Ordinal);
                var packageSummaries = new Dictionary<string, UnitDisplayGrouping>(StringComparer.Ordinal);
                var paths = capture.Sources.ToLookup(source => source.Path,
                    OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
                bool HasLocalDirectives(LibraryUnitSidebarFile file) =>
                    paths[file.Binding.EntryPath].Any(source => LibraryUnitLocalDirective.IsMatch(source.Text));
                // Prepare independent files first. Definitions local to a unit
                // need an isolated environment, but share this same capture.
                var batches = pending[0].Binding.Core ? new[] { pending } : pending.Where(file => !HasLocalDirectives(file))
                    .Chunk(WccLibraryUnitPreviewPlan.MaximumBatchFiles)
                    .Concat(pending.Where(HasLocalDirectives).Select(file => new[] { file }));
                foreach (var batch in batches)
                {
                    await PrepareBatchAsync(capture, batch, summaries).ConfigureAwait(false);
                    foreach (var (key, grouping) in summaries) packageSummaries[key] = grouping;
                    await PublishAsync(summaries).ConfigureAwait(false);
                    summaries.Clear();
                }
                if (cacheRequest is not null)
                {
                    lock (_unitDisplaySync) if (!IsCurrentLibraryUnitSidebarPreparation(work)) return;
                    var entries = packageSummaries.Values.SelectMany(grouping => grouping.Values).Select(pair =>
                        new WccUnitSidebarCachedSummary(new(pair.Key.Path, pair.Key.Hash, pair.Key.Start, pair.Key.Length),
                            pair.Value is not null, pair.Value?.EffectiveRace, pair.Value?.PreviewPath,
                            pair.Value?.RequiresCapturedImages ?? false)).ToArray();
                    await cache.TrySaveAsync(cacheRequest, entries, token).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            // Unsupported/unavailable Library content remains inspectable. This
            // job never turns a failed display preparation into edit authority.
        }
        finally { work.Lifetime.Complete(); }

        bool NeedsPreparation(LibraryUnitSidebarFile file)
        {
            token.ThrowIfCancellationRequested();
            lock (_unitDisplaySync)
                if (_unitDisplaySummaries.ContainsKey(file.Binding.Key)) return false;
            foreach (var entity in file.Entities)
            {
                if (entity.Summary.ContainsKey("base_unit") ||
                    !entity.Summary.TryGetValue("race", out var race) || string.IsNullOrWhiteSpace(race) ||
                    race.IndexOfAny(['{', '}', '$']) >= 0) return true;
                var package = work.Snapshot.FindPackageForEntity(entity.Id);
                if (CreateIndexedUnitPreviewResolver(entity, file.Source, package)?.Invoke() is null) return true;
            }
            return false;
        }

        async Task PrepareBatchAsync(WccStaticSourceCaptureResult capture, LibraryUnitSidebarFile[] batch,
            Dictionary<string, UnitDisplayGrouping> results)
        {
            token.ThrowIfCancellationRequested();
            UnitDisplayScope? scope = null;
            await _unitDisplayCaptureGate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                try
                {
                    if (capture.IsCaptureComplete && capture.HasCompleteCoreUnitPrelude)
                        scope = CreateCapturedUnitDisplayScope(batch[0].Binding, capture,
                            batch.Length > 1 && !batch[0].Binding.Core
                                ? batch.Select(file => file.Binding.EntryPath).ToArray() : null, token);
                }
                catch (Exception failure) when (failure is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
                {
                    scope = null;
                }
                if (batch[0].Binding.Core && (scope?.Evaluation.IsComplete != true ||
                    !scope.UnitRegistry.HasCompleteDeclaredInventory))
                {
                    scope?.Retire();
                    scope = null;
                }
                if (!batch[0].Binding.Core && batch.Length > 1 && (scope?.Evaluation.IsComplete != true ||
                    !scope.UnitRegistry.HasCompleteDeclaredInventory || !CanShareLibraryUnitBatch(scope, batch)))
                {
                    scope?.Retire();
                    scope = null;
                }
                else
                {
                    var grouped = new Dictionary<string, (UnitDisplayBinding Binding,
                        Dictionary<UnitDisplayLookup, UnitDisplaySummary?> Values)>(StringComparer.Ordinal);
                    foreach (var file in batch)
                    {
                        if (!grouped.TryGetValue(file.Binding.Key, out var grouping))
                        {
                            grouping = (file.Binding, new());
                            grouped.Add(file.Binding.Key, grouping);
                        }
                        var values = grouping.Values;
                        foreach (var lookup in file.Lookups)
                        {
                            token.ThrowIfCancellationRequested();
                            var display = scope?.FindScalarPreview(lookup.Path, lookup.Hash, lookup.Start, lookup.Length, token);
                            var preview = display?.PreviewDocument;
                            string? path = null;
                            var capturedImages = file.Binding.Core || scope?.RequiresCapturedImages == true;
                            if (preview is not null && capturedImages)
                            {
                                foreach (var kind in new[] { WccAssetReferenceKind.Image, WccAssetReferenceKind.Portrait })
                                {
                                    var candidate = WccUnitPreviewPathResolver.ResolveKind(null,
                                        WccSourceOrigin.ImportedExternal, preview, kind);
                                    if (!candidate.HasReference || candidate.IsSuppressed || candidate.RawExpression is null) continue;
                                    var resolution = scope.ImageResources.Resolve(candidate.RawExpression, candidate.SourceLabel);
                                    if (resolution.IsResolved) { path = resolution.ResolvedAbsolutePath; break; }
                                }
                            }
                            values[lookup] = preview is null ? null : new(display!.EffectiveRace,
                                capturedImages ? null : preview, path, capturedImages);
                        }
                    }
                    foreach (var (key, grouping) in grouped)
                        results[key] = new(grouping.Binding, grouping.Values);
                    return;
                }
            }
            finally
            {
                scope?.Retire();
                _unitDisplayCaptureGate.Release();
            }
            // A broken sibling must not hide valid units. There are at most
            // 2*N-1 attempts, all using the same immutable package capture.
            var middle = batch.Length / 2;
            await PrepareBatchAsync(capture, batch[..middle], results).ConfigureAwait(false);
            await PrepareBatchAsync(capture, batch[middle..], results).ConfigureAwait(false);
        }

        Task PublishAsync(Dictionary<string, UnitDisplayGrouping> summaries)
        {
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            void Publish()
            {
                try
                {
                    lock (_unitDisplaySync)
                    {
                        if (!IsCurrentLibraryUnitSidebarPreparation(work)) return;
                        var changed = false;
                        foreach (var (key, grouping) in summaries)
                        {
                            if (!_activeLibraryRevisionIds.Contains(grouping.Binding.Revision) ||
                                CreateUnitDisplayBinding(work.Snapshot, grouping.Binding.Revision,
                                    grouping.Binding.EntryPath, false)?.Key != key || _unitDisplaySummaries.ContainsKey(key)) continue;
                            _unitDisplaySummaries.Add(key, grouping);
                            _unitDisplayGroupingRequests.Add(key);
                            _unitDisplayPublications.Add(key);
                            changed = true;
                        }
                        if (changed) ApplyLibraryUnitSidebarSummaries(summaries.Values.SelectMany(grouping => grouping.Values.Keys)
                            .Select(lookup => lookup.Path));
                    }
                }
                catch (Exception failure) { completion.TrySetException(failure); }
                finally { completion.TrySetResult(); }
            }
            if (work.Owner is null || ReferenceEquals(work.Owner, SynchronizationContext.Current)) Publish();
            else work.Owner.Post(_ => Publish(), null);
            return completion.Task.WaitAsync(token);
        }
    }

    private static bool CanShareLibraryUnitBatch(UnitDisplayScope scope, LibraryUnitSidebarFile[] batch)
    {
        // Batching must retain the single-file preview's macro environment. A
        // unit-local define/undef can affect a later sibling. Conservatively
        // split those batches, including directives inside invoked helpers.
        // This is only a sharing veto; the evaluator still interprets all WML.
        var selected = batch.Select(file => file.Binding.EntryPath).ToHashSet(
            OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        var reached = false;
        foreach (var source in scope.Evaluation.Sources)
        {
            reached |= selected.Contains(source.Path);
            if (reached && LibraryUnitLocalDirective.IsMatch(source.Text)) return false;
        }
        foreach (var definition in scope.Evaluation.Invocations.Where(invocation =>
                     invocation.Outcome == WmlStaticInvocationOutcome.Expanded)
                 .Select(invocation => invocation.Definition).OfType<WmlStaticSourceRange>().Distinct())
        {
            var directives = LibraryUnitLocalDirective.Matches(definition.Text);
            if (directives.Count > 1 || directives.Any(match => match.Groups[1].Value == "undef")) return false;
        }
        var includedDefinitions = new HashSet<WmlStaticSource>();
        foreach (var source in scope.Evaluation.Includes.SelectMany(include => include.Resolution.Sources))
            if (LibraryUnitLocalDirective.IsMatch(source.Text) && !includedDefinitions.Add(source)) return false;
        return true;
    }
}
