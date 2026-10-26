using System.Text.Json;
using System.Text.RegularExpressions;

namespace DevHub.Core;

public sealed class ManagementService(HubPaths paths)
{
    public CommandSpec PythonInstall(string version)
    {
        if (!Regex.IsMatch(version, @"^(?:cpython-|pypy-)?\d+\.\d+(?:\.\d+)?$")) throw new ArgumentException("请输入 Python 版本，例如 3.13 或 3.13.2。");
        var uv = DiscoveryService.ResolveCommand(["uv.exe"]) ?? throw new InvalidOperationException("请先从工具页面安装 uv。");
        return new(uv, ["python", "install", version], paths.Root, TimeoutSeconds: 1200);
    }
    public CommandSpec RustCommand(string action, string toolchain)
    {
        if (!Regex.IsMatch(toolchain, @"^[A-Za-z0-9][A-Za-z0-9._-]*$")) throw new ArgumentException("请输入 stable、beta、nightly 或具体 Rust 版本。");
        var rustup = DiscoveryService.ResolveCommand(["rustup.exe"]) ?? throw new InvalidOperationException("请先安装 rustup。");
        var args = action switch
        {
            "install" => new[] { "toolchain", "install", toolchain }, "update" => ["update", toolchain],
            "remove" => ["toolchain", "uninstall", toolchain], "default" => ["default", toolchain], _ => throw new ArgumentException("无效 Rust 操作。")
        };
        return new(rustup, args, paths.Root, TimeoutSeconds: 1800);
    }
    public CommandSpec PythonMaintenance(ToolInstallation installation, string action)
    {
        if (installation.ToolId != "python" || installation.Source != "uv" || !HubPaths.IsWithin(installation.HomePath, Path.Combine(paths.Sdks, "python")))
            throw new InvalidOperationException("此操作仅管理 DevHub 目录中的 uv Python。");
        var version = ProjectService.CleanVersion(installation.Version);
        if (string.IsNullOrEmpty(version)) throw new InvalidOperationException("无法确认 Python 版本。");
        var uv = DiscoveryService.ResolveCommand(["uv.exe"]) ?? throw new InvalidOperationException("未找到 uv。");
        return action switch
        {
            "update" => new(uv, ["python", "upgrade", string.Join('.', version.Split('.').Take(2))], paths.Root, TimeoutSeconds: 1200),
            "remove" => new(uv, ["python", "uninstall", version], paths.Root, TimeoutSeconds: 180),
            _ => throw new ArgumentException("无效 Python 操作。")
        };
    }
    public async Task RemoveManagedAsync(ToolInstallation installation, IReadOnlyList<ProjectProfile> projects, CancellationToken ct = default)
    {
        if (!installation.Managed || installation.Owner is not null) throw new InvalidOperationException("只允许直接移除 DevHub 管理的独立 SDK。");
        var users = projects.Where(p => p.Bindings.Values.Contains(installation.Id)).ToList();
        if (users.Count > 0) throw new InvalidOperationException("以下项目仍绑定此版本，请先解除：" + string.Join("、", users.Select(p => p.Name)));
        if (installation.IsActive) throw new InvalidOperationException("该版本仍是当前入口，请先切换到其他版本并重新扫描。");
        HubPaths.RequireSafeChild(installation.HomePath, paths.Sdks);
        var markerPath = Path.Combine(installation.HomePath, ".devhub-install.json");
        var marker = JsonSerializer.Deserialize<DownloadRelease>(await File.ReadAllTextAsync(markerPath, ct), JsonDefaults.Options);
        if (marker is null || marker.ToolId != installation.ToolId) throw new InvalidOperationException("SDK 归属记录不匹配，拒绝删除。");
        var javaHome = Environment.GetEnvironmentVariable("JAVA_HOME");
        if (javaHome is not null && DiscoveryService.SamePath(javaHome, installation.HomePath)) throw new InvalidOperationException("JAVA_HOME 仍在使用此版本。");
        var removedRoot = Path.Combine(paths.Data, "removed-sdks"); Directory.CreateDirectory(removedRoot);
        var retired = Path.Combine(removedRoot, Path.GetFileName(installation.HomePath) + "-" + Guid.NewGuid().ToString("N"));
        ct.ThrowIfCancellationRequested();
        // Retire the whole SDK atomically; permanent removal is a separate cache-space action.
        Directory.Move(installation.HomePath, retired);
    }
    public static void DeleteTreeWithoutLinks(string directory, string allowedRoot)
    {
        HubPaths.RequireSafeChild(directory, allowedRoot);
        var directories = new List<string>(); var pending = new Stack<string>(); pending.Push(directory);
        var files = new List<string>();
        while (pending.TryPop(out var dir))
        {
            HubPaths.RequireSafeChild(dir, allowedRoot); directories.Add(dir);
            foreach (var entry in Directory.EnumerateFileSystemEntries(dir))
            {
                if ((File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0) throw new InvalidOperationException("清理目录包含链接，已停止。");
                if (Directory.Exists(entry)) pending.Push(entry); else files.Add(entry);
            }
        }
        foreach (var file in files) { File.SetAttributes(file, FileAttributes.Normal); File.Delete(file); }
        foreach (var dir in Enumerable.Reverse(directories)) Directory.Delete(dir, false);
    }
}

public sealed record CacheEntry(string Name, string Directory, long Bytes, string Description,
    string Category = "DevHub 自身", string Owner = "DevHub", bool CanClean = true, string? BlockReason = null,
    int MinimumAgeDays = 0, long? ReclaimableBytes = null, string? CleanupTool = null)
{
    public string Size => Bytes >= 1073741824 ? $"{Bytes / 1073741824d:F2} GB" : $"{Bytes / 1048576d:F1} MB";
    public long Reclaimable => CanClean ? ReclaimableBytes ?? Bytes : 0;
    public override string ToString() => $"{Name} · {Size}";
}
public sealed record CacheCleanupResult(int DeletedFiles, int RetainedFiles, long ReleasedBytes, string? Note = null);
public sealed partial class CacheService
{
    private readonly HubPaths cachePaths;
    public CacheService(HubPaths paths) => cachePaths = paths;
    private readonly Dictionary<string, CacheEntry> approvedPaths = new(StringComparer.OrdinalIgnoreCase);
    public List<CacheEntry> Inspect()
    {
        var entries = Directory.GetDirectories(cachePaths.Cache).Select(d => new CacheEntry(CacheName(Path.GetFileName(d)), d, Measure(d), "DevHub 自身构建或通过管理器运行工具产生的缓存", CleanupTool: Path.GetFileName(d) == "uv" ? "uv-prune" : null)).ToList();
        var gradleCaches = Path.Combine(cachePaths.Data, "gradle", "caches");
        if (Directory.Exists(gradleCaches)) entries.Add(new("Gradle 缓存", gradleCaches, Measure(gradleCaches), "保留 Gradle 配置和其他工作数据"));
        var cargoRegistry = Path.Combine(cachePaths.Data, "cargo", "registry", "cache");
        if (Directory.Exists(cargoRegistry)) entries.Add(new("Cargo 下载缓存", cargoRegistry, Measure(cargoRegistry), "保留已安装命令和注册信息"));
        return entries.OrderByDescending(e => e.Bytes).ToList();
    }
    public CacheCleanupResult Clear(CacheEntry entry)
    {
        var approved = approvedPaths.GetValueOrDefault(Path.GetFullPath(entry.Directory)) ?? Inspect().FirstOrDefault(e => DiscoveryService.SamePath(e.Directory, entry.Directory));
        if (approved is null) throw new InvalidOperationException("该目录不在最近扫描确认的缓存清单中，请重新扫描。");
        if (!approved.CanClean) throw new InvalidOperationException(approved.BlockReason ?? "此目录包含依赖或工具状态，请通过所属工具管理。");
        if (approved.CleanupTool is not null) throw new InvalidOperationException("此缓存必须通过所属工具清理。");
        if (approved.MinimumAgeDays > 0)
        {
            return ClearCacheContents(approved.Directory, DateTime.UtcNow.AddDays(-approved.MinimumAgeDays));
        }
        return ClearCacheContents(approved.Directory, null);
    }
    public async Task<CacheCleanupResult> ClearAsync(CacheEntry entry, ICommandRunner runner, CancellationToken ct = default)
    {
        var approved = approvedPaths.GetValueOrDefault(Path.GetFullPath(entry.Directory)) ?? Inspect().FirstOrDefault(e => DiscoveryService.SamePath(e.Directory, entry.Directory));
        if (approved is null || !approved.CanClean) throw new InvalidOperationException("此目录未批准作为可清理缓存，请重新扫描。");
        if (approved.CleanupTool is null) return await Task.Run(() => Clear(approved), ct);
        var exe = DiscoveryService.ResolveCommand(approved.CleanupTool == "uv-prune" ? ["uv.exe"] : new[] { "pnpm.exe", "pnpm.cmd" })
            ?? throw new InvalidOperationException("未找到缓存所属工具，请先安装对应工具。");
        var args = approved.CleanupTool switch
        {
            "uv-prune" => new[] { "cache", "prune", "--cache-dir", approved.Directory },
            "pnpm-prune" => ["store", "prune", "--store-dir", approved.Directory],
            _ => throw new InvalidOperationException("不支持的缓存清理工具。")
        };
        var before = Measure(approved.Directory, ct);
        var result = await runner.RunAsync(new(exe, args, cachePaths.Root, TimeoutSeconds: 1200), cancellation: ct); result.EnsureSuccess();
        var after = Directory.Exists(approved.Directory) ? Measure(approved.Directory, ct) : 0;
        return new(-1, 0, Math.Max(0, before - after), result.Combined);
    }
    public static long Measure(string directory, CancellationToken cancellation = default)
    {
        long size = 0; var dirs = new Stack<string>(); dirs.Push(directory);
        while (dirs.TryPop(out var dir))
        {
            cancellation.ThrowIfCancellationRequested();
            try
            {
                if ((File.GetAttributes(dir) & FileAttributes.ReparsePoint) != 0) continue;
                foreach (var entry in Directory.EnumerateFileSystemEntries(dir))
                {
                    var attrs = File.GetAttributes(entry);
                    if ((attrs & FileAttributes.ReparsePoint) != 0) continue;
                    if ((attrs & FileAttributes.Directory) != 0) dirs.Push(entry); else size += new FileInfo(entry).Length;
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        return size;
    }
    private static string CacheName(string name) => name switch
    {
        "nuget" => "软件构建依赖（NuGet）", "tmp" => "临时文件", "downloads" => "安装包下载",
        "metadata" => "版本列表数据", "scan" => "工具扫描索引", "everything" => "Everything 查询结果", _ => name + " 缓存"
    };
    internal static void DeleteOldTemporaryFiles(string directory, DateTime cutoff)
        => ClearCacheContents(directory, cutoff);
    private static CacheCleanupResult ClearCacheContents(string directory, DateTime? cutoff)
    {
        var parent = Path.GetDirectoryName(Path.GetFullPath(directory)) ?? throw new InvalidOperationException("无法清理磁盘根目录。");
        HubPaths.RequireSafeChild(directory, parent);
        int deleted = 0, retained = 0; long released = 0;
        var visited = new List<string>();
        var pending = new Stack<string>(); pending.Push(directory);
        while (pending.TryPop(out var current))
        {
            HubPaths.RequireSafeChild(current, parent); visited.Add(current);
            try
            {
                foreach (var entry in Directory.EnumerateFileSystemEntries(current))
                {
                    try
                    {
                        var attributes = File.GetAttributes(entry); if ((attributes & FileAttributes.ReparsePoint) != 0) { retained++; continue; }
                        if ((attributes & FileAttributes.Directory) != 0) { pending.Push(entry); continue; }
                        if (cutoff is not null && File.GetLastWriteTimeUtc(entry) >= cutoff.Value) { retained++; continue; }
                        var bytes = new FileInfo(entry).Length;
                        using (File.Open(entry, FileMode.Open, FileAccess.Read, FileShare.None)) { }
                        if ((attributes & FileAttributes.ReadOnly) != 0) File.SetAttributes(entry, attributes & ~FileAttributes.ReadOnly);
                        File.Delete(entry); deleted++; released += bytes;
                    }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException) { retained++; }
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { retained++; }
        }
        foreach (var child in Enumerable.Reverse(visited).Where(d => !DiscoveryService.SamePath(d, directory)))
            try { if (!Directory.EnumerateFileSystemEntries(child).Any()) Directory.Delete(child); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return new(deleted, retained, released);
    }
    private static long MeasureOldFiles(string directory, DateTime cutoff, CancellationToken ct)
    {
        long size = 0; var pending = new Stack<string>(); pending.Push(directory);
        while (pending.TryPop(out var current))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                foreach (var entry in Directory.EnumerateFileSystemEntries(current))
                {
                    var attributes = File.GetAttributes(entry); if ((attributes & FileAttributes.ReparsePoint) != 0) continue;
                    if ((attributes & FileAttributes.Directory) != 0) pending.Push(entry);
                    else if (File.GetLastWriteTimeUtc(entry) < cutoff) size += new FileInfo(entry).Length;
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        return size;
    }
}

public static class DiagnosticService
{
    public static List<Diagnostic> Analyze(IReadOnlyList<ToolInstallation> installations, IReadOnlyList<ProjectProfile> projects)
    {
        var issues = new List<Diagnostic>();
        var java = installations.FirstOrDefault(t => t.ToolId == "java" && t.IsActive);
        var home = Environment.GetEnvironmentVariable("JAVA_HOME");
        if (!string.IsNullOrWhiteSpace(home))
        {
            if (!File.Exists(Path.Combine(home, "bin", "java.exe"))) issues.Add(new("错误", "JAVA_HOME 已失效", home, "java"));
            else if (java is not null && !DiscoveryService.SamePath(java.HomePath, home)) issues.Add(new("警告", "JAVA_HOME 与 Java 命令入口不一致", $"JAVA_HOME：{home}\n当前 java：{java.ExecutablePath}\n可在 Java 详情中预览全局切换。", "java"));
        }
        var python = installations.FirstOrDefault(t => t.ToolId == "python" && t.IsActive);
        var pip = installations.FirstOrDefault(t => t.ToolId == "pip" && t.IsActive);
        if (python is not null && pip is not null && !HubPaths.IsWithin(pip.ExecutablePath, python.HomePath))
            issues.Add(new("警告", "python 与 pip 的入口来自不同目录", $"python：{python.ExecutablePath}\npip：{pip.ExecutablePath}\n使用本工具的包管理页面可明确选择 Python。", "python"));
        foreach (var duplicate in DiscoveryService.EnvironmentPaths().GroupBy(p => p.TrimEnd('\\'), StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
            issues.Add(new("提示", "PATH 存在重复目录", duplicate.Key));
        foreach (var invalid in installations.Where(i => i.Version is "入口不可用" or "检测超时" or "无法读取"))
            issues.Add(new("警告", invalid.Name + " " + invalid.Version, invalid.ExecutablePath, invalid.ToolId));
        foreach (var project in projects)
        {
            if (!Directory.Exists(project.Directory)) issues.Add(new("错误", "项目目录不存在：" + project.Name, project.Directory));
            foreach (var binding in project.Bindings)
                if (!installations.Any(i => i.Id == binding.Value)) issues.Add(new("错误", project.Name + " 的工具版本未找到", binding.Key));
            if (project.VirtualEnvironment is { Length: > 0 } venv && !File.Exists(Path.Combine(venv, "Scripts", "python.exe"))) issues.Add(new("错误", project.Name + " 的 Python 环境已失效", venv));
            foreach (var binding in project.Bindings)
            {
                if (binding.Key is not ("python" or "node" or "dotnet" or "rust" or "flutter")) continue;
                var selected = installations.FirstOrDefault(i => i.Id == binding.Value); if (selected is null) continue;
                var file = Path.Combine(project.Directory, ProjectService.VersionFileName(binding.Key));
                if (!File.Exists(file)) continue;
                var content = File.ReadAllText(file);
                var selectedVersion = ProjectService.CleanVersion(selected.Version);
                if (!string.IsNullOrEmpty(selectedVersion) && !content.Contains(selectedVersion, StringComparison.Ordinal)) issues.Add(new("警告", project.Name + " 的版本声明可能与绑定不一致", $"文件：{file}\n选择：{selected.Version}\n请在项目页检查声明内容。", binding.Key));
            }
        }
        if (issues.Count == 0) issues.Add(new("正常", "未发现常见环境冲突", "工具入口和项目绑定检查通过。可继续运行各工具的专项诊断。"));
        return issues;
    }
}
