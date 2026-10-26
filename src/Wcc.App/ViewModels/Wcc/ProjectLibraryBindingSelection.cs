using System;
using System.Collections.Generic;
using Wcc.Core.Library;

namespace Wcc.App.ViewModels.Wcc;

public enum CatalogPickerEmissionKind
{
    Default,
    MacroInvocation,
    RegistryId
}

public sealed record CatalogPickerSelection(
    string EmittedValue,
    WccEntityId? LibraryEntityId,
    WccPackageId? LibraryPackageId,
    CatalogPickerEmissionKind EmissionKind = CatalogPickerEmissionKind.Default)
{
    public WccSourceFileId? LibrarySourceFileId { get; init; }
    public WccPackageRevisionId? LibraryPackageRevisionId { get; init; }
    public string? LibraryContentHash { get; init; }
}

public sealed class ProjectLibraryBindingSelectionEventArgs : EventArgs
{
    public required string SourcePath { get; init; }
    public required WccEntityTypeId OwnerEntityType { get; init; }
    public string? OwnerExportSymbol { get; init; }
    public required int OwnerEntityOrdinal { get; init; }
    public required string FieldPath { get; init; }
    public required IReadOnlyList<CatalogPickerSelection> Selections { get; init; }
}
