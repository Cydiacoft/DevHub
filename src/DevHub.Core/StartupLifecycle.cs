using System.Reflection;
using System.Text.Json;

namespace DevHub.Core;

public sealed record StartupReport(string Status, string Version, int ProcessId, string Directory, string? Error = null);
public static class StartupLifecycle
{
    public static string Version => typeof(StartupLifecycle).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "1.1.0";
    public static string ReportPath(HubPaths paths, string id)
    {
        if (!Guid.TryParseExact(id, "N", out _)) throw new ArgumentException("启动标识无效。");
        return Path.Combine(paths.Data, "startup", id + ".json");
    }
    public static void Report(HubPaths paths, string id, string status, string? error = null)
    {
        var file = ReportPath(paths, id); Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var temporary = file + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(new StartupReport(status, Version, Environment.ProcessId, AppContext.BaseDirectory, error), JsonDefaults.Options));
        File.Move(temporary, file, true);
    }
    public static string LogFailure(Exception error)
    {
        try
        {
            var paths = HubPaths.Discover(); var log = Path.Combine(paths.Logs, "app-errors.log");
            File.AppendAllText(log, $"{DateTimeOffset.Now:O}\n{error}\n");
            return log;
        }
        catch { return "日志目录不可写，请将完整程序移到可写目录后重新打开。"; }
    }
}
