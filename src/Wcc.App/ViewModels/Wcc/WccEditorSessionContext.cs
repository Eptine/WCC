namespace Wcc.App.ViewModels.Wcc;

public enum WccEditorSessionScope
{
    None,
    Project,
    LooseFile,
    Library
}

public enum WccEditorAccessMode
{
    ReadOnly,
    Writable
}

/// <summary>
/// Describes where an editor document came from and whether that context owns a
/// write boundary. Editor views stay reusable; the shell supplies this policy.
/// </summary>
public sealed record WccEditorSessionContext(
    WccEditorSessionScope Scope,
    WccEditorAccessMode AccessMode,
    string? RootPath,
    string DisplayLabel,
    string? AssetRootPath = null)
{
    public static WccEditorSessionContext Empty { get; } = new(
        WccEditorSessionScope.None,
        WccEditorAccessMode.ReadOnly,
        RootPath: null,
        DisplayLabel: "No document loaded");

    public bool CanWrite => AccessMode == WccEditorAccessMode.Writable;
    public bool IsReadOnly => !CanWrite;
    public bool CanImportPackageAssets =>
        CanWrite && !string.IsNullOrWhiteSpace(AssetRootPath);
}
