using System;
using System.IO;
using System.Linq;
using System.Text;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Wcc.App.ViewModels;
using Wcc.Core.Wml.Checking;

namespace Wcc.App.Views;

/// <summary>Independent diagnostics host with explicit RAW source fixes and report export.</summary>
public partial class WmlTesterWindow : Window
{
    private void ResetFiltersClicked(object? sender, RoutedEventArgs args) => ViewModel.ResetFilters();
    private bool _closed;
    public WmlTesterViewModel ViewModel { get; }

    public WmlTesterWindow() : this(new WmlTesterViewModel()) { }

    public WmlTesterWindow(WmlTesterViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        InitializeComponent();
        DataContext = ViewModel = viewModel;
        Closed += (_, _) => { _closed = true; ViewModel.Dispose(); };
    }

    private async void AddFilesClicked(object? sender, RoutedEventArgs args)
    {
        if (_closed || ViewModel.IsBusy) return;
        try
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Select WML files to check", AllowMultiple = true,
                FileTypeFilter = [new FilePickerFileType("Wesnoth WML") { Patterns = ["*.cfg"] }]
            });
            if (!_closed && !ViewModel.IsBusy)
                ViewModel.AddFiles(files.Select(file => file.TryGetLocalPath()).OfType<string>());
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException or InvalidOperationException)
        { if (!_closed) WindowErrorText.Text = failure.Message; }
    }

    private void RemoveFileClicked(object? sender, RoutedEventArgs args)
    { if (FileList.SelectedItem is WmlTesterFile file) ViewModel.RemoveFile(file); }
    private void ClearFilesClicked(object? sender, RoutedEventArgs args) => ViewModel.ClearFiles();
    private async void PickCampaignContextClicked(object? sender, RoutedEventArgs args)
    {
        if (_closed || ViewModel.IsBusy) return;
        try
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Select campaign main .cfg", AllowMultiple = false,
                FileTypeFilter = [new FilePickerFileType("Wesnoth WML") { Patterns = ["*.cfg"] }]
            });
            if (!_closed && !ViewModel.IsBusy && files.FirstOrDefault()?.TryGetLocalPath() is { } path)
                ViewModel.CampaignEntryPath = path;
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException or InvalidOperationException)
        { if (!_closed) WindowErrorText.Text = failure.Message; }
    }
    private void ClearCampaignContextClicked(object? sender, RoutedEventArgs args)
    { if (!_closed && !ViewModel.IsBusy) ViewModel.ClearCampaignContext(); }

    private async void PickAddonsNamespaceClicked(object? sender, RoutedEventArgs args)
    {
        if (_closed || ViewModel.IsBusy) return;
        try
        {
            var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "Select parent folder of installed add-ons", AllowMultiple = false
            });
            if (!_closed && !ViewModel.IsBusy && folders.FirstOrDefault()?.TryGetLocalPath() is { } path)
                ViewModel.AddonsNamespacePath = path;
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException or InvalidOperationException)
        { if (!_closed) WindowErrorText.Text = failure.Message; }
    }

    private async void AddDependencyFoldersClicked(object? sender, RoutedEventArgs args)
    {
        if (_closed || ViewModel.IsBusy) return;
        try
        {
            var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "Select dependency add-on folders", AllowMultiple = true
            });
            if (_closed || ViewModel.IsBusy) return;
            var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
            var current = ViewModel.DependencyRoots;
            var existing = current.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).ToHashSet(comparer);
            var added = folders.Select(folder => folder.TryGetLocalPath()).OfType<string>()
                .Distinct(comparer).Where(path => !existing.Contains(path)).ToArray();
            if (added.Length == 0) return;
            var separator = current.Length != 0 && !current.EndsWith('\r') && !current.EndsWith('\n')
                ? Environment.NewLine : "";
            ViewModel.DependencyRoots = current + separator + string.Join(Environment.NewLine, added);
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException or InvalidOperationException)
        { if (!_closed) WindowErrorText.Text = failure.Message; }
    }

    private void ClearDependencyFoldersClicked(object? sender, RoutedEventArgs args)
    { if (!_closed && !ViewModel.IsBusy) ViewModel.DependencyRoots = ""; }

    private async void RunClicked(object? sender, RoutedEventArgs args)
    { WindowErrorText.Text = null; await ViewModel.RunAsync(); }
    private void CancelClicked(object? sender, RoutedEventArgs args) => ViewModel.Cancel();

    public RawCodeEditorWindow? CreateSelectedSourceWindow()
    {
        if (_closed || !ViewModel.CanOpenSelectedSource) return null;
        if (ViewModel.CreateSelectedSourceEdit() is { } edit) return new RawCodeEditorWindow("Checked WML - " + Path.GetFileName(edit.Path), edit.Text,
            descriptionText: edit.Path + ". Apply saves this RAW file and reruns the selected checks. Discard leaves it unchanged.",
            initialSelectionStart: edit.Location.StartOffset, initialSelectionLength: edit.Location.Length,
            onSaveAsync: edit.ApplyAsync);
        if (ViewModel.SelectedDiagnosticContextSource is not { } support || ViewModel.SelectedDiagnostic is not { } finding) return null;
        return new RawCodeEditorWindow("Context WML - " + Path.GetFileName(support.Path), support.Text, isReadOnly: true,
            descriptionText: support.Path + ". Captured context source; add it to Files to check before editing it.",
            initialSelectionStart: finding.Location.StartOffset, initialSelectionLength: finding.Location.Length);
    }

    private async void ViewSourceClicked(object? sender, RoutedEventArgs args)
    { if (CreateSelectedSourceWindow() is { } editor) await editor.ShowDialog(this); }

    private async void ExportClicked(object? sender, RoutedEventArgs args)
    {
        if (_closed || ViewModel.Report is not { } report) return;
        try
        {
            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Export WML check report", SuggestedFileName = "wcc-wml-report.txt", DefaultExtension = "txt",
                FileTypeChoices = [new FilePickerFileType("Text report") { Patterns = ["*.txt"] },
                    new FilePickerFileType("JSON report") { Patterns = ["*.json"] }]
            });
            if (_closed || file is null) return;
            if (!file.Name.EndsWith(".txt", StringComparison.OrdinalIgnoreCase) &&
                !file.Name.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            {
                WindowErrorText.Text = "Choose a .txt or .json report file.";
                return;
            }
            // The picker can remain open across a new run; export the report explicitly captured above.
            var text = file.Name.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
                ? WmlCheckReportFormatter.ToJson(report) : WmlCheckReportFormatter.ToText(report);
            await using var stream = await file.OpenWriteAsync();
            stream.SetLength(0);
            await using var writer = new StreamWriter(stream, new UTF8Encoding(false));
            await writer.WriteAsync(text);
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException or InvalidOperationException)
        { if (!_closed) WindowErrorText.Text = "Could not export the report: " + failure.Message; }
    }
}
