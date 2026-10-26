using Avalonia;
using System;
using Wcc.App.Services;
using Wcc.Core;

namespace Wcc.App;

sealed class Program
{
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static void Main(string[] args)
    {
        var request = WccFileOpenArguments.Parse(args, Environment.CurrentDirectory);
        foreach (var diagnostic in request.Diagnostics) Console.Error.WriteLine(diagnostic);
        if (request.Diagnostics.Count > 0 && request.Files.Count == 0)
        {
            Environment.ExitCode = 2;
            return;
        }
        if (!WccFileOpenInstanceBroker.TryAcquirePrimary(out var broker))
        {
            var forwarded = WccFileOpenInstanceBroker.ForwardAsync(request.Files).GetAwaiter().GetResult();
            if (!forwarded.Succeeded)
            {
                Console.Error.WriteLine(forwarded.Diagnostic);
                Environment.ExitCode = 1;
            }
            return;
        }
        using (broker)
        {
            try { WccPortableData.InitializeDefault(); }
            catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or
                System.Text.Json.JsonException or NotSupportedException or ArgumentException)
            {
                App.PortableStartupError = "WCC could not prepare its portable data.\n\n" +
                    "Extract WCC into a folder you can write to. Library, Cache and settings.json " +
                    "are stored beside the executable.\n\n" + ex.Message;
                Environment.ExitCode = 1;
                BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
                return;
            }
            using (var controller = new WmlEditorApplicationController(request.Files))
            {
                App.FileOpenController = controller;
                broker!.StartListening(controller.EnqueueFiles);
                try { BuildAvaloniaApp().StartWithClassicDesktopLifetime(args); }
                finally { App.FileOpenController = null; }
            }
        }
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
