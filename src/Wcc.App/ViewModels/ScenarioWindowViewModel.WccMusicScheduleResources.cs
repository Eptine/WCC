using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Wcc.App.ViewModels.Wcc;
using Wcc.Core.Library;
using Wcc.Core.References;
using Wcc.Core.Projects;
using Wcc.Core.Scenarios;
using Wcc.Core.Wml;

namespace Wcc.App.ViewModels;

public partial class ScenarioWindowViewModel
{
    private readonly HashSet<string> _importedScenarioMusic = new(StringComparer.Ordinal);
    private string? _importedScenarioMusicRoot;
    private sealed record MusicScheduleDefinitionsDisplayKey(WccLibrarySnapshot? Snapshot,
        IReadOnlySet<WccPackageRevisionId>? VisibleRevisions, StaticRootRevisionSelection? Root,
        WmlCampaignEvaluationPlan? Plan, WccEditorSessionContext Host, ScenarioDocument? Document,
        string? SourcePath, string? FullSource, string? OriginalSource, string Raw, string Context,
        string? InstallPath, string? AddonsPath, string Profile, string AdditionalNames);
    private sealed record MusicScheduleDefinitionsDisplayCache(MusicScheduleDefinitionsDisplayKey Key,
        IReadOnlyList<WmlMacroDefinition> LocalDefinitions, IReadOnlyList<WmlMacroExpansionDefinition> Definitions)
    {
        public RawEditorReferenceSnapshot? CapturedReferences { get; init; }
        public WmlStaticScalarProjection? CapturedProjection { get; init; }
        public IReadOnlyList<WmlMacroDefinition>? SourceDefinitions { get; init; }
    }
    private MusicScheduleDefinitionsDisplayCache? _musicScheduleDefinitionsCache;
    private sealed record ScheduleImagePreviewContext(WccLibrarySnapshot? Snapshot,
        StaticRootRevisionSelection? Root, string? SourcePath, string? SourceText,
        CapturedScheduleDisplayContext? Captured);
    private ScheduleImagePreviewContext? _scheduleImagePreviewContext;

    public IReadOnlyList<WmlMacroExpansionDefinition> GetWccMusicScheduleDefinitions(
        IEnumerable<string>? additionalMacroNames = null, bool fresh = false)
    {
        lock (_wccInitialFieldRefreshGate)
            return GetWccMusicScheduleDefinitionsCore(additionalMacroNames, fresh);
    }

    private IReadOnlyList<WmlMacroExpansionDefinition> GetWccMusicScheduleDefinitionsCore(
        IEnumerable<string>? additionalMacroNames, bool fresh)
    {
        var raw = GetWccScenarioSourceRawWml() ?? "";
        var capturedProjection = CurrentMusicScheduleAuthoringProjection;
        var capturedReferences = capturedProjection is not null && _rawEditorReferenceSnapshot is { } references &&
            references.Generation == _staticInitialValuesGeneration && references.ContextKey == _evaluationContext.SemanticKey &&
            PathsEqual(references.Source.Path, _currentSourcePath ?? LoadedPath) && references.Source.Text == _pristineLoadedSourceText
                ? references : null;
        // Publication temporarily revokes the live preview. Keep the original
        // draft's physical definition choices, while every fresh read below
        // still verifies indexed bytes and the unchanged source/session binding.
        if (capturedReferences is null && _musicScheduleSourceBinding is
                { DefinitionProjection: { } originalProjection, DefinitionReferences: { } originalReferences } binding &&
            GetWccMusicScheduleBindingProblem(binding) is null)
        {
            capturedProjection = originalProjection;
            capturedReferences = originalReferences;
        }
        IReadOnlyList<WmlMacroDefinition>? sourceDefinitions = null;
        if (capturedReferences is not null)
        {
            sourceDefinitions = _musicScheduleDefinitionsCache is { SourceDefinitions: { } scanned } original &&
                original.Key.OriginalSource == capturedReferences.Source.Text
                    ? scanned : WmlMacroDefinitionScanner.Scan(capturedReferences.Source.Text);
            // Local display definitions can change before an authored source is
            // published. Their new bodies must not be replaced by old indexed
            // definitions or presented as part of the captured evaluation.
            if (!_currentSourceMacroDefinitions.SequenceEqual(sourceDefinitions))
            {
                capturedProjection = null;
                capturedReferences = null;
            }
        }
        var additional = (additionalMacroNames ?? []).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        // This cache holds display definitions, never a checked source binding.
        // Selection/hydration generations are not definition inputs. Fresh
        // picker, draft and Save checks still reread and verify indexed bytes.
        var key = new MusicScheduleDefinitionsDisplayKey(_macroLibrarySnapshot, _macroVisibleRevisionIds,
            _staticRootRevisionSelection, _sourceEvaluationPlan, _sourceHostSession, SelectedScenario?.Document,
            _currentSourcePath ?? LoadedPath, _pristineLoadedSourceText, SelectedScenario?.Document.OriginalText,
            raw, _evaluationContext.SemanticKey, _wesnothSourceInstallPath, _wesnothSourceAddonsPath,
            _wesnothSourceProfileIdentity, string.Join("\n", additional));
        if (!fresh && _musicScheduleDefinitionsCache is { } cached && cached.Key == key &&
            ReferenceEquals(cached.CapturedReferences, capturedReferences) &&
            ReferenceEquals(cached.CapturedProjection, capturedProjection) &&
            cached.LocalDefinitions.SequenceEqual(_currentSourceMacroDefinitions))
            return cached.Definitions;
        var names = ScenarioContentSourceInventory.Build(raw)
            .Where(entry => entry.Kind == ScenarioContentSourceKind.MacroInvocation).Select(entry => entry.Name)
            .Concat(new[] { "SCENARIO_MUSIC", "INTRO_AND_SCENARIO_MUSIC", "EXTRA_SCENARIO_MUSIC", "LET_INTRO_MUSIC_FINISH" })
            .Concat(additional).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var localDefinitions = BuildCurrentSourceMacroExpansionDefinitions();
        var resolver = _macroLibrarySnapshot is null ? null : new WccLibraryMacroExpansionDefinitionResolver(
            _macroLibrarySnapshot, ScenarioContentVisibleRevisions, _currentSourcePath ?? LoadedPath);
        var result = new List<WmlMacroExpansionDefinition>();
        var observedDefinitions = capturedReferences?.Evidence.Macros.GroupBy(use => use.Name, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Select(use => use.Definition)
                .DistinctBy(WmlEditorReferenceEvidence.RangeIdentity).Take(2).ToArray(), StringComparer.Ordinal);
        var observedResolvers = new Dictionary<string, WccLibraryMacroExpansionDefinitionResolver>(OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        var queue = new Queue<string>(names);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var checkedFiles = new Dictionary<string, (bool Matches, string? Text)>(OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        while (queue.TryDequeue(out var name) && seen.Count < 2048)
        {
            if (!seen.Add(name)) continue;
            var local = localDefinitions.Where(definition => definition.Name == name).ToArray();
            var matches = local.Length > 0 ? local : resolver?.FindDefinitions(name) ?? [];
            // A reached invocation proves its physical binding, not every later
            // use of the same symbol. Conflicting observed declarations stay
            // ambiguous; unused suggestions retain the ordinary recipe checks.
            if (observedDefinitions?.GetValueOrDefault(name) is { Length: > 1 }) matches = [];
            else if (_macroLibrarySnapshot is { } observedSnapshot &&
                observedDefinitions?.GetValueOrDefault(name) is { Length: 1 } observed)
            {
                var active = observed[0];
                if (!observedResolvers.TryGetValue(active.Source.Path, out var observedResolver))
                    observedResolvers[active.Source.Path] = observedResolver = new(observedSnapshot,
                        ScenarioContentVisibleRevisions, active.Source.Path);
                matches = observedResolver.FindDefinitions(name)
                    .Where(definition => MatchesObservedScheduleDefinition(definition, active)).ToArray();
                if (matches.Count != 1) matches = [];
            }
            foreach (var definition in matches)
            {
                string? capturedText;
                if (!PathsEqual(definition.SourcePath, _currentSourcePath ?? LoadedPath))
                {
                    if (!DefinitionFileMatchesIndex(definition, checkedFiles, out capturedText)) continue;
                }
                else capturedText = SelectedScenario?.Document.OriginalText;
                var captured = definition with { TextDomain = capturedText is not null && definition.SourceStartLine is { } start
                    ? WmlTextDomainCapture.BeforeLine(capturedText, start) : null };
                result.Add(captured);
                foreach (var invocation in WmlMacroInvocationSyntaxScanner.Scan(definition.RawText))
                    if (!seen.Contains(invocation.Name)) queue.Enqueue(invocation.Name);
            }
        }
        _musicScheduleDefinitionsCache = new(key, _currentSourceMacroDefinitions.ToArray(),
            Array.AsReadOnly(result.ToArray()))
        { CapturedReferences = capturedReferences, CapturedProjection = capturedProjection,
            SourceDefinitions = sourceDefinitions };
        return _musicScheduleDefinitionsCache.Definitions;
    }

    private static bool MatchesObservedScheduleDefinition(WmlMacroExpansionDefinition definition, WmlStaticSourceRange active)
    {
        if (!active.Source.IsAuthored || !PathsEqual(definition.SourcePath, active.Source.Path) ||
            definition.RawText.Trim() != active.Text.Trim()) return false;
        var line = 0;
        for (var index = 0; index < active.StartOffset; index++)
            if (active.Source.Text[index] == '\n' || active.Source.Text[index] == '\r' &&
                (index + 1 >= active.StartOffset || active.Source.Text[index + 1] != '\n')) line++;
        return definition.SourceStartLine == line;
    }

    private bool DefinitionFileMatchesIndex(WmlMacroExpansionDefinition definition,
        Dictionary<string, (bool Matches, string? Text)> checkedFiles, out string? capturedText)
    {
        capturedText = null;
        if (_macroLibrarySnapshot is not { } snapshot || string.IsNullOrWhiteSpace(definition.SourcePath)) return false;
        if (checkedFiles.TryGetValue(definition.SourcePath, out var known))
        { capturedText = known.Text; return known.Matches; }
        var indexed = snapshot.SourceFilesById.Values.Where(file =>
            PathsEqual(file.StoragePath, definition.SourcePath) &&
            (definition.PackageRevisionId is null || file.PackageRevisionId.ToString() == definition.PackageRevisionId)).ToArray();
        var matches = false;
        try
        {
            capturedText = File.ReadAllText(definition.SourcePath);
            var hash = WccContentHash.ComputeText(capturedText);
            matches = indexed.Length == 1 && string.Equals(indexed[0].ContentHash, hash, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        checkedFiles[definition.SourcePath] = (matches, capturedText);
        return matches;
    }

    public string? GetWccScenarioTextDomain()
    {
        if (SelectedScenario?.Document is not { } document) return null;
        return WmlTextDomainCapture.BeforeOffset(document.OriginalText, document.BlockSpan.StartIndex);
    }

    private bool IsInitialMusicSchedulePickerRevision(WccPackageRevisionId revisionId)
        => ScenarioContentVisibleRevisions?.Contains(revisionId) == true;

    // The captured planner's root ResourcePack is Core even before an external project's
    // executable profile is bound. This classification does not establish runtime defaults.
    private bool IsInitialCorePackage(WccPackageRecord package) => package.SourceKind == WccPackageSourceKinds.WesnothCore ||
        package.SourceKind == WccPackageSourceKinds.WesnothMainline && package.ContentKind == WccPackageContentKinds.ResourcePack &&
        package.ParentPackageId is null;

    internal bool IsInitialCoreOrWccPresetDefinition(WmlMacroExpansionDefinition? definition) =>
        definition?.PackageRevisionId is { } id && _macroLibrarySnapshot is { } snapshot &&
        snapshot.RevisionsById.Values.FirstOrDefault(revision => revision.Id.ToString() == id) is { } revision &&
        snapshot.PackagesById.TryGetValue(revision.PackageId, out var package) &&
        (IsInitialCorePackage(package) || package.SourceKind == WccPackageSourceKinds.WccManagedLibrary ||
         package.SourceKind == WccPackageSourceKinds.WccBundle);

    public IReadOnlyList<CatalogPickerOptionVm> GetWccInitialSchedulePickerOptions() =>
        GetWccScenarioMacroOptions(schedule: true).Where(option => option.LibraryEntityId is { } id &&
            _macroLibrarySnapshot is { } snapshot && snapshot.EntitiesById.TryGetValue(id, out var entity) &&
            snapshot.SourceFilesById.TryGetValue(entity.SourceFileId, out var file) &&
            IsInitialMusicSchedulePickerRevision(file.PackageRevisionId)).ToArray();

    private WccScheduleImagePreviews CreateWccScheduleImagePreviews(IReadOnlyList<WmlMacroExpansionDefinition> definitions) =>
        new(_macroLibrarySnapshot, _staticRootRevisionSelection?.RootRevisionId,
            (_currentSourcePath ?? LoadedPath) is { } path && _pristineLoadedSourceText is { } text
                ? new WmlStaticSource(path, text) : null, definitions);

    private ScheduleImagePreviewContext CaptureWccScheduleImagePreviewContext() =>
        new(_macroLibrarySnapshot, _staticRootRevisionSelection, _currentSourcePath ?? LoadedPath, _pristineLoadedSourceText,
            _capturedScheduleDisplayContext);

    private void RefreshWccScheduleImagePreviews()
    {
        var context = CaptureWccScheduleImagePreviewContext();
        if (_scheduleImagePreviewContext == context) return;
        _scheduleImagePreviewContext = context;
        if (DisplayedWccSchedule is not { } schedule) return;
        var images = CreateWccScheduleImagePreviews(_musicScheduleDefinitions);
        // A late Library/root publication changes image lookup inputs even when
        // the schedule text and definitions are identical. Rebind display objects
        // only; pending values, source projections and selection stay untouched.
        foreach (var row in _wccScheduleTimes.ToArray())
        {
            if (context != CaptureWccScheduleImagePreviewContext() || !ReferenceEquals(schedule, DisplayedWccSchedule)) return;
            if (_wccScheduleTimes.Contains(row)) row.Preview = HasPendingWccScheduleEdit && _scheduleUsesCapturedAuthoring &&
                _musicScheduleSourceBinding is { } binding && GetWccMusicScheduleBindingProblem(binding) is null &&
                _pendingScheduleImagePreviews.TryGetValue(row.Time.Index, out var retainedImage) ? retainedImage :
                (_usesCapturedScheduleDisplay || _scheduleUsesCapturedAuthoring) &&
                !HasPendingWccScheduleEdit &&
                _capturedSchedulePreview is { } captured && SameScheduleTimeline(captured.Schedule, schedule)
                ? CapturedScheduleImage(row.Time) ?? images.Time(schedule, row.Time.Index)
                : images.Time(schedule, row.Time.Index);
        }
    }

    public IReadOnlyList<CatalogPickerOptionVm> GetWccMusicPlaylistPickerOptions()
    {
        var options = new List<CatalogPickerOptionVm>();
        if (_macroLibrarySnapshot is { } snapshot)
        {
            var revisions = GetInitialMusicPickerRevisions(snapshot);
            foreach (var file in snapshot.SourceFilesById.Values.Where(file =>
                revisions.Contains(file.PackageRevisionId) &&
                file.FileType == WccSourceFileTypes.Audio))
            {
                if (GetMusicLogicalPath(file.RelativePath) is not { } logical ||
                    !snapshot.RevisionsById.TryGetValue(file.PackageRevisionId, out var revision) ||
                    !snapshot.PackagesById.TryGetValue(revision.PackageId, out var package) ||
                    GetInitialMusicOrigin(package, file.PackageRevisionId) is not { } origin) continue;
                options.Add(new()
                {
                    ValueId = logical, DisplayName = logical,
                    LibraryPackageId = package.Id, LibrarySourceFileId = file.Id,
                    LibraryPackageRevisionId = file.PackageRevisionId, LibraryContentHash = file.ContentHash,
                    ResourceSourceLabel = origin,
                    ResourceSourceHint = package.Name + (origin is "Core Campaign" or "WCC"
                        ? ": copied into the campaign when selected." : ": referenced directly."),
                    EmissionKind = CatalogPickerEmissionKind.Default
                });
            }
        }
        if (CaptureCurrentDependencyAudioScope() is { } dependencyAudio)
            options.AddRange(dependencyAudio.Build("music").Select(choice => choice.Option));
        RefreshImportedMusicRoot();
        foreach (var name in _importedScenarioMusic)
            if (!options.Any(option => option.ValueId == name && option.ResourceSourceLabel == "Campaign"))
                options.Add(new() { ValueId = name, DisplayName = name, ResourceSourceLabel = "Campaign",
                    ResourceSourceHint = "Already imported into this campaign's music folder.", EmissionKind = CatalogPickerEmissionKind.Default });
        var authored = ScenarioInitialMusicEditor.Project(GetWccScenarioSourceRawWml() ?? "",
            GetWccMusicScheduleDefinitions()).Tracks.Select(track => track.Name).Concat(WccMusicRows.Select(row => row.Name));
        foreach (var name in authored.Distinct(StringComparer.Ordinal))
            if (!options.Any(option => option.ValueId == name && option.ResourceSourceLabel is not "Core Campaign" and not "WCC"))
                options.Add(new() { ValueId = name, DisplayName = name, ResourceSourceLabel = "Scenario",
                    ResourceSourceHint = "Authored track name; its resource origin is not captured in this Library view.", EmissionKind = CatalogPickerEmissionKind.Default });
        return options.OrderBy(option => option.ValueId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(option => option.ResourceSourceLabel switch { "Campaign" => 0, "Core" => 1, "Core Campaign" => 2, "WCC" => 3, _ => 4 })
            .ThenBy(option => option.LibraryPackageId?.ToString(), StringComparer.Ordinal).ToArray();
    }

    private IReadOnlySet<WccPackageRevisionId> GetInitialMusicPickerRevisions(WccLibrarySnapshot snapshot)
    {
        var revisions = ScenarioContentVisibleRevisions?.ToHashSet() ?? [];
        var pinnedCores = revisions.Where(id => snapshot.RevisionsById.TryGetValue(id, out var revision) &&
            snapshot.PackagesById.TryGetValue(revision.PackageId, out var package) && IsInitialCorePackage(package))
            .Select(id => snapshot.RevisionsById[id]).ToArray();
        foreach (var core in pinnedCores)
            revisions.UnionWith(WccLibraryProfileRevisionScope.GetVisibleRevisionIds(snapshot, core.PackageId, core.Id));
        return revisions;
    }

    private string? GetInitialMusicOrigin(WccPackageRecord package, WccPackageRevisionId revision) =>
        _staticRootRevisionSelection?.RootRevisionId == revision ? "Campaign"
        : IsInitialCorePackage(package) ? "Core"
        : package.SourceKind == WccPackageSourceKinds.WesnothMainline && package.ContentKind == WccPackageContentKinds.Campaign ? "Core Campaign"
        : package.SourceKind == WccPackageSourceKinds.WccManagedLibrary || package.SourceKind == WccPackageSourceKinds.WccBundle ? "WCC" : null;

    private static string? GetMusicLogicalPath(string relativePath)
    {
        var path = relativePath.Replace('\\', '/');
        var musicIndex = path.StartsWith("music/", StringComparison.OrdinalIgnoreCase) ? 0
            : path.IndexOf("/music/", StringComparison.OrdinalIgnoreCase) is var position && position >= 0 ? position + 1 : -1;
        return musicIndex < 0 ? null : path[(musicIndex + 6)..];
    }

    private CatalogPickerOptionVm? FindCurrentWccMusicOption(CatalogPickerSelection selection) =>
        GetWccMusicPlaylistPickerOptions().SingleOrDefault(option => option.ValueId == selection.EmittedValue &&
            option.EmissionKind == selection.EmissionKind && option.LibraryEntityId == selection.LibraryEntityId &&
            option.LibraryPackageId == selection.LibraryPackageId &&
            option.LibrarySourceFileId == selection.LibrarySourceFileId &&
            option.LibraryPackageRevisionId == selection.LibraryPackageRevisionId &&
            option.LibraryContentHash == selection.LibraryContentHash);

    internal bool CanReferenceWccMusicSelectionDirectly(CatalogPickerSelection selection) =>
        FindCurrentWccMusicOption(selection) is { ResourceSourceLabel: not "Core Campaign" and not "WCC" };

    public async Task<bool> AddWccMusicSelectionsAsync(IEnumerable<CatalogPickerSelection> selections)
    {
        var requested = selections.ToArray();
        if (!CanEditWccMusicPlaylist || CaptureWccMusicSchedulePickerSource() is not { } source) return false;
        var snapshot = _macroLibrarySnapshot;
        var mainNames = GetWccMusicPlaylistMainTrackNames().ToHashSet(StringComparer.Ordinal);
        var chosen = new List<(CatalogPickerOptionVm Option, WccSourceFileRecord? File)>();
        foreach (var selection in requested.Distinct())
        {
            if (FindCurrentWccMusicOption(selection) is not { } option)
            { Status = "The selected music source changed. Reopen the picker."; return false; }
            if (option.ResourceSourceLabel is not "Core Campaign" and not "WCC" && !mainNames.Add(option.ValueId)) continue;
            WccSourceFileRecord? file = null;
            if (option.LibrarySourceFileId is { } id)
            {
                if (snapshot is null || !snapshot.SourceFilesById.TryGetValue(id, out file) ||
                    string.IsNullOrWhiteSpace(file.StoragePath))
                { Status = "The selected music source is unavailable. Refresh the Library."; return false; }
                try
                {
                    if (!string.Equals(await WccContentHash.ComputeFileAsync(file.StoragePath), file.ContentHash, StringComparison.OrdinalIgnoreCase))
                    { Status = "The selected music file changed. Refresh the Library before adding it."; return false; }
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                { Status = "The selected music file is unavailable. Refresh the Library."; return false; }
                if (!ReferenceEquals(snapshot, _macroLibrarySnapshot) || !IsCurrentWccMusicSchedulePickerSource(source)) return false;
            }
            chosen.Add((option, file));
        }
        if (chosen.Count == 0) return IsCurrentWccMusicSchedulePickerSource(source);
        var names = new List<string>();
        foreach (var (option, file) in chosen)
        {
            if (!ReferenceEquals(snapshot, _macroLibrarySnapshot) || !IsCurrentWccMusicSchedulePickerSource(source)) return false;
            if (option.ResourceSourceLabel is "Core Campaign" or "WCC")
            {
                if (!CanImportWccScenarioMusic() || file?.StoragePath is not { } path)
                { Status = "Open a writable campaign before copying selected music into it."; return false; }
                var imported = WccPackageMusicImporter.Import(_sourceHostSession.RootPath!, path);
                if (!imported.IsSuccess) { Status = imported.Problem; return false; }
                try
                {
                    if (!string.Equals(await WccContentHash.ComputeFileAsync(imported.DestinationPath!), file.ContentHash, StringComparison.OrdinalIgnoreCase))
                    { Status = "The copied music does not match its captured source. Refresh the Library."; return false; }
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                { Status = "The copied music file is unavailable. Refresh the Library."; return false; }
                if (!ReferenceEquals(snapshot, _macroLibrarySnapshot) || !IsCurrentWccMusicSchedulePickerSource(source)) return false;
                RefreshImportedMusicRoot();
                _importedScenarioMusic.Add(imported.WmlPath!);
                names.Add(imported.WmlPath!);
            }
            else names.Add(option.ValueId);
        }
        return ReferenceEquals(snapshot, _macroLibrarySnapshot) && IsCurrentWccMusicSchedulePickerSource(source) && AddWccMusicTracks(names);
    }

    private bool CanImportWccScenarioMusic() => !IsReadOnly && _sourceHostSession.CanWrite &&
        _sourceHostSession.Scope == WccEditorSessionScope.Project &&
        !string.IsNullOrWhiteSpace(_sourceHostSession.RootPath) &&
        IsPathInsideRoot(_currentSourcePath ?? LoadedPath, _sourceHostSession.RootPath) &&
        !IsProtectedWesnothInstallPath(_sourceHostSession.RootPath) && GetWccInlineBindingProblem() is null && !IsWccInlineSaveBusy &&
        !WccFieldEditSession.IsBusy && !_externalSourceChangesPending && !_hasAuthoredSourceChanges;

    private void RefreshImportedMusicRoot()
    {
        if (PathsEqual(_importedScenarioMusicRoot, _sourceHostSession.RootPath)) return;
        _importedScenarioMusicRoot = _sourceHostSession.RootPath;
        _importedScenarioMusic.Clear();
    }

    private ScenarioEventProjection SeparateInitialMusicEvents(ScenarioEventProjection projection)
    {
        var initial = ScenarioInitialMusicEditor.Project(GetWccScenarioSourceRawWml() ?? "",
            GetWccMusicScheduleDefinitions());
        var remaining = projection.Events.Where(entry => !initial.IsDedicatedGeneratedEvent(entry)).ToArray();
        return remaining.Length == projection.Events.Count ? projection : new ScenarioEventProjection
        {
            Events = remaining,
            MacroExpansion = projection.MacroExpansion,
            Diagnostics = projection.Diagnostics
        };
    }

    // File import is an explicit asset action. Source changes remain staged until Save.
    public Task<IReadOnlyList<string>> ImportWccScenarioMusicFilesAsync(IEnumerable<string> sourcePaths)
    {
        var imported = new List<string>();
        if (!CanImportWccScenarioMusic())
        {
            Status = "Open a writable campaign and save other source changes before importing music.";
            return Task.FromResult<IReadOnlyList<string>>(imported);
        }
        RefreshImportedMusicRoot();
        foreach (var source in sourcePaths)
        {
            var result = WccPackageMusicImporter.Import(_sourceHostSession.RootPath!, source);
            if (!result.IsSuccess) { Status = result.Problem; continue; }
            imported.Add(result.WmlPath!);
            _importedScenarioMusic.Add(result.WmlPath!);
        }
        if (imported.Count > 0) Status = $"Added {imported.Count} music file(s) to the campaign music folder.";
        return Task.FromResult<IReadOnlyList<string>>(imported);
    }
}
