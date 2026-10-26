using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using Wcc.App.ViewModels;
using Wcc.App.ViewModels.Wcc;
using Wcc.App.Views.Controls;
using Wcc.Core.Scenarios;
using Wcc.Core.Projects;
using Wcc.Core.References;

namespace Wcc.App.Views;

public partial class StoryEditorWindow : Window
{
    private StoryEditorWindowViewModel? _vm;
    private readonly StorySlidePreview _preview = new();
    private readonly Dictionary<StoryPartVm, Image> _thumbnails = [];
    private StoryChildVm? _selectedImage;
    private StoryChildVm? _selectedLayer;
    private readonly Dictionary<string, Image> _travelThumbnails = [];
    private bool _sync;
    private long _hostEpoch;
    public StoryEditorWindow()
    {
        InitializeComponent(); PreviewHost.Child = _preview;
        DataContextChanged += (_, _) => Attach();
        ContextPicker.SelectionChanged += (_, _) =>
        {
            if (_sync || _vm is null || ContextPicker.SelectedItem is not WccScenarioStoryContext context) return;
            _vm.SelectedContext = context; ResetElementSelection(); BuildSlides(); BuildInspector();
        };
        _preview.ImageSelected += (_, _) => { _selectedImage = _preview.SelectedImage; if (_selectedImage is not null) { _selectedLayer = null; _legacyBackgroundSelected = false; _activeTab = 1; } BuildInspector(); };
        _preview.AssetsLoaded += (_, _) => { foreach (var (part, image) in _thumbnails) image.Source = _preview.Thumbnail(part); foreach (var (macro, image) in _travelThumbnails) image.Source = _preview.TravelThumbnail(macro); PreviewStatus.Text = "Static scale 1 preview · no timed/audio playback. " + _preview.PreviewDiagnostic; };
        Closed += (_, _) => { _hostEpoch++; if (_vm is not null) { _vm.PropertyChanged -= VmChanged; _vm.DraftChanged -= DraftChanged; } _preview.Dispose(); };
    }
    private void Attach()
    {
        _hostEpoch++;
        if (_vm is not null) { _vm.PropertyChanged -= VmChanged; _vm.DraftChanged -= DraftChanged; }
        _vm = DataContext as StoryEditorWindowViewModel; if (_vm is null) return;
        _vm.PropertyChanged += VmChanged; _vm.DraftChanged += DraftChanged;
        _sync = true; ContextPicker.ItemsSource = _vm.Contexts; ContextPicker.ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<WccScenarioStoryContext>((c, _) => new TextBlock { Text = c?.DisplayName }); ContextPicker.SelectedItem = _vm.SelectedContext; _sync = false;
        _preview.Attach(_vm); BuildSlides(); BuildInspector(); StatusLabel.Text = _vm.Status;
    }
    private void VmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(StoryEditorWindowViewModel.Status)) StatusLabel.Text = _vm?.Status;
    }
    private void DraftChanged(object? sender, EventArgs e)
    {
        if (_vm is null) return;
        foreach (var (part, image) in _thumbnails) image.Source = _preview.Thumbnail(part);
        RefreshSlideActions(); // Existing field controls keep focus while preview and bound labels update.
    }
    private Button ActionButton(string text, Action action, bool enabled = true)
    {
        var button = new Button { Content = text, IsEnabled = enabled, Padding = new Thickness(8, 5) };
        button.Click += (_, _) => action(); return button;
    }
    private void BuildSlides() => BuildSlideSidebar();
    private void BuildInspector() => BuildMockInspector();
    private void MoveChild(StoryChildVm child, int delta)
    {
        if (_vm?.CanEditTopology != true || _vm.SelectedPart is not { CanEdit: true } part || !child.CanEdit) return;
        var children = child.Tag == "image" ? part.Images : part.BackgroundLayers;
        var reference = children.Where(c => c.AvailableDefines.Contains(_vm.PreviewDefine))
            .OrderBy(c => c.OrderByDefine.GetValueOrDefault(_vm.PreviewDefine, children.IndexOf(c))).ToArray();
        var referenceIndex = Array.IndexOf(reference, child);
        if (referenceIndex < 0 || referenceIndex + delta < 0 || referenceIndex + delta >= reference.Length) return;
        var target = reference[referenceIndex + delta];
        foreach (var scope in _vm.Session.Snapshot.Contexts.Select(c => c.Define).Where(d => _vm.Scope is null || _vm.Scope == d))
        {
            var ordered = children.Where(c => c.AvailableDefines.Contains(scope)).OrderBy(c => c.OrderByDefine.GetValueOrDefault(scope, children.IndexOf(c))).ToArray();
            var index = Array.IndexOf(ordered, child); var next = Array.IndexOf(ordered, target);
            if (index < 0 || next < 0 || (delta < 0 ? next >= index : next <= index)) continue;
            (ordered[index], ordered[next]) = (ordered[next], ordered[index]);
            for (var i = 0; i < ordered.Length; i++) ordered[i].OrderByDefine[scope] = i;
        }
        part.Refresh(); _vm.Changed(); BuildInspector();
    }
    private void AddField(StackPanel target, StoryFieldVm field, string label, string[]? choices = null, bool multiline = false, Action? pick = null, bool locked = false)
    {
        var enabled = _vm?.CanEdit == true && _vm.SelectedPart?.CanEdit == true && field.CanEdit && !locked;
        var section = new StackPanel { Spacing = 4 };
        var header = new StackPanel { Name = "StoryLabel_" + field.Key, Orientation = Orientation.Horizontal, Spacing = 4 };
        header.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center });
        header.Children.Add(CreateStoryFieldDifficultyButton(field, () => FieldDifficulties(field), enabled));
        section.Children.Add(header);
        if (choices is not null)
        {
            var wrap = new WrapPanel { Orientation = Orientation.Horizontal }; var group = Guid.NewGuid().ToString("N");
            var radios = new List<(string Value, RadioButton Radio)>(); var syncing = false;
            foreach (var choice in choices)
            {
                var radio = new RadioButton { Content = choice, GroupName = group, IsEnabled = enabled, Margin = new Thickness(0, 2, 9, 2) };
                void SelectChoice() { if (!syncing && enabled && radio.IsChecked == true) field.Text = choice.Contains(',') ? "\"" + choice + "\"" : choice; }
                radio.IsCheckedChanged += (_, _) => SelectChoice();
                radio.Click += (_, _) => SelectChoice();
                radios.Add((choice, radio)); wrap.Children.Add(radio);
            }
            var status = new TextBlock { Opacity = 0.6, FontSize = 11, TextWrapping = TextWrapping.Wrap };
            ObserveStoryField(section, field, () =>
            {
                syncing = true;
                try
                {
                    var raw = StoryLiteral(field.Text); var value = StoryChoiceValue(field, choices);
                    foreach (var (choice, radio) in radios)
                    {
                        radio.IsChecked = choice == value;
                        radio.Opacity = !field.CanEdit || string.IsNullOrEmpty(raw) && radio.IsChecked == true ? 0.6 : 1;
                    }
                    status.Text = string.IsNullOrEmpty(raw) && value is not null ? "Default: " + value : "";
                    status.IsVisible = !string.IsNullOrEmpty(status.Text);
                }
                finally { syncing = false; }
            });
            var reset = SmallAction("Reset", "Reset " + label + " to its default", () => { field.Text = ""; }, enabled);
            WccButtonIcon.SetAction(reset, WccActionIcon.Reset); wrap.Children.Add(reset); section.Children.Add(wrap); section.Children.Add(status);
        }
        else
        {
            var watermark = field.Key == "title" && _vm is { } model ? model.Session.GetScenarioTitle(model.PreviewDefine) : null;
            var box = new TextBox { AcceptsReturn = multiline, TextWrapping = multiline ? TextWrapping.Wrap : TextWrapping.NoWrap, MinHeight = multiline ? 170 : 0,
                Watermark = string.IsNullOrEmpty(watermark) ? "Inherited / default" : watermark, IsReadOnly = !enabled, Opacity = field.CanEdit ? 1 : 0.65 };
            box.Bind(TextBox.TextProperty, new Binding(nameof(StoryFieldVm.Text)) { Source = field, Mode = BindingMode.TwoWay }); section.Children.Add(box);
            if (pick is not null)
            {
                var picker = ActionButton("Choose / upload image…", pick, enabled);
                WccButtonIcon.SetAction(picker, WccActionIcon.Browse); WccButtonIcon.SetShowLabel(picker, true);
                ToolTip.SetTip(picker, "Choose an image file to copy into this campaign when saved"); section.Children.Add(picker);
            }
        }
        AddStoryFieldOrigin(section, field); target.Children.Add(section);
    }
    private async void FieldDifficulties(StoryFieldVm field)
    {
        if (_vm is null) return;
        var content = new StackPanel { Margin = new Thickness(16), Spacing = 10 };
        content.Children.Add(new TextBlock { Text = "Edit this field separately for each difficulty. Empty uses the WML default.", TextWrapping = TextWrapping.Wrap });
        var window = new Window { Title = field.Key + " · difficulties", Width = 520, Height = 420, Content = new ScrollViewer { Content = content } };
        foreach (var context in _vm.Session.Snapshot.Contexts)
        {
            content.Children.Add(new TextBlock { Text = context.DisplayName }); var box = new TextBox { Text = field.Raw(context.Define), AcceptsReturn = field.Key == "story", TextWrapping = TextWrapping.Wrap };
            box.TextChanged += (_, _) => field.SetForDefine(context.Define, string.IsNullOrWhiteSpace(box.Text) ? null : box.Text); content.Children.Add(box);
        }
        var apply = ActionButton("Use displayed value for all difficulties", () => { field.MakeShared(); window.Close(); BuildInspector(); });
        WccButtonIcon.SetAction(apply, WccActionIcon.Accept); WccButtonIcon.SetShowLabel(apply, true); content.Children.Add(apply);
        var done = ActionButton("Done", () => { window.Close(); BuildInspector(); });
        WccButtonIcon.SetAction(done, WccActionIcon.Close); WccButtonIcon.SetShowLabel(done, true); content.Children.Add(done); await window.ShowDialog(this);
    }
    private async void Availability(HashSet<string> availability, HashSet<string>? parent, Action changed)
    {
        if (_vm is null) return;
        var content = new StackPanel { Margin = new Thickness(16), Spacing = 10 }; var window = new Window { Title = "Visible in difficulties", Width = 350, Height = 350, Content = content };
        foreach (var context in _vm.Session.Snapshot.Contexts)
        {
            var check = new CheckBox { Content = context.DisplayName, IsChecked = availability.Contains(context.Define) && (parent?.Contains(context.Define) ?? true), IsEnabled = parent?.Contains(context.Define) ?? true };
            check.IsCheckedChanged += (_, _) =>
            {
                if (check.IsChecked == true) availability.Add(context.Define);
                else if (availability.Count > 1) availability.Remove(context.Define);
                else { check.IsChecked = true; return; }
                changed();
            }; content.Children.Add(check);
        }
        content.Children.Add(new TextBlock { Text = "Keep at least one difficulty selected. Remove the item to delete it.", TextWrapping = TextWrapping.Wrap, Opacity = 0.7 });
        var done = ActionButton("Done", () => { window.Close(); BuildInspector(); });
        WccButtonIcon.SetAction(done, WccActionIcon.Close); WccButtonIcon.SetShowLabel(done, true); content.Children.Add(done); await window.ShowDialog(this);
    }
    private async void PickImage(StoryPartVm part, StoryChildVm? child, StoryFieldVm field)
    {
        if (_vm is not { } model) return;
        var hostEpoch = _hostEpoch;
        if (await model.PickMediaFileAsync(part, child, field, async () =>
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "Choose a Story image", AllowMultiple = false,
                FileTypeFilter = [new FilePickerFileType("Wesnoth images") { Patterns = ["*.png", "*.webp", "*.jpg", "*.jpeg", "*.bmp", "*.gif"] }] });
            if (files.FirstOrDefault() is not { } file) return null;
            return file.TryGetLocalPath() ?? throw new InvalidOperationException("Choose an image stored on this computer.");
        }, () => hostEpoch == _hostEpoch && ReferenceEquals(model, _vm))) _preview.Refresh();
    }
    private void AddAudioControls(StackPanel audio, StoryPartVm part)
    {
        if (_vm is null) return;
        foreach (var key in new[] { "music", "sound", "voice" })
        {
            var field = part.Field(key); AddField(audio, field, key);
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            var enabled = _vm.CanEdit && part.CanEdit && field.CanEdit;
            var choose = ActionButton("Choose resource…", () => PickAudio(part, field), enabled && _vm.Session.AudioCatalog is not null);
            WccButtonIcon.SetAction(choose, WccActionIcon.Select); ToolTip.SetTip(choose, "Choose " + key + " from the resource catalog"); row.Children.Add(choose);
            var upload = ActionButton("Upload audio…", () => UploadAudio(part, field), enabled);
            WccButtonIcon.SetAction(upload, WccActionIcon.Browse); WccButtonIcon.SetShowLabel(upload, true);
            ToolTip.SetTip(upload, "Choose an audio file to copy into this campaign when saved"); row.Children.Add(upload);
            audio.Children.Add(row);
        }
        audio.Children.Add(new TextBlock { Text = "Core audio stays linked. WCC or uploaded audio is copied when the scenario is saved. Timed playback is planned after v0.1.",
            Opacity = 0.7, TextWrapping = TextWrapping.Wrap });
    }
    private async void PickAudio(StoryPartVm part, StoryFieldVm field)
    {
        if (_vm is not { } model) return;
        var epoch = _hostEpoch;
        await model.PickAudioAsync(part, field, async vm =>
        {
            var picker = new CatalogPickerWindow(vm);
            await picker.ShowDialog(this);
            return picker.WasAccepted ? picker.SelectedSelections.SingleOrDefault() : null;
        }, () => epoch == _hostEpoch && ReferenceEquals(model, _vm));
    }
    private async void UploadAudio(StoryPartVm part, StoryFieldVm field)
    {
        if (_vm is not { } model) return;
        var epoch = _hostEpoch;
        await model.PickMediaFileAsync(part, null, field, async () =>
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "Upload Story " + field.Key, AllowMultiple = false,
                FileTypeFilter = [new FilePickerFileType("Audio") { Patterns = WccPackageMusicImporter.FilePatterns }] });
            if (files.FirstOrDefault() is not { } file) return null;
            return file.TryGetLocalPath() ?? throw new InvalidOperationException("Choose audio stored on this computer.");
        }, () => epoch == _hostEpoch && ReferenceEquals(model, _vm));
    }
    private void AddPartClicked(object? sender, RoutedEventArgs e) { _vm?.AddPart(); ResetElementSelection(); BuildSlides(); BuildInspector(); }
    private void DuplicatePartClicked(object? sender, RoutedEventArgs e) { _vm?.DuplicateSelectedPart(); ResetElementSelection(); BuildSlides(); BuildInspector(); }
    private void RemovePartClicked(object? sender, RoutedEventArgs e) { _vm?.RemoveSelectedPart(); ResetElementSelection(); BuildSlides(); BuildInspector(); }
    private void MovePartUpClicked(object? sender, RoutedEventArgs e) { _vm?.MoveSelectedPart(-1); BuildSlides(); BuildInspector(); }
    private void MovePartDownClicked(object? sender, RoutedEventArgs e) { _vm?.MoveSelectedPart(1); BuildSlides(); BuildInspector(); }
    private void ApplyClicked(object? sender, RoutedEventArgs e) { if (_vm?.Apply() == true) Close(true); }
    private void CancelClicked(object? sender, RoutedEventArgs e) => Close(false);
    private async void SourceClicked(object? sender, RoutedEventArgs e)
    {
        if (_vm is null) return;
        var raw = _vm.SelectedPart?.RawText is { Length: > 0 } text ? text : _vm.Session.Snapshot.DefinitionSourceText ?? _vm.Session.Snapshot.Source.Text;
        var window = new RawCodeEditorWindow("Captured Story source", raw, isReadOnly: true, descriptionText: "Original captured WML. Apply stages structured changes; scenario Save performs the full source review."); await window.ShowDialog(this);
    }
}
