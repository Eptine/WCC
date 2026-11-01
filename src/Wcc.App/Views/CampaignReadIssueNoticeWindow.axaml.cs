using System;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Wcc.App.Services;
using Wcc.App.ViewModels;

namespace Wcc.App.Views;

/// <summary>An explicit modal acknowledgement; the Tester is a child of this notice.</summary>
public partial class CampaignReadIssueNoticeWindow : Window
{
    private readonly Func<Window, Task>? _openTester;
    private bool _closed;
    private bool _openingTester;
    private bool _decisionChosen;

    public CampaignReadIssueNoticeWindow()
    {
        InitializeComponent();
        Closed += (_, _) => _closed = true;
        Closing += (_, args) =>
        {
            // Require an explicit action to acknowledge the load limitation.
            // An owner/application shutdown must still close its owned windows.
            if (!_decisionChosen && args.CloseReason is not (WindowCloseReason.OwnerWindowClosing
                or WindowCloseReason.ApplicationShutdown or WindowCloseReason.OSShutdown))
                args.Cancel = true;
        };
        Opened += (_, _) =>
        {
            // The owner is disabled by ShowDialog. Bring this acknowledgement to
            // the front once without leaving it permanently above other apps.
            Topmost = true;
            Activate();
            Dispatcher.UIThread.Post(() =>
            {
                if (_closed) return;
                Topmost = false;
                Activate();
                OpenTesterButton.Focus();
            }, DispatcherPriority.Input);
        };
    }

    public CampaignReadIssueNoticeWindow(CampaignReadIssueNotice notice, Func<Window, Task> openTester) : this()
    {
        ArgumentNullException.ThrowIfNull(notice);
        ArgumentNullException.ThrowIfNull(openTester);
        DataContext = notice;
        _openTester = openTester;
    }

    private void IgnoreClicked(object? sender, RoutedEventArgs args)
    {
        if (!_closed && !_openingTester) Choose(CampaignReadIssueNoticeDecision.Ignore);
    }

    private void ReloadCampaignClicked(object? sender, RoutedEventArgs args)
    {
        if (_closed || _openingTester || !CurrentNotice()) return;
        Choose(CampaignReadIssueNoticeDecision.Reload);
    }

    private async void OpenTesterClicked(object? sender, RoutedEventArgs args)
    {
        if (_closed || _openingTester || _openTester is null || !CurrentNotice()) return;
        _openingTester = true;
        SetActionsEnabled(false);
        ActionStatus.Text = null;
        try
        {
            await _openTester(this);
            if (!_closed && CurrentNotice())
            {
                ActionStatus.Text = "Saved any source fixes? Choose Reload Campaign to read the campaign again.";
                Activate();
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or NotSupportedException)
        {
            if (!_closed) ActionStatus.Text = "The Tester could not be opened: " + error.Message;
        }
        finally
        {
            _openingTester = false;
            if (!_closed) SetActionsEnabled(true);
        }
    }

    private bool CurrentNotice()
    {
        if (DataContext is CampaignReadIssueNotice notice && notice.IsCurrent?.Invoke() != false) return true;
        Choose(CampaignReadIssueNoticeDecision.Ignore);
        return false;
    }

    private void Choose(CampaignReadIssueNoticeDecision decision)
    {
        if (_closed) return;
        _decisionChosen = true;
        Close(decision);
    }

    private void SetActionsEnabled(bool enabled)
    {
        IgnoreButton.IsEnabled = enabled;
        OpenTesterButton.IsEnabled = enabled;
        ReloadCampaignButton.IsEnabled = enabled;
    }
}

/// <summary>One fresh Tester session uses the exact context which produced the notice.</summary>
internal static class CampaignReadIssueTester
{
    internal static WmlTesterViewModel CreateViewModel(CampaignReadIssueNotice notice)
    {
        ArgumentNullException.ThrowIfNull(notice);
        var installationPath = notice.InstallationPath;
        var exactVersion = notice.ExactVersion;
        var label = string.IsNullOrWhiteSpace(exactVersion) ? "Campaign installation" : "Wesnoth " + exactVersion;
        return new WmlTesterViewModel(captureCampaignRun: options =>
            new WmlTesterCampaignContextRunner(installationPath, label, options, exactVersion).CheckCaptured)
        {
            CampaignEntryPath = notice.EntryPath,
            AddonsNamespacePath = notice.AddonsNamespacePath ?? string.Empty,
            DependencyRoots = string.Join(Environment.NewLine, notice.DependencyRoots),
            CheckWholeCampaign = true,
            CheckStructure = true,
            CheckReferences = true,
            CheckFormatting = true,
            CheckResources = true
        };
    }
}
