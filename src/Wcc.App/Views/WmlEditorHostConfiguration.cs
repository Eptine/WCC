using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Wcc.App.ViewModels;

namespace Wcc.App.Views;

/// <summary>Common campaign context setup for both RAW window policies.</summary>
internal static class WmlEditorHostConfiguration
{
    public static async Task ConfigureAsync(Window window, WmlEditorWorkspaceView view,
        WmlEditorDocumentViewModel root, CancellationToken cancellationToken)
    {
        var owner = window.Owner;
        while (owner is not null && owner.DataContext is not AppShellViewModel) owner = owner.Owner;
        if (owner?.DataContext is not AppShellViewModel shell) return;
        view.SourceDocumentFactory = shell.CreateCampaignMacroSourceDocument;
        if (view.ReferenceContext is not null) return;
        try
        {
            var source = await shell.BuildCurrentSourceWmlViewAsync(cancellationToken);
            var context = await shell.BuildRawEditorReferenceContextAsync(root.Text, source?.SourcePath,
                cancellationToken: cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            view.ReferenceContext = context;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        { shell.Status = $"RAW reference context unavailable: {failure.Message}"; }
    }
}
