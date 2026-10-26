using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Wcc.App.ViewModels.Wcc;
using Wcc.Core.Library;
using Wcc.Core.Projects;
using Wcc.Core.References;
using Wcc.Core.Scenarios;
using Wcc.Core.Wml;

namespace Wcc.App.ViewModels;

public partial class MainWindowViewModel
{
    private string? _attackName;
    private string? _attackDescriptionWml;
    private string? _attackIcon;

    public string? AttackName
    {
        get => AttackText(AttackDisplayValue(_attackName, "name"));
        set => SetAttackText(ref _attackName, value, "name", false, AttackNameIsEditable);
    }
    public string? AttackDescription
    {
        get => AttackText(AttackDisplayValue(_attackDescriptionWml, "description"));
        set => SetAttackText(ref _attackDescriptionWml, value, "description", true, AttackDescriptionIsEditable);
    }
    public string? AttackIcon
    {
        get => AttackText(AttackDisplayValue(_attackIcon, "icon"));
        set => SetAttackText(ref _attackIcon, value, "icon", false, AttackIconIsEditable);
    }
    public bool AttackNameIsEditable => SelectedAttack is { IsInherited: false } && IsEditable(SelectedAttack.Attack.FieldStates, "name");
    public bool AttackDescriptionIsEditable => SelectedAttack is { IsInherited: false } && IsEditable(SelectedAttack.Attack.FieldStates, "description");
    public bool AttackIconIsEditable => SelectedAttack is { IsInherited: false } && IsEditable(SelectedAttack.Attack.FieldStates, "icon");
    public bool AttackNameHasRawEditor => SelectedAttack is { IsInherited: false } && HasRawEditor(SelectedAttack.Attack.FieldStates, "name");
    public bool AttackDescriptionHasRawEditor => SelectedAttack is { IsInherited: false } && HasRawEditor(SelectedAttack.Attack.FieldStates, "description");
    public bool AttackIconHasRawEditor => SelectedAttack is { IsInherited: false } && HasRawEditor(SelectedAttack.Attack.FieldStates, "icon");
    public bool AttackNameIsInherited => AttackIsInherited("name");
    public bool AttackDescriptionIsInherited => AttackIsInherited("description");
    public bool AttackIconIsInherited => AttackIsInherited("icon");
    public bool CanPickAttackIcon => HasWritablePackageAssetRoot && AttackIconIsEditable;

    private static string? AttackText(string? raw) => WccWmlPlainTextCodec.TryReadLiteral(raw, out var text, out _) ? text : raw;

    private void SetAttackText(ref string? direct, string? value, string key, bool translate, bool editable,
        [CallerMemberName] string? property = null)
    {
        if (IsSynchronizingUi || !editable || value is null || AttackText(AttackDisplayValue(direct, key)) == value) return;
        if (!WccWmlPlainTextCodec.TryEncodeInput(value, translate, AttackDisplayValue(direct, key), out var encoded, out var problem))
        { Status = problem; return; }
        if (key == "description" && GetSelectedAttackDocument() is { } document)
            TouchRoot("attack:" + document.InternalId + ":description");
        if (!SetAttackDisplayValue(ref direct, encoded, key, property)) return;
        PushCurrentAttackFieldsIntoModel();
        if (key == "icon") RefreshSelectedUnitAssetReferences();
        RefreshAttackIconPreviews();
    }

    private void LoadAttackIdentityFields()
    {
        _attackName = SelectedAttack?.Attack.Name;
        _attackDescriptionWml = SelectedAttack?.Attack.DescriptionWml;
        _attackIcon = SelectedAttack?.Attack.Icon;
        OnPropertyChanged(nameof(AttackName)); OnPropertyChanged(nameof(AttackDescription)); OnPropertyChanged(nameof(AttackIcon));
        RaiseAttackEditingStateChanged();
    }

    private void RaiseAttackEditingStateChanged()
    {
        foreach (var property in new[] { nameof(AttackName), nameof(AttackDescription), nameof(AttackIcon) })
        {
            OnPropertyChanged(property + "IsEditable");
            OnPropertyChanged(property + "HasRawEditor"); OnPropertyChanged(property + "IsInherited");
        }
        OnPropertyChanged(nameof(CanPickAttackIcon));
    }

    private Func<bool>? CaptureAttackIconGuard(Func<bool>? hostIsCurrent)
    {
        if (!CanPickAttackIcon) return null;
        var draft = RenderPatchedFile();
        if (draft is null) return null;
        var sourceGuard = CaptureRawEditorApplyGuard();
        return () => (hostIsCurrent?.Invoke() ?? true) && sourceGuard() && CanPickAttackIcon &&
            string.Equals(draft, RenderPatchedFile(), StringComparison.Ordinal);
    }

    internal UnitVisualAssetImportRequest? CaptureAttackIconImportRequest(out string? problem, Func<bool>? hostIsCurrent = null)
    {
        problem = null;
        if (CaptureAttackIconGuard(hostIsCurrent) is not { } current || WritablePackageAssetRoot is not { } root)
        { problem = "Attack icon import needs a local editable attack in a writable package."; return null; }
        if (!UnitIdIsEditable || !WmlResourceValueTextDecoder.TryDecode(UnitId, out var unitId, out _) ||
            WccUnitResourcePathPolicy.GetUnitIdProblem(unitId) is { })
        { problem = "Attack icon import needs a nonempty, unambiguous literal unit ID."; return null; }
        return new(root, unitId, WccPackageImageTarget.AttackIcon, current, path =>
        {
            if (!current()) return false;
            return ApplyExplicitAttackIcon(path);
        });
    }

    private bool ApplyExplicitAttackIcon(string path)
    {
        if (!CanPickAttackIcon || SelectedAttack is not { IsInherited: false } selected) return false;
        // Choosing a physical resource is explicit local intent even when its
        // relative spelling equals an inherited image from another package.
        if (AttackIcon == path && AttackIconIsInherited)
        {
            if (!_attackEffectiveTouched.TryGetValue(selected.Attack, out var touched))
                _attackEffectiveTouched[selected.Attack] = touched = new(StringComparer.Ordinal);
            touched.Add("icon");
            _attackIcon = WccWmlPlainTextCodec.EncodePlainText(path, false);
            PushCurrentAttackFieldsIntoModel();
            OnPropertyChanged(nameof(AttackIcon)); OnPropertyChanged(nameof(AttackIconIsInherited));
            RefreshSelectedUnitAssetReferences(); RefreshAttackIconPreviews();
        }
        else AttackIcon = path;
        return AttackIcon == path && !AttackIconIsInherited;
    }

    // The generic picker returns an exact physical resource choice. Only this
    // adapter maps that receipt to a relative WML path; it grants no save authority.
    private sealed record AttackIconChoice(CatalogPickerOptionVm Option, string Root, string Path,
        string WmlPath, long Length, DateTime Modified, string? IndexedHash);

    public async Task<bool> PickAttackIconAsync(Func<CatalogPickerWindowViewModel, Task<CatalogPickerSelection?>> present,
        Func<bool>? hostIsCurrent = null)
    {
        ArgumentNullException.ThrowIfNull(present);
        if (CaptureAttackIconGuard(hostIsCurrent) is not { } current) return false;
        var root = WritablePackageAssetRoot!; var install = _wesnothInstallPath; var snapshot = _macroLibrarySnapshot;
        var revisions = _macroVisibleRevisionIds?.ToHashSet();
        try
        {
            var choices = await Task.Run(() => BuildAttackIconChoices(root, install, snapshot, revisions));
            if (!current()) return false;
            var selected = choices.Where(choice => choice.WmlPath == AttackIcon).Select(choice => choice.Option.ToSelection()).ToArray();
            var picker = new CatalogPickerWindowViewModel(choices.Select(choice => choice.Option), null, false,
                "Attack icon", "Choose an icon from Core or the current package. These files remain directly referenced; Upload copies a new image into this package.",
                currentSelections: selected.Length == 1 ? selected : []);
            var selection = await present(picker);
            if (selection is null || !current()) return false;
            var matches = choices.Where(choice => choice.Option.ToSelection() == selection).ToArray();
            if (matches.Length != 1) { Status = "That icon no longer matches one captured source. Reopen its picker."; return false; }
            var choice = matches[0]; var info = new FileInfo(choice.Path);
            if (!info.Exists || info.Length != choice.Length || info.LastWriteTimeUtc != choice.Modified)
            { Status = "The selected icon changed. Reopen its picker."; return false; }
            var capture = WccTextDiskRevisionCapture.CaptureBinary(choice.Root, choice.Path);
            if (!capture.IsCaptured || capture.Revision is null || choice.IndexedHash is { } hash &&
                !string.Equals(hash, capture.Revision.RawByteSha256, StringComparison.OrdinalIgnoreCase))
            { Status = "The selected icon no longer matches its captured package. Refresh the Library and reopen the picker."; return false; }
            var resolved = WccAttackIconPathResolver.ResolveFields(_currentSourcePath, _currentSourceOrigin,
                choice.WmlPath, AttackDisplayValue(_attackName, "name"), install);
            if (!string.Equals(resolved.ResolvedAbsolutePath, choice.Path,
                    OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            { Status = "Another package image shadows this icon path. Choose that image or upload a distinct copy."; return false; }
            if (!current()) return false;
            return ApplyExplicitAttackIcon(choice.WmlPath);
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        { Status = "Could not select an attack icon: " + failure.Message; return false; }
    }

    private static IReadOnlyList<AttackIconChoice> BuildAttackIconChoices(string packageRoot, string? install,
        WccLibrarySnapshot? snapshot, IReadOnlySet<WccPackageRevisionId>? visible)
    {
        var result = new List<AttackIconChoice>();
        AddRoot(packageRoot, "Current package", false);
        if (!string.IsNullOrWhiteSpace(install)) AddRoot(WccCoreInstallPaths.GetCoreDataRoot(install), "Core", true);
        return result;

        void AddRoot(string root, string label, bool core)
        {
            root = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(root));
            var images = System.IO.Path.Combine(root, "images");
            var directory = core ? System.IO.Path.Combine(images, "attacks") : images;
            if (!Directory.Exists(directory)) return;
            var packages = snapshot?.PackagesById.Values.Where(package => package.IsAvailable && package.SourceLocation is { } source &&
                string.Equals(System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(source)), root,
                    OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)).ToArray() ?? [];
            var package = packages.Length == 1 ? packages[0] : null;
            var revision = package?.CurrentRevisionId is { } id && (visible is null || visible.Contains(id)) ? (WccPackageRevisionId?)id : null;
            foreach (var path in Directory.EnumerateFiles(directory, "*", new EnumerationOptions
                { RecurseSubdirectories = true, MaxRecursionDepth = 24, AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = false }))
            {
                if (!new[] { ".png", ".webp", ".jpg", ".jpeg" }.Contains(System.IO.Path.GetExtension(path), StringComparer.OrdinalIgnoreCase)) continue;
                if (result.Count >= 4096) throw new InvalidOperationException("The attack icon choices exceed the bounded image list.");
                var full = System.IO.Path.GetFullPath(path);
                var relative = System.IO.Path.GetRelativePath(images, full).Replace('\\', '/');
                var files = snapshot?.SourceFilesById.Values.Where(file => file.PackageRevisionId == revision && file.StoragePath is { } storage &&
                    string.Equals(System.IO.Path.GetFullPath(storage), full, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)).ToArray() ?? [];
                var indexed = files.Length == 1 ? files[0] : null;
                var info = new FileInfo(full);
                result.Add(new(new CatalogPickerOptionVm
                {
                    ValueId = full, DisplayName = relative, LibraryPackageId = revision is null ? null : package?.Id,
                    LibraryPackageRevisionId = revision, LibrarySourceFileId = indexed?.Id, LibraryContentHash = indexed?.ContentHash,
                    ResourceSourceLabel = label, ResourceSourceHint = full, PreviewPathResolver = () => full
                }, root, full, relative, info.Length, info.LastWriteTimeUtc, indexed?.ContentHash));
            }
        }
    }
}
