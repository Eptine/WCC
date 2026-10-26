using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Wcc.App.ViewModels;
using Wcc.App.ViewModels.Wcc;
using Wcc.App.Views.Controls;
using Wcc.Core.Projects;
using Wcc.Core.References;

namespace Wcc.App.Views;

public partial class StoryEditorWindow
{
    private StoryPartVm? _audioSelectionPart;
    private string _audioSelectionKey = "music";
    private int? _audioSelectionIndex;
    private long _audioSelectionEpoch;
    private bool _audioPendingRow;
    private readonly List<(Button Button, string Key, int? Index)> _audioRowButtons = [];
    private readonly List<(Button Button, string Key, bool Editable)> _audioRemoveButtons = [];

    private void SelectAudioRow(StoryPartVm part, string key, int? index, bool pending = false)
    {
        _audioSelectionEpoch++; _audioSelectionPart = part; _audioSelectionKey = key; _audioSelectionIndex = index;
        _audioPendingRow = pending;
        foreach (var row in _audioRowButtons)
            row.Button.BorderThickness = new Thickness(row.Key == key && row.Index == index ? 1 : 0);
        foreach (var row in _audioRemoveButtons) row.Button.IsEnabled = row.Editable && row.Key == key && index is not null;
        BuildSelectedSettings();
    }

    private Control BuildAudioSidebar(StoryPartVm part)
    {
        var panel = new StackPanel { Spacing = 10 };
        _audioRowButtons.Clear(); _audioRemoveButtons.Clear();
        if (_vm is not { } model) return panel;
        if (!ReferenceEquals(_audioSelectionPart, part))
        { _audioSelectionEpoch++; _audioPendingRow = false; _audioSelectionPart = part; _audioSelectionKey = "music"; _audioSelectionIndex = model.ReadAudioEntries(part, "music").Entries.Count > 0 ? 0 : null; }
        foreach (var key in new[] { "music", "sound", "voice" })
        {
            var state = model.ReadAudioEntries(part, key);
            var header = new DockPanel();
            var headerActions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
            var add = ActionButton("+", () =>
            {
                SelectAudioRow(part, key, key == "music" && state.Entries.Count > 0 ? 0 : null);
                _audioPendingRow = _audioSelectionIndex is null; BuildInspector();
            }, state.CanEdit);
            add.Name = "AudioAdd_" + key;
            WccButtonIcon.SetAction(add, WccActionIcon.Add);
            ToolTip.SetTip(add, key == "music" ? "Choose the slide's music track" : "Add a random alternative");
            var remove = ActionButton("−", () =>
            {
                if (_audioSelectionKey != key || _audioSelectionIndex is not { } position || !model.RemoveAudioEntry(part, key, position)) return;
                _audioSelectionEpoch++; _audioPendingRow = false; _audioSelectionIndex = null; BuildInspector();
            }, state.CanEdit && _audioSelectionKey == key && _audioSelectionIndex is not null);
            remove.Name = "AudioRemove_" + key; ToolTip.SetTip(remove, "Remove the selected audio entry");
            WccButtonIcon.SetAction(remove, WccActionIcon.Remove);
            _audioRemoveButtons.Add((remove, key, state.CanEdit)); headerActions.Children.Add(add); headerActions.Children.Add(remove);
            DockPanel.SetDock(headerActions, Dock.Right); header.Children.Add(headerActions);
            header.Children.Add(new TextBlock { Text = key == "music" ? "Music" : key == "sound" ? "Sounds" : "Voice", VerticalAlignment = VerticalAlignment.Center });
            panel.Children.Add(header);
            var rows = new StackPanel { Spacing = 4 };
            panel.Children.Add(new Border { BorderBrush = Brushes.Goldenrod, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(3),
                Padding = new Thickness(4), Child = new ScrollViewer { MaxHeight = key == "music" ? 110 : key == "sound" ? 175 : 90, Content = rows } });
            if (!state.IsPlain)
            {
                AddRow("Raw WML expression", null); continue;
            }
            for (var index = 0; index < state.Entries.Count; index++) AddRow(state.Entries[index], index);
            if (_audioPendingRow && _audioSelectionKey == key) AddRow("New " + key, null);
            else if (state.Entries.Count == 0) rows.Children.Add(new TextBlock { Text = "None", Opacity = 0.5, FontSize = 12 });
            if (key != "music") panel.Children.Add(new TextBlock { Text = "One alternative plays at random.", Opacity = 0.65, FontSize = 11, TextWrapping = TextWrapping.Wrap });

            void AddRow(string reference, int? index)
            {
                var button = ActionButton(Path.GetFileName(reference), () => SelectAudioRow(part, key, index, index is null && state.IsPlain));
                button.Name = "AudioEntry_" + key + "_" + (index?.ToString() ?? "new");
                button.HorizontalAlignment = HorizontalAlignment.Stretch; button.HorizontalContentAlignment = HorizontalAlignment.Left;
                button.BorderBrush = Brushes.Goldenrod; button.BorderThickness = new Thickness(_audioSelectionKey == key && _audioSelectionIndex == index ? 1 : 0);
                button.Opacity = part.Field(key).CanEdit ? 1 : 0.6;
                ToolTip.SetTip(button, reference); _audioRowButtons.Add((button, key, index)); rows.Children.Add(button);
            }
        }
        return panel;
    }

    private Control BuildAudioSettings(StoryPartVm part)
    {
        var panel = new StackPanel { Spacing = 8 };
        if (_vm is not { } model) return panel;
        if (!ReferenceEquals(_audioSelectionPart, part)) { _audioSelectionEpoch++; _audioSelectionPart = part; _audioSelectionKey = "music"; _audioSelectionIndex = null; }
        var key = _audioSelectionKey; var field = part.Field(key); var state = model.ReadAudioEntries(part, key);
        var index = _audioSelectionIndex is { } selected && selected < state.Entries.Count ? _audioSelectionIndex : null;
        _audioSelectionIndex = index;
        var header = new StackPanel { Name = "StoryLabel_" + field.Key, Orientation = Orientation.Horizontal, Spacing = 4 };
        var diamond = CreateStoryFieldDifficultyButton(field, () => FieldDifficulties(field), model.CanEdit && part.CanEdit && field.CanEdit);
        header.Children.Add(new TextBlock { Text = key == "music" ? "Music track" : key == "sound" ? "Sound alternative" : "Voice alternative", FontSize = 16 });
        header.Children.Add(diamond);
        panel.Children.Add(header);
        if (state.Problem is { } problem) panel.Children.Add(new TextBlock { Text = problem, Opacity = 0.7, TextWrapping = TextWrapping.Wrap });
        AddStoryFieldOrigin(panel, field);
        if (!state.IsPlain)
        {
            AddField(panel, field, "Raw " + key + " WML"); return panel;
        }
        var value = index is { } row ? state.Entries[row] : "";
        var input = new TextBox { Name = "AudioFileName", Text = value, IsEnabled = state.CanEdit, Watermark = "Audio filename", HorizontalAlignment = HorizontalAlignment.Stretch };
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        var set = ActionButton("Set filename", () =>
        {
            if (!model.SetAudioEntry(part, key, index, input.Text ?? "")) return;
            _audioPendingRow = false; _audioSelectionIndex = index ?? model.ReadAudioEntries(part, key).Entries.Count - 1; BuildInspector();
        }, state.CanEdit); set.Name = "AudioSetFileName"; WccButtonIcon.SetAction(set, WccActionIcon.Accept);
        WccButtonIcon.SetShowLabel(set, true); actions.Children.Add(set);
        var choose = ActionButton("Choose…", () => PickAudioRow(part, key, index), state.CanEdit && model.Session.AudioCatalog is not null);
        choose.Name = "AudioChoose"; WccButtonIcon.SetAction(choose, WccActionIcon.Select);
        ToolTip.SetTip(choose, "Choose " + key + " from the resource catalog"); actions.Children.Add(choose);
        var upload = ActionButton("Upload…", () => UploadAudioRow(part, key, index), state.CanEdit);
        WccButtonIcon.SetAction(upload, WccActionIcon.Browse); WccButtonIcon.SetShowLabel(upload, true);
        ToolTip.SetTip(upload, "Choose an audio file to copy into this campaign when saved");
        upload.Name = "AudioUpload"; actions.Children.Add(upload);
        panel.Children.Add(input); panel.Children.Add(actions);
        panel.Children.Add(new TextBlock { Text = key == "music" ? "The slide changes to one music track. An empty field keeps the current music." :
            "The engine chooses one of these alternatives at random and plays it once. Row order is not a playback sequence.", Opacity = 0.65, TextWrapping = TextWrapping.Wrap });
        return panel;
    }

    private async void PickAudioRow(StoryPartVm part, string key, int? index)
    {
        if (_vm is not { } model) return;
        var epoch = _hostEpoch; var rowEpoch = _audioSelectionEpoch;
        if (await model.PickAudioEntryAsync(part, key, index, async vm =>
        {
            var picker = new CatalogPickerWindow(vm); await picker.ShowDialog(this);
            return picker.WasAccepted ? picker.SelectedSelections.SingleOrDefault() : null;
        }, () => epoch == _hostEpoch && rowEpoch == _audioSelectionEpoch && ReferenceEquals(model, _vm)))
        { _audioPendingRow = false; _audioSelectionIndex = index ?? model.ReadAudioEntries(part, key).Entries.Count - 1; BuildInspector(); }
    }

    private async void UploadAudioRow(StoryPartVm part, string key, int? index)
    {
        if (_vm is not { } model) return;
        var epoch = _hostEpoch; var rowEpoch = _audioSelectionEpoch;
        if (await model.PickAudioEntryFileAsync(part, key, index, async () =>
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "Upload Story " + key, AllowMultiple = false,
                FileTypeFilter = [new FilePickerFileType("Audio") { Patterns = WccPackageMusicImporter.FilePatterns }] });
            return files.FirstOrDefault() is { } file ? file.TryGetLocalPath() ?? throw new InvalidOperationException("Choose audio stored on this computer.") : null;
        }, () => epoch == _hostEpoch && rowEpoch == _audioSelectionEpoch && ReferenceEquals(model, _vm)))
        { _audioPendingRow = false; _audioSelectionIndex = index ?? model.ReadAudioEntries(part, key).Entries.Count - 1; BuildInspector(); }
    }
}
