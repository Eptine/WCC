using System.Collections.Generic;
using System.Linq;
using Wcc.Core.Presets;
using Wcc.Core.Wml;

namespace Wcc.App.ViewModels;

public sealed class RacePresetManagerAdapter : IPresetManagerAdapter
{
    private readonly MainWindowViewModel? _context;

    public RacePresetManagerAdapter()
    {
    }

    public RacePresetManagerAdapter(MainWindowViewModel? context)
    {
        _context = context;
    }

    public string ManagerTitle => "Manage Races";
    public string LibraryTitle => "Race Library";
    public string SearchWatermark => "Search races";
    public string ItemKindLabel => "Race";

    public IReadOnlyList<PresetManagerItemVm> GetEntries(AppShellViewModel shell)
    {
        var standalone = shell.RaceCatalog.Entries
            .Select(entry =>
            {
                var packageLabel = shell.GetProjectedCatalogPackageLabel(entry.InternalId, entry.Origin);
                return (entry.SourcePath, ValueId: entry.Id, Item: new PresetManagerItemVm
                {
                    InternalId = entry.InternalId,
                    ValueId = entry.Id,
                    DisplayName = ResolveDisplayName(entry),
                    SecondaryText = $"{packageLabel} — {entry.SourceFileName}",
                    Description = WmlDisplayStringNormalizer.Normalize(entry.Description),
                    RawText = entry.RawText,
                    OriginLabel = packageLabel,
                    SourceFileName = entry.SourceFileName,
                    CanEdit = shell.CanEditManagedPreset(
                        entry.InternalId,
                        global::Wcc.Core.Library.WccManagedLibraryFileKind.Race),
                    CanDelete = shell.CanEditManagedPreset(
                        entry.InternalId,
                        global::Wcc.Core.Library.WccManagedLibraryFileKind.Race)
                });
            })
            .ToArray();
        return _context is null
            ? standalone.Select(row => row.Item).ToArray()
            : ContextualUnitPresetManagerEntries.Build(shell, _context.GetRaceCatalogOptions(),
                global::Wcc.Core.Library.WccEntityTypes.Race, standalone);
    }

    public string BuildNewTemplate(AppShellViewModel shell)
    {
        var raceId = shell.CreateUniqueRaceIdSuggestion("custom_race");
        return RaceWmlTemplateHelper.CreateNewRaceTemplate(raceId);
    }

    public string? BuildDuplicateTemplate(AppShellViewModel shell, PresetManagerItemVm selected)
    {
        var newId = shell.CreateUniqueRaceIdSuggestion(selected.ValueId);
        return RaceWmlTemplateHelper.ReplaceRaceId(selected.RawText, newId);
    }

    public void SaveNew(AppShellViewModel shell, string rawText)
        => shell.UpsertWccRaceFromRawText(rawText);

    public void SaveExisting(AppShellViewModel shell, PresetManagerItemVm selected, string rawText)
        => shell.UpsertWccRaceFromRawText(rawText, selected.InternalId);

    public void Delete(AppShellViewModel shell, PresetManagerItemVm selected)
        => shell.DeleteWccRaceByInternalId(selected.InternalId);

    private static string ResolveDisplayName(RaceCatalogEntry entry)
    {
        var label = FirstNonEmpty(
            WmlDisplayStringNormalizer.Normalize(entry.PluralName),
            WmlDisplayStringNormalizer.Normalize(entry.Name),
            WmlDisplayStringNormalizer.Normalize(entry.MaleName),
            WmlDisplayStringNormalizer.Normalize(entry.FemaleName),
            WmlDisplayStringNormalizer.HumanizeIdentifier(entry.Id),
            entry.Id);

        return label is null ? entry.Id : $"{entry.Id} — {label}";
    }

    private static string? FirstNonEmpty(params string?[] values)
        => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
}
