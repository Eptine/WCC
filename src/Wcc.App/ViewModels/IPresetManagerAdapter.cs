using System.Collections.Generic;
using Wcc.Core.Library;

namespace Wcc.App.ViewModels;

public interface IPresetManagerAdapter
{
    string ManagerTitle { get; }
    string LibraryTitle { get; }
    string SearchWatermark { get; }
    string ItemKindLabel { get; }

    IReadOnlyList<PresetManagerItemVm> GetEntries(AppShellViewModel shell);

    string BuildNewTemplate(AppShellViewModel shell);
    string? BuildDuplicateTemplate(AppShellViewModel shell, PresetManagerItemVm selected);

    void SaveNew(AppShellViewModel shell, string rawText);
    void SaveExisting(AppShellViewModel shell, PresetManagerItemVm selected, string rawText);
    void Delete(AppShellViewModel shell, PresetManagerItemVm selected);
}

public sealed record PresetManagerCreationKind(
    string Id,
    string DisplayName);

public interface IPresetManagerCreationKindAdapter
{
    IReadOnlyList<PresetManagerCreationKind> CreationKinds { get; }

    string BuildNewTemplate(
        AppShellViewModel shell,
        PresetManagerCreationKind creationKind);
}

/// <summary>Macro duplication retains its indexed source for checked helper and resource copying.</summary>
public interface IPresetManagerManagedMacroCopyAdapter
{
    WccManagedLibraryFileKind ManagedMacroCopyKind { get; }
}
