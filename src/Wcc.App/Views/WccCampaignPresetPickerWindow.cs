using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Layout;
using Wcc.App.ViewModels;

namespace Wcc.App.Views;

/// <summary>The shared catalog picker with one explicit instance identity choice.</summary>
public sealed class WccCampaignPresetPickerWindow : CatalogPickerWindow
{
    public WccCampaignPresetPickerWindow(WccCampaignPresetPickerVm vm) : base(vm)
    {
        Title = "Campaign " + vm.Binding.Role + " preset"; Classes.Add("wccShell");
        if (!vm.HasIdentityOverride || Content is not DockPanel panel) return;
        var identity = new StackPanel { Spacing = 6, Margin = new(0, 10, 0, 10) };
        identity.Children.Add(new TextBlock { Text = vm.IdentityLabel });
        var text = new TextBox { Watermark = "Reuse preset identity" };
        text.Bind(TextBox.TextProperty, new Binding(nameof(vm.IdentityOverride)) { Mode = BindingMode.TwoWay });
        identity.Children.Add(text);
        identity.Children.Add(new TextBlock { Text = vm.IdentityGuidance, TextWrapping = Avalonia.Media.TextWrapping.Wrap });
        DockPanel.SetDock(identity, Dock.Top); panel.Children.Insert(1, identity);
    }
}
