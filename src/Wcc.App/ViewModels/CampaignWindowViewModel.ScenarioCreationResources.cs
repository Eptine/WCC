using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Wcc.App.ViewModels.Wcc;
using Wcc.Core.Library;
using Wcc.Core.Macros;
using Wcc.Core.Maps;
using Wcc.Core.Scenarios;
using Wcc.Core.Wml;

namespace Wcc.App.ViewModels;

public partial class CampaignWindowViewModel
{
    // Display choices only. The typed creation reviewer recaptures the actual
    // candidate campaign in every fixed context before any file is written.
    private ScenarioCreationWizardSetup CreateCampaignScenarioWizardSetup(CampaignDraftBinding binding,
        WmlStaticSource main, WmlCampaignEvaluationPlan plan, IReadOnlyList<WmlStaticSource> sources,
        WccCampaignLocalPresetCatalogSnapshot? catalog, string? install, Func<bool> current, WccDependencyAudioScope? dependencyAudio = null)
    {
        var snapshot = binding.Configuration.Snapshot!;
        var root = binding.Configuration.RootRevision!.Value;
        var visible = WccLibraryRevisionVisibility.GetVisibleRevisionIds(snapshot, root);
        var units = PresetUnitChoices(snapshot, visible, install);
        var music = CreateCampaignScenarioMusicOptions(snapshot, root, visible).Concat(
            dependencyAudio?.Matches(snapshot, root, plan) == true
                ? dependencyAudio.Build("music").Select(choice => choice.Option) : Enumerable.Empty<CatalogPickerOptionVm>()).ToArray();
        var scheduleNames = WccLibraryMacroCatalogProjection.Build(snapshot, visible)
            .Where(entry => entry.PurposeKind == WccMacroPurposeKind.Schedule)
            .Select(entry => entry.Name).Distinct(StringComparer.Ordinal).ToArray();
        var musicNames = new[] { "SCENARIO_MUSIC", "INTRO_AND_SCENARIO_MUSIC", "EXTRA_SCENARIO_MUSIC", "LET_INTRO_MUSIC_FINISH" };
        var definitions = CaptureCampaignScenarioChoiceDefinitions(snapshot, visible, main.Path, sources,
            scheduleNames.Concat(musicNames));
        var schedules = CreateCampaignScenarioScheduleChoices(snapshot, root, visible, main, plan, definitions);
        var musicDefinitions = CampaignScenarioDefinitionClosure(musicNames, definitions);
        var previews = new WccPresetFieldImagePreviews(snapshot, root, main.Path, install, current);
        try
        {
            return new(plan, catalog?.Entries ?? [], units, music, schedules, previews.Get,
                musicDefinitions: musicDefinitions, previewLifetime: previews,
                defaultHeroId: _campaignAuthoring!.AddonId + "_Leader",
                terrainCatalog: WesnothTerrainVisualCatalog.LoadCore(install));
        }
        catch { previews.Dispose(); throw; }
    }

    private static CatalogPickerOptionVm[] CreateCampaignScenarioMusicOptions(WccLibrarySnapshot snapshot,
        WccPackageRevisionId root, IReadOnlySet<WccPackageRevisionId> visible)
    {
        var options = new List<CatalogPickerOptionVm>();
        foreach (var file in snapshot.SourceFilesById.Values.Where(file =>
                     file.FileType == WccSourceFileTypes.Audio && visible.Contains(file.PackageRevisionId)))
        {
            if (!snapshot.RevisionsById.TryGetValue(file.PackageRevisionId, out var revision) ||
                !snapshot.PackagesById.TryGetValue(revision.PackageId, out var package) || !package.IsAvailable) continue;
            var campaign = file.PackageRevisionId == root;
            if (!campaign && !IsCampaignScenarioCorePackage(package)) continue;
            var path = file.RelativePath.Replace('\\', '/');
            // Owned campaigns register their package root. A nested package's
            // music directory is not automatically a runtime binary path.
            var index = path.StartsWith("music/", StringComparison.OrdinalIgnoreCase) ? 0 : campaign ? -1 :
                path.IndexOf("/music/", StringComparison.OrdinalIgnoreCase) is var position && position >= 0 ? position + 1 : -1;
            if (index < 0 || path.Length <= index + 6 || string.IsNullOrWhiteSpace(file.StoragePath)) continue;
            var name = path[(index + 6)..];
            options.Add(new()
            {
                ValueId = name, DisplayName = name, LibraryPackageId = package.Id,
                LibrarySourceFileId = file.Id, LibraryPackageRevisionId = file.PackageRevisionId,
                LibraryContentHash = file.ContentHash, ResourceSourceLabel = campaign ? "Campaign" : "Core",
                ResourceSourceHint = package.Name + ": referenced directly; no file is copied by this wizard.",
                EmissionKind = CatalogPickerEmissionKind.Default
            });
        }
        return options.OrderBy(option => option.ValueId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(option => option.ResourceSourceLabel == "Campaign" ? 0 : 1)
            .ThenBy(option => option.LibrarySourceFileId?.ToString(), StringComparer.Ordinal).ToArray();
    }

    private static bool IsCampaignScenarioCorePackage(WccPackageRecord package) =>
        package.ParentPackageId is null &&
        (package.SourceKind == WccPackageSourceKinds.WesnothCore || package.SourceKind == WccPackageSourceKinds.WesnothMainline) &&
        (package.ContentKind == WccPackageContentKinds.Core || package.ContentKind == WccPackageContentKinds.ResourcePack);

    private static IReadOnlyList<WmlMacroExpansionDefinition> CaptureCampaignScenarioChoiceDefinitions(
        WccLibrarySnapshot snapshot, IReadOnlySet<WccPackageRevisionId> visible, string mainPath,
        IReadOnlyList<WmlStaticSource> sources, IEnumerable<string> names)
    {
        var resolver = new WccLibraryMacroExpansionDefinitionResolver(snapshot, visible, mainPath);
        var pending = new Queue<string>(names.Distinct(StringComparer.Ordinal));
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var definitions = new List<WmlMacroExpansionDefinition>();
        var captured = sources.Where(source => source.IsAuthored).GroupBy(source => Path.GetFullPath(source.Path),
                OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal)
            .Where(group => group.Select(source => source.ContentHash).Distinct(StringComparer.Ordinal).Count() == 1)
            .ToDictionary(group => group.Key, group => group.First(),
                OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        var parsedFiles = new Dictionary<string, IReadOnlyList<WmlMacroDefinition>>(
            OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        while (pending.TryDequeue(out var name) && seen.Count < 2048)
        {
            if (!seen.Add(name)) continue;
            var candidates = resolver.FindDefinitions(name);
            if (candidates.Count != 1 || candidates[0] is not { SourcePath: { } path, SourceStartLine: { } line } definition ||
                !captured.TryGetValue(Path.GetFullPath(path), out var source)) continue;
            var indexed = snapshot.SourceFilesById.Values.Where(file => visible.Contains(file.PackageRevisionId) &&
                file.PackageRevisionId.ToString() == definition.PackageRevisionId &&
                file.StoragePath is { } storage && CampaignScenarioPathsEqual(storage, path)).ToArray();
            if (indexed.Length != 1 || !string.Equals(indexed[0].ContentHash, source.ContentHash, StringComparison.OrdinalIgnoreCase)) continue;
            if (!parsedFiles.TryGetValue(source.Path, out var parsed))
                parsedFiles[source.Path] = parsed = WmlMacroDefinitionScanner.Scan(source.Text);
            var exact = parsed.Where(item => item.Name == name && item.StartLineIndex == line && item.RawText == definition.RawText).ToArray();
            if (exact.Length != 1) continue;
            definitions.Add(definition with { TextDomain = WmlTextDomainCapture.BeforeLine(source.Text, line) });
            foreach (var invocation in WmlMacroInvocationSyntaxScanner.Scan(definition.RawText))
                if (!seen.Contains(invocation.Name)) pending.Enqueue(invocation.Name);
        }
        return Array.AsReadOnly(definitions.ToArray());
    }

    private static ScenarioCreationScheduleChoice[] CreateCampaignScenarioScheduleChoices(WccLibrarySnapshot snapshot,
        WccPackageRevisionId root, IReadOnlySet<WccPackageRevisionId> visible, WmlStaticSource main,
        WmlCampaignEvaluationPlan plan, IReadOnlyList<WmlMacroExpansionDefinition> definitions)
    {
        var pureNames = WmlScheduleMacroClassifier.GetScheduleMacroNames(definitions);
        var domain = WmlTextDomainCapture.BeforeOffset(main.Text, plan.AuthoredCampaignSpan?.StartIndex ?? 0);
        var options = new List<ScenarioCreationScheduleChoice>();
        foreach (var entity in snapshot.FindEntities(WccEntityTypes.MacroDefinition))
        {
            if (entity.ExportSymbol is not { } name || !pureNames.Contains(name) ||
                !WccLibraryRevisionVisibility.ContainsEntity(snapshot, visible, entity) ||
                !snapshot.SourceFilesById.TryGetValue(entity.SourceFileId, out var file) ||
                !snapshot.RevisionsById.TryGetValue(file.PackageRevisionId, out var revision) ||
                !snapshot.PackagesById.TryGetValue(revision.PackageId, out var package) || !package.IsAvailable ||
                file.PackageRevisionId != root && !IsCampaignScenarioCorePackage(package)) continue;
            var exact = definitions.Where(definition => definition.Name == name && definition.SourcePath is { } path &&
                file.StoragePath is { } storage && CampaignScenarioPathsEqual(path, storage) &&
                definition.PackageRevisionId == file.PackageRevisionId.ToString() && definition.SourceStartLine == entity.SourceSpan?.StartLine).ToArray();
            if (exact.Length != 1) continue;
            var invocation = "{" + name + "}";
            var closure = CampaignScenarioDefinitionClosure([name], definitions);
            var preview = ScenarioInitialScheduleEditor.Project("[scenario]\nid=wcc_schedule_preview\n" + invocation + "\n[/scenario]\n", closure, domain);
            // Required arguments and mixed output need their normal authored
            // editing workflow. A name or filename alone never proves a preset.
            if (!preview.CanCompose || preview.Times.Count == 0 || preview.PresetInvocation != invocation ||
                !preview.StartIsKnown || preview.IsRandomStart || preview.StartIndex is not { } start || start < 0 || start >= preview.Times.Count) continue;
            // The wizard displays the actual fixed start first. Keeping the
            // invocation preserves a preset's current_time; switching to custom
            // then keeps the same starting time without an index override.
            var times = Array.AsReadOnly(preview.Times.Skip(start).Concat(preview.Times.Take(start))
                .Select((time, index) => time with { Index = index }).ToArray());
            var option = new CatalogPickerOptionVm
            {
                ValueId = name, DisplayName = name + " [" + package.Name + "]",
                LibraryEntityId = entity.Id, LibraryPackageId = package.Id,
                LibrarySourceFileId = file.Id, LibraryPackageRevisionId = file.PackageRevisionId,
                LibraryContentHash = file.ContentHash, ResourceSourceLabel = file.PackageRevisionId == root ? "Campaign" : "Core",
                EmissionKind = CatalogPickerEmissionKind.MacroInvocation
            };
            options.Add(new(option, invocation, times, closure));
        }
        return options.OrderBy(choice => choice.Invocation == "{DEFAULT_SCHEDULE}" ? 0 : 1)
            .ThenBy(choice => choice.Option.ValueId, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static IReadOnlyList<WmlMacroExpansionDefinition> CampaignScenarioDefinitionClosure(IEnumerable<string> names,
        IReadOnlyList<WmlMacroExpansionDefinition> definitions)
    {
        var pending = new Queue<string>(names); var seen = new HashSet<string>(StringComparer.Ordinal);
        var selected = new List<WmlMacroExpansionDefinition>();
        while (pending.TryDequeue(out var name) && seen.Count < 2048)
        {
            if (!seen.Add(name)) continue;
            var candidates = definitions.Where(definition => definition.Name == name).ToArray();
            if (candidates.Length != 1) continue;
            selected.Add(candidates[0]);
            foreach (var invocation in WmlMacroInvocationSyntaxScanner.Scan(candidates[0].RawText))
                if (!seen.Contains(invocation.Name)) pending.Enqueue(invocation.Name);
        }
        return Array.AsReadOnly(selected.ToArray());
    }

    private static bool CampaignScenarioPathsEqual(string left, string right) => string.Equals(
        Path.GetFullPath(left), Path.GetFullPath(right), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}
