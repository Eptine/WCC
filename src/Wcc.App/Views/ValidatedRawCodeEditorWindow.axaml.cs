using System;
using System.Threading;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Wcc.App.ViewModels;

namespace Wcc.App.Views;

public partial class ValidatedRawCodeEditorWindow : Window
{
    private readonly WmlEditorDocumentViewModel _document;
    private readonly CancellationTokenSource _sourceContextCancellation = new();
    public WmlEditorInitialSelection? InitialSourceSelection { get; init; }

    public WmlEditorReferenceContext? ReferenceContext
    {
        get => CodeView.ReferenceContext;
        set => CodeView.ReferenceContext = value;
    }

    public ValidatedRawCodeEditorWindow()
    {
        InitializeComponent();
        _document = new WmlEditorDocumentViewModel("Raw WML Editor", "", isReadOnly: true);
        DataContext = _document;
        AddHandler(KeyDownEvent, (_, args) => CodeView.HandleSearchKey(args), RoutingStrategies.Tunnel);
        Closing += (_, args) =>
        {
            CodeView.DiscardSourceEdits();
            args.Cancel = !_document.TryClose();
        };
        Opened += (_, _) =>
        {
            if (InitialSourceSelection is not { } selection) return;
            selection.TryGetSelection(_document.Text, out var start, out var length);
            CodeView.SelectSource(start, length);
        };
        Opened += async (_, _) => await WmlEditorHostConfiguration.ConfigureAsync(
            this, CodeView, _document, _sourceContextCancellation.Token);
        Closed += (_, _) => _sourceContextCancellation.Cancel();
    }

    public ValidatedRawCodeEditorWindow(
        string title,
        string initialText,
        string descriptionText,
        Func<string, string?> validate,
        Action<string> onSave)
        : this()
    {
        Title = title;
        _document = new WmlEditorDocumentViewModel(title, initialText,
            onSave ?? throw new ArgumentNullException(nameof(onSave)), descriptionText: descriptionText,
            validate: validate ?? throw new ArgumentNullException(nameof(validate)),
            closePolicy: WmlEditorClosePolicy.ExplicitApply);
        DataContext = _document;
    }

    private void SaveClicked(object? sender, RoutedEventArgs e)
    {
        if (CodeView.RequireAppliedSourceEdits() && _document.TryApply()) Close();
    }

    private void CancelClicked(object? sender, RoutedEventArgs e)
    {
        _document.Cancel();
        Close();
    }
}
