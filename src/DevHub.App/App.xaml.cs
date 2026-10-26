using Microsoft.UI.Xaml;
using DevHub.Core;

namespace DevHub.App;

public partial class App : Application
{
    private Window? window;
    public App()
    {
        InitializeComponent();
        DebugSettings.IsXamlResourceReferenceTracingEnabled = true;
        DebugSettings.XamlResourceReferenceFailed += (_, e) =>
        {
            var paths = HubPaths.Discover(); File.AppendAllText(Path.Combine(paths.Logs, "xaml-resources.log"), e.Message + "\n");
        };
        UnhandledException += (_, e) =>
        {
            try
            {
                var paths = HubPaths.Discover();
                File.AppendAllText(Path.Combine(paths.Logs, "app-errors.log"), $"{DateTimeOffset.Now:O}\n{e.Message}\n{e.Exception}\n");
            }
            catch { }
        };
    }
    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var arguments = Environment.GetCommandLineArgs(); var page = "home";
        var index = Array.IndexOf(arguments, "--page");
        if (index >= 0 && index + 1 < arguments.Length && new[] { "home", "tools", "agents", "versions", "projects", "diagnostics", "cache", "settings", "tasks" }.Contains(arguments[index + 1])) page = arguments[index + 1];
        window = new MainWindow(page); window.Activate();
    }
}
