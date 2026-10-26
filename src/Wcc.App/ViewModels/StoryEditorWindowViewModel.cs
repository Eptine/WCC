using System;
using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Wcc.App.ViewModels.Wcc;
using Wcc.Core.Scenarios;
using Wcc.Core.References;
using Wcc.Core.Projects;

namespace Wcc.App.ViewModels;

public sealed partial class StoryEditorWindowViewModel : ViewModelBase, IDisposable
{
    public StoryEditorWindowViewModel(WccScenarioStoryEditingSession session)
    {
        Session = session;
        Contexts.Add(new("", "", "All difficulties"));
        foreach (var context in session.Snapshot.Contexts) Contexts.Add(context);
        _selectedContext = Contexts[0];
        var defines = session.Snapshot.Contexts.Select(c => c.Define).ToArray();
        if (session.PendingEdit is { } pending)
        {
            foreach (var part in pending.Parts) Parts.Add(new(defines, () => Scope, Changed,
                session.Snapshot.DisplayParts.FirstOrDefault(p => p.Id == part.OriginalPartId), part, () => PreviewDefine));
            foreach (var part in session.Snapshot.DisplayParts.Where(p => p.IsExpandedPreview)) Parts.Add(new(defines, () => Scope, Changed, part, displayContext: () => PreviewDefine));
            _uploads.AddRange(pending.Uploads);
            foreach (var upload in pending.Uploads) { _uploadPreviewPaths[upload.RelativeReference] = upload.SourcePath; _uploadPreviewIdentities[upload.RelativeReference] = upload.SourcePath + ":" + upload.SourceRevision.RawByteSha256; }
        }
        else foreach (var part in session.Snapshot.DisplayParts) Parts.Add(new(defines, () => Scope, Changed, part, displayContext: () => PreviewDefine));
        InitializeImageCanvasTracking();
        _selectedPart = SidebarSelectableParts.FirstOrDefault();
        foreach (var context in session.Snapshot.Contexts)
        {
            var active = Parts.Where(p => p.AvailableDefines.Contains(context.Define))
                .OrderBy(p => p.OrderByDefine.GetValueOrDefault(context.Define, Parts.IndexOf(p))).ToArray();
            if (active.Length < 2) continue;
            var title = active[0].Field("title").Raw(context.Define);
            if (title is not null && active.All(p => p.Field("title").Raw(context.Define) == title &&
                p.Field("show_title").Raw(context.Define)?.TrimStart().StartsWith('_') != true &&
                p.Field("show_title").Decoded(context.Define) is "yes" or "true")) _titleFollowingDefines.Add(context.Define);
        }
        Status = string.Join(" ", new[] { session.Snapshot.Diagnostic, session.Snapshot.PreviewDiagnostic }.Where(message => !string.IsNullOrWhiteSpace(message)));
    }
    public WccScenarioStoryEditingSession Session { get; }
    public ObservableCollection<WccScenarioStoryContext> Contexts { get; } = [];
    public ObservableCollection<StoryPartVm> Parts { get; } = [];
    private WccScenarioStoryContext _selectedContext;
    public WccScenarioStoryContext SelectedContext
    {
        get => _selectedContext;
        set
        {
            if (value is null || !Contexts.Contains(value) || !SetField(ref _selectedContext, value)) return;
            EnsureVisiblePartSelection();
            foreach (var part in Parts) part.Refresh();
            OnPropertyChanged(nameof(Scope)); OnPropertyChanged(nameof(PreviewDefine)); OnPropertyChanged(nameof(ShowTitleOnAll));
            NotifyDraftChanged();
        }
    }
    public string? Scope => string.IsNullOrEmpty(SelectedContext.Define) ? null : SelectedContext.Define;
    public string PreviewDefine => Scope ?? Session.Snapshot.Contexts.FirstOrDefault(c => SelectedPart?.AvailableDefines.Contains(c.Define) == true)?.Define ?? Session.Snapshot.Contexts.First().Define;
    public IReadOnlyList<StoryPartVm> OrderedParts => Parts.OrderBy(p => p.OrderByDefine.GetValueOrDefault(PreviewDefine, Parts.IndexOf(p))).ToArray();
    private StoryPartVm? _selectedPart;
    public StoryPartVm? SelectedPart
    {
        get => _selectedPart;
        set
        {
            if (value is not null && (!Parts.Contains(value) || Scope is { } scope && !value.AvailableDefines.Contains(scope))) return;
            if (!SetField(ref _selectedPart, value)) return;
            foreach (var part in Parts) part.Refresh();
            OnPropertyChanged(nameof(PreviewDefine)); NotifyDraftChanged();
        }
    }
    private string? _status;
    public string? Status { get => _status; set => SetField(ref _status, value); }
    private readonly HashSet<string> _titleFollowingDefines = new(StringComparer.Ordinal);
    public bool ShowTitleOnAll
    {
        get => Scope is { } scope ? _titleFollowingDefines.Contains(scope) : Session.Snapshot.Contexts.All(c => _titleFollowingDefines.Contains(c.Define));
        set
        {
            var targets = Session.Snapshot.Contexts.Select(c => c.Define).Where(d => Scope is null || Scope == d).ToArray();
            foreach (var define in targets)
            {
                if (value) _titleFollowingDefines.Add(define); else _titleFollowingDefines.Remove(define);
            }
            OnPropertyChanged(nameof(ShowTitleOnAll));
            if (value) foreach (var define in targets)
                Parts.Where(p => p.AvailableDefines.Contains(define)).OrderBy(p => p.OrderByDefine.GetValueOrDefault(define, Parts.IndexOf(p))).FirstOrDefault()?.Field("show_title").SetForDefine(define, "yes");
            Changed();
        }
    }
    public bool CanEdit => Session.Snapshot.CanEdit && Session.IsCurrent;
    public bool CanEditTopology => CanEdit && Session.Snapshot.SupportsTopologyEditing;
    public bool CanAddLocalBackgroundLayers => CanEdit && Session.Snapshot.CanAddLocalBackgroundLayers;
    public bool CanEditTravelMarkers => CanEdit && Session.Snapshot.CanEditTravelMarkers;
    public StoryChildVm? AddTravelPreset(string macro)
    {
        if (!CanEditTravelMarkers || SelectedPart is not { CanEdit: true } part || !WccScenarioStoryPlanner.SupportedTravelMacros.Contains(macro, StringComparer.Ordinal)) return null;
        var available = part.AvailableDefines.Where(d => Scope is null || Scope == d).ToArray();
        var pending = NewCenteredImageDefaults() with { TravelMacroName = macro, AvailableDefines = available };
        var image = new StoryChildVm("image", null, Session.Snapshot.Contexts.Select(c => c.Define), () => Scope,
            () => { part.Refresh(); Changed(); }, pending: pending, displayContext: () => PreviewDefine);
        part.Images.Add(image); TrackNewImageCenter(part, image); Changed(); return image;
    }
    public bool TitleLocked => ShowTitleOnAll && SelectedPart != OrderedParts.FirstOrDefault(p => p.AvailableDefines.Contains(PreviewDefine));
    public event EventHandler? DraftChanged;
    public long DraftVersion { get; private set; }
    private bool _propagating;
    public void Changed()
    {
        if (_propagating || _addingImageCanvas) return;
        EnsureImageCoordinateCanvases();
        PropagateTitle(); NotifyDraftChanged();
    }
    private void NotifyDraftChanged() { DraftVersion++; OnPropertyChanged(nameof(TitleLocked)); OnPropertyChanged(nameof(OrderedParts)); NotifySlideNavigation(); DraftChanged?.Invoke(this, EventArgs.Empty); }
    private void PropagateTitle()
    {
        if (_titleFollowingDefines.Count == 0 || _propagating) return;
        _propagating = true;
        try
        {
            foreach (var define in _titleFollowingDefines)
            {
                var ordered = Parts.Where(p => p.AvailableDefines.Contains(define)).OrderBy(p => p.OrderByDefine.GetValueOrDefault(define, Parts.IndexOf(p))).ToArray();
                if (ordered.FirstOrDefault() is not { } first) continue;
                foreach (var p in ordered.Skip(1).Where(p => p.CanEdit)) foreach (var key in new[] { "title", "show_title" }) p.Field(key).SetForDefine(define, first.Field(key).Raw(define));
            }
        }
        finally { _propagating = false; }
    }
    public void AddPart()
    {
        if (!CanEditTopology) return;
        var anchor = SelectedPart;
        var reference = PartsForDefine(PreviewDefine);
        var targets = Session.Snapshot.Contexts.Select(context => context.Define).Where(define => Scope is null || Scope == define).ToArray();
        var before = targets.ToDictionary(define => define, PartsForDefine, StringComparer.Ordinal);
        var part = new StoryPartVm(Session.Snapshot.Contexts.Select(c => c.Define), () => Scope, Changed, displayContext: () => PreviewDefine);
        if (Scope is { } scope) { part.AvailableDefines.Clear(); part.AvailableDefines.Add(scope); }
        part.Field("story").Text = "_ \"New story text\"";
        Parts.Insert(anchor is not null ? Parts.IndexOf(anchor) + 1 : Parts.Count, part);
        foreach (var define in targets)
        {
            var ordered = before[define].ToList();
            ordered.Insert(PartInsertionIndex(ordered, reference, anchor), part);
            SetPartOrders(define, ordered);
        }
        SelectedPart = part; Changed();
    }
    public void RemoveSelectedPart()
    {
        if (!CanRemovePart || SelectedPart is not { } part) return;
        var oldRows = SidebarSelectableParts;
        var oldIndex = Array.IndexOf(oldRows.ToArray(), part);
        var targets = Session.Snapshot.Contexts.Select(context => context.Define)
            .Where(define => part.AvailableDefines.Contains(define) && (Scope is null || Scope == define)).ToArray();
        var before = targets.ToDictionary(define => define, PartsForDefine, StringComparer.Ordinal);
        foreach (var define in targets) { part.AvailableDefines.Remove(define); part.OrderByDefine.Remove(define); }
        if (part.AvailableDefines.Count == 0) Parts.Remove(part);
        foreach (var define in targets) SetPartOrders(define, before[define].Where(item => !ReferenceEquals(item, part)).ToArray());
        var remaining = SidebarSelectableParts;
        SelectedPart = remaining.ElementAtOrDefault(Math.Min(Math.Max(oldIndex, 0), remaining.Count - 1));
        foreach (var item in Parts) item.Refresh(); Changed();
    }
    public void MoveSelectedPart(int delta)
    {
        if (SelectedPart is not { } selected || PartMoveTarget(delta) is not { } target) return;
        var direction = Math.Sign(delta);
        foreach (var define in PartMoveDefines(selected, target))
        {
            var ordered = PartsForDefine(define).ToArray();
            var first = Array.IndexOf(ordered, selected); var second = Array.IndexOf(ordered, target);
            if (direction < 0 ? second >= first : second <= first) continue;
            (ordered[first], ordered[second]) = (ordered[second], ordered[first]);
            SetPartOrders(define, ordered);
        }
        if (Scope is null) Parts.Move(Parts.IndexOf(selected), Parts.IndexOf(target));
        foreach (var part in Parts) part.Refresh(); Changed();
    }
    public StoryChildVm? AddChild(string tag)
    {
        if (!(tag == "background_layer" ? CanAddLocalBackgroundLayers : CanEditTopology) || SelectedPart is not { CanEdit: true } p) return null;
        var child = new StoryChildVm(tag, null, Session.Snapshot.Contexts.Select(c => c.Define), () => Scope, () => { p.Refresh(); Changed(); },
            pending: tag == "image" ? NewCenteredImageDefaults() : null, displayContext: () => PreviewDefine);
        child.AvailableDefines.IntersectWith(p.AvailableDefines);
        if (Scope is { } scope) child.AvailableDefines.IntersectWith([scope]);
        if (tag == "background_layer")
            foreach (var define in child.AvailableDefines.Where(define => p.BackgroundLayers.Any(layer => layer.IsExpandedPreview && layer.AvailableDefines.Contains(define))))
                child.OrderByDefine[define] = p.BackgroundLayers.Count(layer => layer.AvailableDefines.Contains(define));
        (tag == "image" ? p.Images : p.BackgroundLayers).Add(child);
        if (tag == "image") TrackNewImageCenter(p, child);
        Changed(); return child;
    }
    public bool Apply()
    {
        try
        {
            if (!CanEdit) { Status = Session.CurrentProblem ?? Session.Snapshot.Diagnostic; return false; }
            if (_imageCanvasProblem is { } imageCanvasProblem) { Status = imageCanvasProblem; return false; }
            if (PendingNewImagePlacementProblem is { } placementProblem) { Status = placementProblem; return false; }
            foreach (var upload in _uploads)
                foreach (var context in Session.Snapshot.Contexts)
                    foreach (var part in Parts.Where(p => p.AvailableDefines.Contains(context.Define)))
                        foreach (var field in new[] { part.Field("background") }.Concat(part.Images.Concat(part.BackgroundLayers)
                            .Where(c => c.AvailableDefines.Contains(context.Define)).Select(c => c.Field(c.Tag == "image" ? "file" : "image"))))
                            if (WccImageExpressionParser.TryParse(field.Decoded(context.Define)) is { HasModifiers: true } expression && expression.BaseReference == upload.RelativeReference)
                            { Status = "Save this uploaded image with its plain path first, then reopen Story to add image modifiers."; return false; }
            var retainedUploads = _uploads.Where(u =>
            {
                var part = Parts.FirstOrDefault(p => p.LogicalId == u.Slot.PartId); if (part is null) return false;
                var child = part.Images.Concat(part.BackgroundLayers).FirstOrDefault(c => c.LogicalId == u.Slot.ChildId);
                var field = u.Slot.ChildId is null ? part.Field(u.Slot.FieldKey) : child?.Field(u.Slot.FieldKey);
                return field is not null && Session.Snapshot.Contexts.Any(c => (u.Slot.DifficultyDefine is null || u.Slot.DifficultyDefine == c.Define) && part.AvailableDefines.Contains(c.Define) &&
                    (child is null || child.AvailableDefines.Contains(c.Define)) && WccScenarioStoryAudioValues.ReferencesUpload(field.Raw(c.Define), u));
            }).ToArray();
            var edit = WccScenarioStoryPlanner.CreateEdit(Session.Snapshot, Parts.Where(p => !p.IsExpandedPreview).Select(p => p.ToSpec()), retainedUploads);
            if (!Session.Apply(edit)) { Status = Session.CurrentProblem ?? "The scenario changed. Reopen the Story editor."; return false; }
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { Status = ex.Message; return false; }
    }
    public void Dispose() { Disposed?.Invoke(this, EventArgs.Empty); Session.Dispose(); }
    public event EventHandler? Disposed;
    private readonly List<WccScenarioStoryUploadReceipt> _uploads = [];
    private readonly Dictionary<string, string> _uploadPreviewPaths = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _uploadPreviewIdentities = new(StringComparer.Ordinal);
    private static string UploadBase(string expression) => WccImageExpressionParser.TryParse(expression)?.BaseReference ?? expression;
    public string? PreviewUploadPath(string expression) => _uploadPreviewPaths.GetValueOrDefault(UploadBase(expression));
    public string? PreviewUploadIdentity(string expression) => _uploadPreviewIdentities.GetValueOrDefault(UploadBase(expression));
    public byte[]? PreviewUploadBytes(string expression) => _uploads.LastOrDefault(u => u.RelativeReference == UploadBase(expression))?.SourceRevision.CopyBytes();
    public WccScenarioStoryUploadReceipt CaptureUpload(WccScenarioStoryAssetSlot slot, string path,
        WccBinaryDiskRevision? expectedSourceRevision = null)
    {
        for (var ordinal = 1; ordinal <= 999; ordinal++)
        {
            var preferred = ordinal == 1 ? null : Path.GetFileNameWithoutExtension(path) + "_" + ordinal + Path.GetExtension(path);
            var receipt = Session.CaptureUpload(slot, path, preferred, expectedSourceRevision);
            if (!_uploads.Any(u => u.Slot != slot && string.Equals(u.DestinationPath, receipt.DestinationPath, StringComparison.OrdinalIgnoreCase) &&
                (u.SourceRevision.RawByteSha256 != receipt.SourceRevision.RawByteSha256 || u.SourceRevision.RouteIdentitySha256 != receipt.SourceRevision.RouteIdentitySha256 ||
                 u.SourceRevision.SourceIdentity != receipt.SourceRevision.SourceIdentity || u.SourceRevision.SourceHardLinkCount != receipt.SourceRevision.SourceHardLinkCount))) return receipt;
        }
        throw new InvalidOperationException("No distinct pending Story media filename is available.");
    }
    public void AddUpload(WccScenarioStoryUploadReceipt receipt, StoryFieldVm field, string externalPath, Action<string>? assignReference = null)
    {
        _uploads.RemoveAll(u => u.Slot == receipt.Slot); _uploads.Add(receipt);
        _uploadPreviewPaths[receipt.RelativeReference] = externalPath;
        _uploadPreviewIdentities[receipt.RelativeReference] = receipt.SourcePath + ":" + receipt.SourceRevision.RawByteSha256;
        if (assignReference is not null) assignReference(receipt.RelativeReference);
        else field.Text = "\"" + receipt.RelativeReference.Replace("\"", "\"\"") + "\"";
        Changed();
    }
}
