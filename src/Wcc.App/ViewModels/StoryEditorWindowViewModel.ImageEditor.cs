using System;
using System.Collections.Generic;
using Wcc.Core.Projects;
using Wcc.Core.References;

namespace Wcc.App.ViewModels;

public sealed partial class StoryEditorWindowViewModel
{
    // Freeze the existing upload/owner/Core display route before local worker use.
    internal Func<string, WccUnitPreviewResolution> CaptureImageEditorResources()
    {
        var uploads = new Dictionary<string, string>(_uploadPreviewPaths, StringComparer.Ordinal);
        var source = Session.SourcePath;
        var install = Session.SourceInstallPath;
        return expression => uploads.TryGetValue(UploadBase(expression), out var upload)
            ? new() { HasReference = true, RawExpression = expression, ResolvedAbsolutePath = upload, SourceLabel = "Pending Story image" }
            : WccUnitPreviewPathResolver.ResolveRawImageExpression(source, WccSourceOrigin.ImportedExternal,
                expression, WccAssetReferenceKind.Image, install);
    }
}