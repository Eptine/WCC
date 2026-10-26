using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Wcc.App.ViewModels.Wcc;
using Wcc.Core.Projects;
using Wcc.Core.References;
using Wcc.Core.Scenarios;

namespace Wcc.App.ViewModels;

public sealed partial class StoryEditorWindowViewModel
{
    // A picker may outlive a focus/preview notification. Its target slot and raw values,
    // rather than unrelated draft notifications, determine whether the result is current.
    private sealed class MediaTarget : IDisposable
    {
        private readonly StoryEditorWindowViewModel _owner;
        private readonly HashSet<string> _parts;
        private readonly HashSet<string>? _children;
        public MediaTarget(StoryEditorWindowViewModel owner, StoryPartVm part, StoryChildVm? child, StoryFieldVm field)
        {
            _owner = owner; Part = part; Child = child; Field = field; Scope = owner.Scope;
            Values = owner.Session.Snapshot.Contexts.ToDictionary(context => context.Define, context => field.Raw(context.Define), StringComparer.Ordinal);
            _parts = part.AvailableDefines.ToHashSet(StringComparer.Ordinal); _children = child?.AvailableDefines.ToHashSet(StringComparer.Ordinal);
            owner.PropertyChanged += SelectionChanged; owner.DraftChanged += DraftChanged; field.PropertyChanged += FieldChanged;
        }
        public StoryPartVm Part { get; }
        public StoryChildVm? Child { get; }
        public StoryFieldVm Field { get; }
        public string? Scope { get; }
        public IReadOnlyDictionary<string, string?> Values { get; }
        public bool Invalidated { get; private set; }
        private void SelectionChanged(object? sender, PropertyChangedEventArgs args)
        { if (args.PropertyName is nameof(SelectedPart) or nameof(SelectedContext)) Invalidated = true; }
        private void FieldChanged(object? sender, PropertyChangedEventArgs args)
        { if (args.PropertyName == nameof(StoryFieldVm.Text) && Values.Any(pair => Field.Raw(pair.Key) != pair.Value)) Invalidated = true; }
        private void DraftChanged(object? sender, EventArgs args)
        {
            if (!_owner.Parts.Contains(Part) || !_parts.SetEquals(Part.AvailableDefines) ||
                Child is not null && (!Part.Images.Concat(Part.BackgroundLayers).Contains(Child) || !_children!.SetEquals(Child.AvailableDefines)))
                Invalidated = true;
        }
        public void Dispose()
        { _owner.PropertyChanged -= SelectionChanged; _owner.DraftChanged -= DraftChanged; Field.PropertyChanged -= FieldChanged; }
    }

    private MediaTarget? CaptureMediaTarget(StoryPartVm part, StoryChildVm? child, StoryFieldVm field)
    {
        if (!CanEdit || !part.CanEdit || !field.CanEdit || !ReferenceEquals(part, SelectedPart) ||
            !Parts.Contains(part) || child is not null && (!child.CanEdit || !part.Images.Concat(part.BackgroundLayers).Contains(child)) ||
            !ReferenceEquals(child is null ? part.Field(field.Key) : child.Field(field.Key), field)) return null;
        return new(this, part, child, field);
    }
    private bool IsCurrentMediaTarget(MediaTarget target, Func<bool>? hostIsCurrent)
    {
        if (hostIsCurrent?.Invoke() == false) { Status = "The Story window changed while choosing media. Reopen the picker."; return false; }
        if (!Session.IsCurrent) { Status = Session.CurrentProblem; return false; }
        if (target.Invalidated || !CanEdit || !ReferenceEquals(SelectedPart, target.Part) || !Parts.Contains(target.Part) || Scope != target.Scope ||
            target.Child is not null && !target.Part.Images.Concat(target.Part.BackgroundLayers).Contains(target.Child) ||
            target.Values.Any(pair => target.Field.Raw(pair.Key) != pair.Value))
        { Status = "The selected slide, difficulty or media field changed while the picker was open. Choose the media again."; return false; }
        return true;
    }

    public async Task<bool> PickMediaFileAsync(StoryPartVm part, StoryChildVm? child, StoryFieldVm field,
        Func<Task<string?>> choose, Func<bool>? hostIsCurrent = null)
    {
        if (CaptureMediaTarget(part, child, field) is not { } target) return false;
        using var targetLifetime = target;
        try
        {
            var path = await choose();
            if (path is null) return false;
            if (!IsCurrentMediaTarget(target, hostIsCurrent)) return false;
            SetMediaFile(target, path, allowDirectReference: true);
            return true;
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException)
        { Status = error.Message; return false; }
    }

    public async Task<bool> PickAudioAsync(StoryPartVm part, StoryFieldVm field,
        Func<CatalogPickerWindowViewModel, Task<CatalogPickerSelection?>> choose, Func<bool>? hostIsCurrent = null)
    {
        if (Session.AudioCatalog is not { } catalog || CaptureMediaTarget(part, null, field) is not { } target) return false;
        using var targetLifetime = target;
        try
        {
            var choices = await Task.Run(() => catalog.Build(field.Key));
            if (!IsCurrentMediaTarget(target, hostIsCurrent)) return false;
            var picker = new CatalogPickerWindowViewModel(choices.Select(choice => choice.Option), field.Decoded(PreviewDefine), false,
                "Choose Story " + field.Key, "Core and campaign resources stay linked. WCC and Core Campaign resources are copied when the scenario is saved.");
            var selection = await choose(picker);
            if (selection is null) return false;
            if (!IsCurrentMediaTarget(target, hostIsCurrent)) return false;
            var choice = choices.SingleOrDefault(choice => choice.Option.ToSelection() == selection)
                ?? throw new InvalidOperationException("The audio selection does not belong to this captured resource picker.");
            var revision = choice.Capture();
            SetMediaFile(target, choice.SourcePath, choice.IsDirectReference, revision, choice.Option.ValueId);
            return true;
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException)
        { Status = error.Message; return false; }
    }

    private void SetMediaFile(MediaTarget target, string path, bool allowDirectReference,
        WccBinaryDiskRevision? sourceRevision = null, string? logicalReference = null,
        string? audioEntryId = null, Action<string>? assignReference = null)
    {
        path = Path.GetFullPath(path);
        var key = target.Field.Key;
        var folder = key == "music" ? "music" : key is "sound" or "voice" ? "sounds" : "images";
        var kind = folder == "music" ? WccAssetReferenceKind.Music : folder == "sounds" ? WccAssetReferenceKind.Sound : WccAssetReferenceKind.Image;
        var roots = new[] { Path.Combine(Session.ProjectRootPath, folder),
            Session.SourceInstallPath is { } install ? Path.Combine(WccCoreInstallPaths.GetCoreDataRoot(install), folder) : null,
            Session.SourceInstallPath is { } game ? Path.Combine(game, "data", folder) : null };
        if (allowDirectReference)
            foreach (var root in roots.Where(root => root is not null))
            {
                var relative = Path.GetRelativePath(root!, path).Replace('\\', '/');
                if (relative == ".." || relative.StartsWith("../", StringComparison.Ordinal) || Path.IsPathRooted(relative)) continue;
                var reference = logicalReference ?? relative;
                var resolved = WccAssetPathResolver.ResolveReference(Session.SourcePath, WccSourceOrigin.ImportedExternal,
                    new WccAssetReference { Kind = kind, OwnerBlockKind = "story_part", SourceFieldKey = key,
                        LookupPath = reference, RawValue = reference }, Session.SourceInstallPath).ResolvedAbsolutePath;
                if (resolved is null || !string.Equals(Path.GetFullPath(resolved), path,
                    OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                    throw new InvalidOperationException("This media name resolves to a different resource in the campaign. Choose a distinct resource or upload an external copy.");
                if (assignReference is not null) assignReference(reference);
                else target.Field.Text = "\"" + reference.Replace("\"", "\"\"") + "\"";
                Status = "Media selected. Apply, then save the scenario to write the Story changes.";
                return;
            }
        var upload = CaptureUpload(new(target.Part.LogicalId, target.Child?.LogicalId, key, target.Scope, audioEntryId), path, sourceRevision);
        AddUpload(upload, target.Field, path, assignReference);
        Status = "Media staged. Apply, then save the scenario to copy it into the campaign.";
    }
}
