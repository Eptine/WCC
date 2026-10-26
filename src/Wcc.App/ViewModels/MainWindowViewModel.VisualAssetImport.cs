using System;
using System.Threading.Tasks;
using Wcc.Core.References;
using Wcc.Core.Wml;

namespace Wcc.App.ViewModels;

internal sealed record UnitVisualAssetImportRequest(string PackageRoot, string UnitId,
    WccPackageImageTarget Target, Func<bool> IsCurrent, Func<string, bool> Apply);

public partial class MainWindowViewModel
{
    internal UnitVisualAssetImportRequest? CaptureVisualAssetImportRequest(string fieldKey,
        out string? problem, Func<bool>? hostIsCurrent = null)
    {
        problem = null;
        bool CanPick() => fieldKey switch
        {
            "image" => CanPickVisualImage,
            "profile" => CanPickVisualProfile,
            "small_profile" => CanPickVisualSmallProfile,
            _ => false
        };
        if (!CanPick() || WritablePackageAssetRoot is not { } packageRoot)
        {
            problem = "Image import requires an editable visual field in a writable Project or WCC Library package.";
            return null;
        }
        if (!UnitIdIsEditable || !WmlResourceValueTextDecoder.TryDecode(UnitId, out var unitId, out _) ||
            WccUnitResourcePathPolicy.GetUnitIdProblem(unitId) is { })
        {
            problem = "Unit image import needs a nonempty, unambiguous literal unit ID; resolve the unit ID before choosing an image.";
            return null;
        }

        // Rendering captures scalar drafts too: those do not all advance EditVersion.
        var draft = RenderPatchedFile();
        if (draft is null)
        {
            problem = "The current unit source could not be captured for image import.";
            return null;
        }
        var rawGuard = CaptureRawEditorApplyGuard();
        var idDraft = UnitId;
        bool IsCurrent() => (hostIsCurrent?.Invoke() ?? true) && rawGuard() && CanPick() &&
            string.Equals(idDraft, UnitId, StringComparison.Ordinal) &&
            string.Equals(draft, RenderPatchedFile(), StringComparison.Ordinal);
        return new(packageRoot, unitId, fieldKey == "image" ? WccPackageImageTarget.UnitImage : WccPackageImageTarget.Portrait,
            IsCurrent, path => IsCurrent() && ApplyImportedVisualAssetPath(fieldKey, path));
    }

    internal async Task<bool> ImportVisualAssetAsync(UnitVisualAssetImportRequest request, string sourcePath)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!request.IsCurrent())
        {
            Status = "The unit source or editing context changed. Reopen its image picker.";
            return false;
        }
        var result = await Task.Run(() => WccPackageImageImporter.Import(
            request.PackageRoot, sourcePath, request.Target, request.UnitId));
        if (!result.IsSuccess || string.IsNullOrWhiteSpace(result.WmlPath))
        {
            Status = "Could not import the selected image: " + (result.ErrorMessage ?? result.Status.ToString());
            return false;
        }
        if (!request.Apply(result.WmlPath))
        {
            Status = "The unit source or editing context changed. Reopen its image picker. The package image remains available at " + result.WmlPath + ".";
            return false;
        }
        var action = result.Status switch
        {
            WccPackageImageImportStatus.AlreadyOwned => "Selected the existing package image",
            WccPackageImageImportStatus.ReusedExisting => "Reused an identical package image",
            _ => "Copied the image into the package"
        };
        Status = action + " as " + result.WmlPath + ". Save the unit to persist the WML reference.";
        return true;
    }
}
