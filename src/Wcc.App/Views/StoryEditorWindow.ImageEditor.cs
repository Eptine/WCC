using System;
using Avalonia.Controls;
using Wcc.App.ViewModels;
using Wcc.App.ViewModels.Wcc;
using Wcc.App.Views.Controls;
using Wcc.Core.References;
using Wcc.Core.Projects;

namespace Wcc.App.Views;

public partial class StoryEditorWindow
{
    private void AttachStoryImageEditor(TextBox box, StoryFieldVm field, Func<bool>? canEdit = null, string? define = null)
    {
        if (field.Key is not ("background" or "image" or "file")) return;
        WccImageEditor.Attach(box, () =>
        {
            if (_vm is not { } model) return null;
            var part = model.SelectedPart;
            var context = model.PreviewDefine;
            var value = define is null ? field.Text : field.Raw(define);
            var resources = model.CaptureImageEditorResources();
            return new ImageEditorFieldContext(model.Session.RuntimeInstallPath, resources,
                () => ReferenceEquals(model, _vm) && model.Session.IsCurrent && ReferenceEquals(part, model.SelectedPart) &&
                context == model.PreviewDefine && field.CanEdit && (canEdit?.Invoke() ?? true) &&
                value == (define is null ? field.Text : field.Raw(define)), true);
        }, () => true);
    }
}