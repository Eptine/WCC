using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Wcc.App.Services;

namespace Wcc.App;

public partial class App : Application
{
    internal static string? PortableStartupError { get; set; }
    internal static WmlEditorApplicationController? FileOpenController { get; set; }

    internal static WmlEditorApplicationController GetFileOpenController()
        => FileOpenController ??= new();
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            if (PortableStartupError is { } error)
            {
                desktop.MainWindow = new Avalonia.Controls.Window
                {
                    Title = "WCC — portable data",
                    Width = 620, Height = 300,
                    Content = new Avalonia.Controls.TextBox
                    {
                        Text = error, IsReadOnly = true, TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                        Margin = new Avalonia.Thickness(20)
                    }
                };
            }
            else GetFileOpenController().Initialize(desktop);
        }

        base.OnFrameworkInitializationCompleted();
    }
}