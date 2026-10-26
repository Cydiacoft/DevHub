using System.Text.RegularExpressions;

namespace DevHub.Core;

public sealed class EverythingService(HubPaths paths, ICommandRunner runner)
{
    public string? FindCli(HubSettings settings)
    {
        var candidates = new[]
        {
            settings.EverythingCliPath, DiscoveryService.ResolveCommand(["es.exe"]),
            Path.Combine(AppContext.BaseDirectory, "integrations", "everything", "es.exe"),
            Path.Combine(paths.Data, "integrations", "everything", "es.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Everything", "es.exe")
        };
        return candidates.FirstOrDefault(p => !string.IsNullOrWhiteSpace(p) && File.Exists(p));
    }
    public async Task<List<string>> FindToolPathsAsync(HubSettings settings, IEnumerable<ToolDefinition> definitions, Action<string>? progress, CancellationToken ct)
    {
        if (!settings.UseEverything) return [];
        var cli = FindCli(settings);
        if (cli is null) { progress?.Invoke("Everything 接口未配置，使用目录与安装记录发现。"); return []; }
        var names = definitions.SelectMany(d => d.Executables).Distinct(StringComparer.OrdinalIgnoreCase).Select(Regex.Escape);
        var regex = "^(?:" + string.Join('|', names) + ")$";
        progress?.Invoke("查询 Everything 文件名索引…");
        var outputFile = Path.Combine(paths.Cache, "everything", Guid.NewGuid().ToString("N") + ".txt"); Directory.CreateDirectory(Path.GetDirectoryName(outputFile)!);
        try
        {
            var result = await runner.RunAsync(new(cli, ["-timeout", "2000", "-n", "5000", "-export-txt", outputFile, "-utf8-bom", "-regex", regex], paths.Root, TimeoutSeconds: 8), cancellation: ct);
            if (!result.Success || !File.Exists(outputFile)) { progress?.Invoke("Everything 未运行或索引不可用，继续使用目录扫描。"); return []; }
            var files = (await File.ReadAllLinesAsync(outputFile, ct)).Select(p => p.Trim().Trim('"')).Where(File.Exists)
                .Where(p => !HubPaths.IsWithin(p, Path.Combine(paths.State, "build")) && !HubPaths.IsWithin(p, paths.Cache))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            progress?.Invoke($"Everything 提供了 {files.Count} 个候选入口，正在核验归属与版本…");
            return files;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or IOException or UnauthorizedAccessException)
        {
            progress?.Invoke("Everything 接口不可用，已回退到目录与安装记录发现。"); return [];
        }
        finally { if (File.Exists(outputFile)) File.Delete(outputFile); }
    }
}
