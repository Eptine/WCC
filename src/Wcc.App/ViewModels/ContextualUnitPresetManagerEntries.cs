using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Wcc.App.ViewModels.Wcc;
using Wcc.Core.Library;
using Wcc.Core.Wml;

namespace Wcc.App.ViewModels;

// The editor's picker receipts define this view. Reuse an exact standalone row
// when present so its existing managed edit/delete route remains unchanged.
internal static class ContextualUnitPresetManagerEntries
{
    public static IReadOnlyList<PresetManagerItemVm> Build(
        AppShellViewModel shell,
        IReadOnlyList<CatalogPickerOptionVm> options,
        WccEntityTypeId entityType,
        IReadOnlyList<(string? SourcePath, string ValueId, PresetManagerItemVm Item)> standalone)
    {
        if (shell.LibrarySnapshot is not { } snapshot)
            return Array.Empty<PresetManagerItemVm>();

        var result = new List<PresetManagerItemVm>();
        var seen = new HashSet<WccEntityId>();
        foreach (var option in options)
        {
            if (option.LibraryEntityId is not { } id || !seen.Add(id) ||
                !snapshot.EntitiesById.TryGetValue(id, out var entity) || entity.EntityType != entityType ||
                !snapshot.SourceFilesById.TryGetValue(entity.SourceFileId, out var source) ||
                snapshot.FindPackageForEntity(id) is not { } package)
                continue;

            var existing = standalone.Where(row => shell.FindProjectedCatalogEntityId(row.Item.InternalId) == id &&
                row.ValueId == entity.ExportSymbol &&
                SamePath(row.SourcePath, source.StoragePath)).ToArray();
            if (existing.Length == 1)
            {
                result.Add(existing[0].Item);
                continue;
            }

            var symbol = entity.ExportSymbol ?? option.ValueId;
            var name = WmlDisplayStringNormalizer.Normalize(entity.DisplayName) ?? symbol;
            var sourceFileName = Path.GetFileName(source.RelativePath);
            result.Add(new PresetManagerItemVm
            {
                InternalId = "context-" + id,
                LibraryEntityId = id,
                ValueId = symbol,
                DisplayName = name == symbol ? symbol : $"{symbol} — {name}",
                SecondaryText = $"{package.Name} — {sourceFileName}",
                Description = entity.Summary.TryGetValue("description", out var description)
                    ? WmlDisplayStringNormalizer.Normalize(description) : null,
                RawText = string.Empty,
                OriginLabel = package.Name,
                SourceFileName = sourceFileName,
                CanEdit = false,
                CanDelete = false,
                CanDuplicate = shell.IsVisibleInActiveLibrary(entity)
            });
        }
        return result;
    }

    private static bool SamePath(string? left, string? right) =>
        !string.IsNullOrWhiteSpace(left) && !string.IsNullOrWhiteSpace(right) &&
        string.Equals(Path.GetFullPath(left), Path.GetFullPath(right),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}

public partial class AppShellViewModel
{
    internal WccEntityId? FindProjectedCatalogEntityId(string internalId) =>
        _projectedCatalogEntityIds.TryGetValue(internalId, out var entityId) ? entityId : null;

    internal bool IsRevisionVisibleInActiveLibrary(WccPackageRevisionId revisionId) =>
        _activeLibraryRevisionIds.Contains(revisionId);
}
