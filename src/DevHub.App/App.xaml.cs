using Microsoft.UI.Xaml;
using DevHub.Core;

namespace DevHub.App;

public partial class App : Application
{
    private Window? window;
    internal static bool SmokeTest => Environment.GetCommandLineArgs().Any(a => a is "--smoke-test" or "--update-smoke");
    internal static bool AutoExitSmoke => Environment.GetCommandLineArgs().Contains("--smoke-test");
    internal static void WriteStartupReport(string status, string? error = null)
    {
        var args = Environment.GetCommandLineArgs(); var index = Array.IndexOf(args, "--startup-id");
        if (index >= 0 && index + 1 < args.Length) StartupLifecycle.Report(HubPaths.Discover(), args[index + 1], status, error);
    }
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
            Program.ShowFailure(e.Exception);
        };
    }
    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var arguments = Environment.GetCommandLineArgs(); var page = "home";
        var index = Array.IndexOf(arguments, "--page");
        if (index >= 0 && index + 1 < arguments.Length && new[] { "home", "tools", "agents", "versions", "projects", "diagnostics", "cache", "settings", "tasks" }.Contains(arguments[index + 1])) page = arguments[index + 1];
        try { window = new MainWindow(page); window.Activate(); }
        catch (Exception error) { Program.ShowFailure(error); Exit(); }
    }
}
