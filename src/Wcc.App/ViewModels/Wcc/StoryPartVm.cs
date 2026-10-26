using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Wcc.Core.Scenarios;

namespace Wcc.App.ViewModels.Wcc;

/// <summary>Detached field state; the selected difficulty determines the editing scope.</summary>
public sealed class StoryFieldVm : ViewModelBase
{
    private readonly Func<string?> _scope;
    private readonly Func<string>? _displayContext;
    private readonly Action _changed;
    private readonly string[] _defines;
    private readonly Dictionary<string, string?> _values;
    private readonly Dictionary<string, bool> _translationDefaults = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _textInputProblems = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string?> _capturedTextContexts;
    private readonly IReadOnlyDictionary<string, string?>? _capturedDisplayValues;
    private bool _touched;
    public StoryFieldVm(string key, IEnumerable<string> defines, Func<string?> scope, Action changed,
        WccScenarioStoryFieldSnapshot? snapshot = null, WccScenarioStoryFieldSpec? pending = null, Func<string>? displayContext = null)
    {
        Key = key; _defines = defines.ToArray(); _scope = scope; _displayContext = displayContext; _changed = changed;
        _values = _defines.ToDictionary(d => d, d => snapshot?.RawValues.GetValueOrDefault(d), StringComparer.Ordinal);
        _capturedTextContexts = new(_values, StringComparer.Ordinal);
        CanEdit = snapshot?.CanEdit ?? true; Diagnostic = snapshot?.Diagnostic; OriginLabel = snapshot?.OriginLabel;
        if (!CanEdit && snapshot is not null && pending?.IsTouched != true)
            _capturedDisplayValues = new Dictionary<string, string?>(snapshot.Values, StringComparer.Ordinal);
        if (pending?.IsTouched == true)
        {
            _touched = true;
            foreach (var d in _defines) _values[d] = pending.Mode == WccScenarioDifficultyValueMode.Shared
                ? pending.SharedValue : pending.ContextValues.GetValueOrDefault(d);
            foreach (var d in _defines)
                if (WccWmlPlainTextCodec.TryReadLiteral(_values[d], out _, out var translated) && translated) _capturedTextContexts[d] = _values[d];
        }
    }
    public string Key { get; }
    internal int ContextCount => _defines.Length;
    public string? InputProblem => _textInputProblems.Values.FirstOrDefault();
    public bool CanEdit { get; }
    public string? Diagnostic { get; }
    public string? OriginLabel { get; }
    public bool IsIndependent => _values.Values.Distinct(StringComparer.Ordinal).Count() > 1;
    public string Diamond => IsIndependent ? "◆" : "◇";
    public string Text
    {
        get
        {
            if (_capturedDisplayValues is not null) return _capturedDisplayValues.GetValueOrDefault(DisplayDefine) ?? "";
            var raw = Raw(DisplayDefine);
            return IsPlainText && !_textInputProblems.ContainsKey(DisplayDefine) &&
                WccWmlPlainTextCodec.TryReadLiteral(raw, out var text, out _) ? text : raw ?? "";
        }
        set
        {
            if (!CanEdit) return;
            var scope = _scope();
            var targets = scope is null ? _defines : [scope];
            var problems = new Dictionary<string, string?>(StringComparer.Ordinal);
            var updates = targets.ToDictionary(define => define, define =>
            {
                if (string.IsNullOrWhiteSpace(value)) return null;
                if (!IsPlainText) return value;
                var previousRaw = EncodingContext(define);
                var translate = WccWmlPlainTextCodec.TryReadLiteral(previousRaw, out _, out var previous) ? previous : _translationDefaults.GetValueOrDefault(define, true);
                var context = translate ? previousRaw ?? _capturedTextContexts.GetValueOrDefault(define) : previousRaw;
                var accepted = WccWmlPlainTextCodec.TryEncodeInput(value, translate, context, out var encoded, out var problem);
                problems[define] = accepted ? null : problem;
                return accepted ? encoded : value;
            }, StringComparer.Ordinal);
            if (targets.All(d => _values.GetValueOrDefault(d) == updates[d] && _textInputProblems.GetValueOrDefault(d) == problems.GetValueOrDefault(d))) return;
            foreach (var d in targets)
            {
                _values[d] = updates[d];
                if (problems.GetValueOrDefault(d) is { } problem) _textInputProblems[d] = problem; else _textInputProblems.Remove(d);
                if (IsPlainText && WccWmlPlainTextCodec.TryReadLiteral(updates[d], out _, out var translated) && translated) _capturedTextContexts[d] = updates[d];
            }
            _touched = true; Notify(); _changed();
        }
    }
    public bool IsPlainText => Key is "story" or "title";
    private string DisplayDefine => _scope() ?? _displayContext?.Invoke() ?? _defines.FirstOrDefault() ?? "";
    private string? EncodingContext(string define) => _textInputProblems.ContainsKey(define)
        ? _capturedTextContexts.GetValueOrDefault(define) ?? Raw(define) : Raw(define);
    public bool Translate
    {
        get => WccWmlPlainTextCodec.TryReadLiteral(EncodingContext(DisplayDefine), out _, out var translated) ? translated : _translationDefaults.GetValueOrDefault(DisplayDefine, true);
        set
        {
            if (!CanEdit || !IsPlainText) return;
            var targets = _scope() is { } scope ? new[] { scope } : _defines;
            var changed = false;
            foreach (var define in targets)
            {
                if (WccWmlPlainTextCodec.TryReadLiteral(Raw(define), out var text, out var previous))
                {
                    if (previous == value) continue;
                    try
                    {
                        _values[define] = WccWmlPlainTextCodec.EncodePlainText(text, value,
                            value && !previous ? _capturedTextContexts.GetValueOrDefault(define) ?? Raw(define) : Raw(define));
                        _textInputProblems.Remove(define); _touched = true; changed = true;
                    }
                    catch (ArgumentException exception) { _textInputProblems[define] = exception.Message; changed = true; }
                }
                else if (Raw(define) is null && _translationDefaults.GetValueOrDefault(define, true) != value)
                { _translationDefaults[define] = value; changed = true; }
            }
            if (changed) { Notify(); _changed(); }
        }
    }
    public string? Raw(string define) => _values.GetValueOrDefault(define);
    public void SetForDefine(string define, string? value)
    {
        if (!CanEdit || !_values.ContainsKey(define) || _values[define] == value) return;
        _values[define] = value; _textInputProblems.Remove(define); _touched = true; Notify(); _changed();
    }
    public string? Decoded(string define) => _capturedDisplayValues is not null
        ? _capturedDisplayValues.GetValueOrDefault(define)
        : Raw(define) is { } value ? IsPlainText && !_textInputProblems.ContainsKey(define) && WccWmlPlainTextCodec.TryReadLiteral(value, out var text, out _)
            ? text : WccScenarioFieldValueCodec.DecodeLiteral(value) : null;
    public void MakeShared()
    {
        if (!CanEdit) return;
        var value = Raw(DisplayDefine); var problem = _textInputProblems.GetValueOrDefault(DisplayDefine);
        foreach (var define in _defines)
        {
            _values[define] = value;
            if (problem is null) _textInputProblems.Remove(define); else _textInputProblems[define] = problem;
        }
        _touched = true; Notify(); _changed();
    }
    public void Notify() { OnPropertyChanged(nameof(Text)); OnPropertyChanged(nameof(InputProblem)); OnPropertyChanged(nameof(Translate)); OnPropertyChanged(nameof(IsIndependent)); OnPropertyChanged(nameof(Diamond)); }
    public WccScenarioStoryFieldSpec ToSpec()
    {
        if (_textInputProblems.Values.FirstOrDefault() is { } problem)
            throw new InvalidOperationException("Story field '" + Key + "': " + problem);
        return new()
        {
            IsTouched = _touched, Mode = IsIndependent ? WccScenarioDifficultyValueMode.Independent : WccScenarioDifficultyValueMode.Shared,
            SharedValue = _values.Values.FirstOrDefault(), ContextValues = new Dictionary<string, string?>(_values, StringComparer.Ordinal)
        };
    }
}

public sealed class StoryChildVm : ViewModelBase
{
    private bool _batch;
    private readonly Action _changed;
    private readonly Dictionary<string, int> _initialDisplayOrders;
    private readonly IReadOnlyDictionary<string, int> _authoringOrders;
    public StoryChildVm(string tag, string? id, IEnumerable<string> defines, Func<string?> scope, Action changed,
        WccScenarioStoryChildSnapshot? snapshot = null, WccScenarioStoryChildSpec? pending = null, Func<string>? displayContext = null)
    {
        _changed = changed;
        Tag = tag; Id = id; NewId = pending?.NewChildId ?? (id is null ? Guid.NewGuid().ToString("N") : null); CanEdit = snapshot?.CanEdit ?? true;
        IsExpandedPreview = snapshot?.IsExpandedPreview ?? false; OriginLabel = snapshot?.OriginLabel; MacroName = pending?.TravelMacroName ?? snapshot?.MacroName;
        AvailableDefines = new HashSet<string>(pending?.AvailableDefines ?? snapshot?.AvailableDefines ?? defines, StringComparer.Ordinal);
        OrderByDefine = new Dictionary<string, int>(pending?.OrderByDefine ?? snapshot?.OrderByDefine ?? new Dictionary<string, int>(), StringComparer.Ordinal);
        _initialDisplayOrders = new(OrderByDefine, StringComparer.Ordinal);
        _authoringOrders = pending?.OrderByDefine ?? snapshot?.AuthoringOrderByDefine ?? _initialDisplayOrders;
        var keys = tag == "image" ? StoryPartVm.ImageKeys : StoryPartVm.LayerKeys;
        foreach (var key in keys) Fields.Add(new StoryFieldVm(key, defines, scope, () => { OnPropertyChanged(nameof(DisplayName)); if (!_batch) changed(); },
            snapshot?.Fields.GetValueOrDefault(key), pending?.Fields.GetValueOrDefault(key), displayContext));
    }
    public string Tag { get; }
    public string? Id { get; }
    public string? NewId { get; }
    public string LogicalId => Id ?? NewId!;
    public bool CanEdit { get; }
    public bool IsExpandedPreview { get; }
    public string? OriginLabel { get; }
    public string? MacroName { get; private set; }
    public HashSet<string> AvailableDefines { get; }
    public Dictionary<string, int> OrderByDefine { get; }
    public ObservableCollection<StoryFieldVm> Fields { get; } = [];
    public StoryFieldVm Field(string key) => Fields.First(f => f.Key == key);
    public void SetCoordinates(string x, string y)
    {
        _batch = true;
        try { Field("x").Text = x; Field("y").Text = y; }
        finally { _batch = false; }
        _changed();
    }
    public void SetCoordinatesForDefine(string define, string x, string y, Func<bool>? canContinue = null)
    {
        if (!CanEdit || !AvailableDefines.Contains(define) || canContinue?.Invoke() == false) return;
        var originalY = Field("y").Raw(define); var manualEdit = false;
        System.ComponentModel.PropertyChangedEventHandler xChanged = (_, args) =>
        { if (args.PropertyName == nameof(StoryFieldVm.Text) && Field("x").Raw(define) != x) manualEdit = true; };
        System.ComponentModel.PropertyChangedEventHandler yChanged = (_, args) =>
        { if (args.PropertyName == nameof(StoryFieldVm.Text) && Field("y").Raw(define) != originalY) manualEdit = true; };
        Field("x").PropertyChanged += xChanged; Field("y").PropertyChanged += yChanged;
        _batch = true;
        try
        {
            Field("x").SetForDefine(define, x);
            if (!manualEdit && canContinue?.Invoke() != false)
            { Field("y").PropertyChanged -= yChanged; Field("y").SetForDefine(define, y); }
        }
        finally { Field("x").PropertyChanged -= xChanged; Field("y").PropertyChanged -= yChanged; _batch = false; }
        _changed();
    }
    public string DisplayName => MacroName ?? (Tag == "background_layer" && WccStoryImageCanvas.IsReference(WccScenarioFieldValueCodec.DecodeLiteral(Field("image").Text))
        ? "Transparent background" : Field(Tag == "image" ? "file" : "image").Text is { Length: > 0 } path ? path : "New " + Tag);
    public WccScenarioStoryChildSpec ToSpec() => new() { OriginalChildId = Id, NewChildId = NewId, TravelMacroName = MacroName,
        AvailableDefines = AvailableDefines.ToArray(), OrderByDefine = SpecOrders(),
        Fields = Fields.Where(f => MacroName is null || f.Key is "x" or "y").ToDictionary(f => f.Key, f => f.ToSpec()) };
    private IReadOnlyDictionary<string, int>? SpecOrders()
    {
        var orders = OrderByDefine.Count == _initialDisplayOrders.Count && OrderByDefine.All(pair => _initialDisplayOrders.GetValueOrDefault(pair.Key, -1) == pair.Value) ? _authoringOrders : OrderByDefine;
        return orders.Count == 0 ? null : new Dictionary<string, int>(orders, StringComparer.Ordinal);
    }
}

public sealed class StoryPartVm : ViewModelBase
{
    private readonly Func<string?> _scope;
    private readonly Func<string>? _displayContext;
    private readonly Dictionary<string, int> _initialDisplayOrders;
    private readonly IReadOnlyDictionary<string, int> _authoringOrders;
    public static readonly string[] PartKeys = ["story", "text_alignment", "text_layout", "show_title", "title", "title_alignment", "title_position", "background", "scale_background", "scale_background_vertically", "scale_background_horizontally", "tile_background", "tile_background_vertically", "tile_background_horizontally", "keep_aspect_ratio", "music", "sound", "voice"];
    public static readonly string[] ImageKeys = ["file", "x", "y", "centered", "delay", "resize_with_background"];
    public static readonly string[] LayerKeys = ["image", "scale", "scale_vertically", "scale_horizontally", "keep_aspect_ratio", "tile", "tile_vertically", "tile_horizontally", "base_layer"];
    public StoryPartVm(IEnumerable<string> defines, Func<string?> scope, Action changed,
        WccScenarioStoryPartSnapshot? snapshot = null, WccScenarioStoryPartSpec? pending = null, Func<string>? displayContext = null)
    {
        _scope = scope; _displayContext = displayContext;
        Id = pending?.OriginalPartId ?? snapshot?.Id; NewId = pending?.NewPartId ?? (Id is null ? Guid.NewGuid().ToString("N") : null); RawText = snapshot?.RawText ?? "";
        IsExpandedPreview = snapshot?.IsExpandedPreview ?? false; OriginLabel = snapshot?.OriginLabel;
        CanEdit = snapshot?.CanEdit ?? true; Diagnostic = snapshot?.Diagnostic;
        AvailableDefines = new HashSet<string>(pending?.AvailableDefines ?? snapshot?.AvailableDefines ?? defines, StringComparer.Ordinal);
        OrderByDefine = new Dictionary<string, int>(pending?.OrderByDefine ?? snapshot?.OrderByDefine ?? new Dictionary<string, int>(), StringComparer.Ordinal);
        _initialDisplayOrders = new(OrderByDefine, StringComparer.Ordinal);
        _authoringOrders = pending?.OrderByDefine ?? snapshot?.AuthoringOrderByDefine ?? _initialDisplayOrders;
        foreach (var key in PartKeys) Fields.Add(new StoryFieldVm(key, defines, scope, () => { Refresh(); changed(); },
            snapshot?.Fields.GetValueOrDefault(key), pending?.Fields.GetValueOrDefault(key), displayContext));
        void LoadChildren(string tag, IReadOnlyList<WccScenarioStoryChildSnapshot> old, IReadOnlyList<WccScenarioStoryChildSpec>? edits, ObservableCollection<StoryChildVm> target)
        {
            void ChildChanged() { Refresh(); changed(); }
            if (edits is null) foreach (var child in old) target.Add(new(tag, child.Id, defines, scope, ChildChanged, child, displayContext: displayContext));
            else
            {
                foreach (var child in edits) target.Add(new(tag, child.OriginalChildId, defines, scope, ChildChanged, old.FirstOrDefault(c => c.Id == child.OriginalChildId), child, displayContext));
                foreach (var child in old.Where(c => c.IsExpandedPreview)) target.Add(new(tag, child.Id, defines, scope, ChildChanged, child, displayContext: displayContext));
            }
        }
        LoadChildren("image", snapshot?.Images ?? [], pending?.Images, Images);
        LoadChildren("background_layer", snapshot?.BackgroundLayers ?? [], pending?.BackgroundLayers, BackgroundLayers);
    }
    public string? Id { get; }
    public string? NewId { get; }
    public string LogicalId => Id ?? NewId!;
    public string RawText { get; }
    public bool CanEdit { get; }
    public string? Diagnostic { get; }
    public bool IsExpandedPreview { get; }
    public string? OriginLabel { get; }
    public HashSet<string> AvailableDefines { get; }
    public Dictionary<string, int> OrderByDefine { get; }
    public ObservableCollection<StoryFieldVm> Fields { get; } = [];
    public ObservableCollection<StoryChildVm> Images { get; } = [];
    public ObservableCollection<StoryChildVm> BackgroundLayers { get; } = [];
    public StoryFieldVm Field(string key) => Fields.First(f => f.Key == key);
    public string DisplayName
    {
        get
        {
            var define = _scope() ?? _displayContext?.Invoke();
            if (define is null || !AvailableDefines.Contains(define)) define = AvailableDefines.FirstOrDefault();
            if (define is null) return "Hidden slide";
            var title = Field("title").Decoded(define); if (!string.IsNullOrWhiteSpace(title)) return title;
            var text = Field("story").Decoded(define)?.Replace('\n', ' ');
            return string.IsNullOrEmpty(text) ? "Map / image slide" : text[..Math.Min(45, text.Length)];
        }
    }
    public string Diamond => Fields.Any(f => f.IsIndependent) || OrderByDefine.Values.Distinct().Count() > 1 ||
        AvailableDefines.Count != Fields.First().ContextCount ||
        Images.Concat(BackgroundLayers).Any(c => c.AvailableDefines.Count != AvailableDefines.Count || c.Fields.Any(f => f.IsIndependent) || c.OrderByDefine.Values.Distinct().Count() > 1) ? "◆" : "◇";
    public void Refresh() { OnPropertyChanged(nameof(DisplayName)); OnPropertyChanged(nameof(Diamond)); foreach (var f in Fields) f.Notify(); }
    public WccScenarioStoryPartSpec ToSpec() => new()
    {
        OriginalPartId = Id, NewPartId = NewId, AvailableDefines = AvailableDefines.ToArray(), OrderByDefine = SpecOrders(),
        Fields = Fields.ToDictionary(f => f.Key, f => f.ToSpec()), Images = Images.Where(c => !c.IsExpandedPreview).Select(c => c.ToSpec()).ToArray(), BackgroundLayers = BackgroundLayers.Where(c => !c.IsExpandedPreview).Select(c => c.ToSpec()).ToArray()
    };
    private IReadOnlyDictionary<string, int>? SpecOrders()
    {
        var orders = OrderByDefine.Count == _initialDisplayOrders.Count && OrderByDefine.All(pair => _initialDisplayOrders.GetValueOrDefault(pair.Key, -1) == pair.Value) ? _authoringOrders : OrderByDefine;
        return orders.Count == 0 ? null : new Dictionary<string, int>(orders, StringComparer.Ordinal);
    }
}
