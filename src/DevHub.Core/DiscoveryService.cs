using Microsoft.Win32;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DevHub.Core;

public sealed class DiscoveryService(HubPaths paths, ICommandRunner runner)
{
    public static ToolCatalog BuiltInCatalog()
    {
        using var stream = typeof(DiscoveryService).Assembly.GetManifestResourceStream("DevHub.Core.Catalog.json")!;
        return JsonSerializer.Deserialize<ToolCatalog>(stream, JsonDefaults.Options)!;
    }

    public async Task<List<ToolInstallation>> ScanAsync(HubSettings settings, Action<string>? progress = null, CancellationToken cancellation = default, bool fullScan = false)
    {
        var stopwatch = Stopwatch.StartNew();
        var catalog = BuiltInCatalog().Tools.Concat(settings.CustomTools).ToList();
        var cachePath = Path.Combine(paths.Cache, "scan", "versions.json");
        Dictionary<string, VersionCacheEntry> versionCache = [];
        if (File.Exists(cachePath))
            try { versionCache = JsonSerializer.Deserialize<Dictionary<string, VersionCacheEntry>>(await File.ReadAllTextAsync(cachePath, cancellation), JsonDefaults.Options) ?? []; }
            catch (JsonException) { }
        progress?.Invoke(fullScan ? "完整扫描：发现工具位置并重新核验版本…" : "快速扫描：发现工具位置，复用未变化的版本记录…");
        var entries = new Dictionary<string, ToolInstallation>(StringComparer.OrdinalIgnoreCase);
        var pathDirs = EnvironmentPaths().Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var dirs = new HashSet<string>(pathDirs, StringComparer.OrdinalIgnoreCase);
        var program = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var local = UserDirectories.LocalAppData;
        var user = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        foreach (var directory in new[] { Path.Combine(UserDirectories.RoamingAppData, "npm"), Path.Combine(local, "pnpm"), Path.Combine(user, ".bun", "bin") })
            if (Directory.Exists(directory)) dirs.Add(directory);
        foreach (var root in new[] { Path.Combine(program, "Java"), Path.Combine(program, "Eclipse Adoptium"), Path.Combine(program, "Microsoft"), Path.Combine(program, "Zulu"), Path.Combine(program, "Amazon Corretto"), Path.Combine(program, "Go"), Path.Combine(program, "dotnet"), Path.Combine(program, "Git"), Path.Combine(program, "7-Zip"), Path.Combine(local, "Programs", "Python"), Path.Combine(local, "Python"), Path.Combine(user, ".jdks"), Path.Combine(user, ".cargo"), Path.Combine(user, ".rustup", "toolchains"), Path.Combine(user, ".local", "bin"), Path.Combine(user, "scoop", "shims"), Path.Combine(user, "scoop", "apps"), Path.Combine(user, ".pyenv", "pyenv-win", "versions"), Path.Combine(user, ".local", "share", "uv", "python"), Path.Combine(local, "uv", "python"), Path.Combine(user, "fvm", "versions"), Path.Combine(user, ".fvm", "versions"), Path.Combine(local, "UniGetUI", "Chocolatey", "bin"), paths.Sdks })
            AddTree(root, dirs, 3, cancellation);
        foreach (var name in new[] { "JAVA_HOME", "JDK_HOME", "PYTHONHOME", "FLUTTER_ROOT", "ANDROID_HOME", "ANDROID_SDK_ROOT", "GOROOT" })
        {
            var value = Environment.GetEnvironmentVariable(name);
            if (!string.IsNullOrWhiteSpace(value)) AddTree(value, dirs, name.StartsWith("ANDROID") ? 4 : 2, cancellation);
        }
        if (settings.AndroidSdkRoot is { Length: > 0 } sdk) AddTree(sdk, dirs, 4, cancellation);
        foreach (var custom in settings.ScanDirectories) AddTree(custom, dirs, Math.Clamp(settings.AdditionalScanDepth, 1, 8), cancellation);
        if (OperatingSystem.IsWindows())
        {
            foreach (var registration in ReadInstallations()) AddTree(registration, dirs, 3, cancellation);
            foreach (var python in ReadPythonInstallations()) AddTree(python, dirs, 2, cancellation);
        }
        // A PATH entry often points at a tool's bin directory; inspect its siblings as well.
        foreach (var path in pathDirs.ToList())
        {
            if (Path.GetFileName(path).Equals("bin", StringComparison.OrdinalIgnoreCase) && Directory.GetParent(path) is { } parent)
            {
                dirs.Add(parent.FullName);
                if (File.Exists(Path.Combine(parent.FullName, "bin", "flutter.bat"))) AddTree(parent.FullName, dirs, 2, cancellation);
            }
        }
        var everythingPaths = await new EverythingService(paths, runner).FindToolPathsAsync(settings, catalog, progress, cancellation);
        foreach (var file in everythingPaths) dirs.Add(Path.GetDirectoryName(file)!);
        progress?.Invoke($"正在检查 {dirs.Count} 个候选目录…");
        foreach (var dir in dirs)
        {
            cancellation.ThrowIfCancellationRequested();
            foreach (var definition in catalog)
                foreach (var file in definition.Executables)
                {
                    var full = Path.Combine(dir, file);
                    if (!File.Exists(full)) continue;
                    // Desktop Electron launchers can share a CLI name (for example Claude).
                    // Do not launch a desktop app merely to query a CLI version.
                    if (definition.Category == "AI 工具" && (File.Exists(Path.Combine(dir, "resources", "app.asar")) || File.Exists(Path.Combine(dir, "resources", "app", "package.json")))) continue;
                    var installation = Identify(definition, full);
                    var key = definition.Id + "|" + installation.RealPath;
                    // python/python3 and batch aliases from the same installation are one record.
                    if (definition.Id is "python" or "java" or "flutter" or "dart" or "android") key = definition.Id + "|" + installation.HomePath;
                    entries.TryAdd(key, installation);
                }
        }
        foreach (var dir in pathDirs.Where(p => !IsSystemDirectory(p)))
        {
            try
            {
                foreach (var full in Directory.EnumerateFiles(dir).Take(4000))
                {
                    var ext = Path.GetExtension(full).ToLowerInvariant();
                    if (ext is not ".exe" and not ".cmd" and not ".bat" and not ".ps1") continue;
                    if (catalog.Any(d => d.Executables.Contains(Path.GetFileName(full), StringComparer.OrdinalIgnoreCase))) continue;
                    var name = Path.GetFileNameWithoutExtension(full);
                    var unknown = Identify(new ToolDefinition { Id = "unknown:" + name.ToLowerInvariant(), Name = name, Category = "待识别 CLI", Executables = [Path.GetFileName(full)] }, full);
                    entries.TryAdd(unknown.ToolId + "|" + unknown.HomePath, unknown);
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        var results = entries.Values.ToList();
        foreach (var group in results.GroupBy(t => t.ToolId))
        {
            var definition = catalog.FirstOrDefault(d => d.Id == group.Key);
            if (definition is null) continue;
            var actual = ResolveCommand(definition.Executables);
            foreach (var install in group) install.IsActive = actual is not null && SamePath(install.ExecutablePath, actual);
        }
        var known = results.Where(t => !t.ToolId.StartsWith("unknown:")).ToList();
        using var semaphore = new SemaphoreSlim(Math.Clamp(settings.ProbeConcurrency, 1, 12));
        var cacheWrites = new System.Collections.Concurrent.ConcurrentDictionary<string, VersionCacheEntry>();
        int completed = 0, reused = 0;
        await Task.WhenAll(known.Select(async installation =>
        {
            await semaphore.WaitAsync(cancellation);
            try
            {
                var definition = catalog.First(d => d.Id == installation.ToolId);
                var fingerprint = Fingerprint(installation, definition);
                var checkedAt = DateTimeOffset.UtcNow;
                if (!fullScan && versionCache.TryGetValue(installation.Id, out var entry) && entry.Fingerprint == fingerprint && entry.Updated > DateTimeOffset.UtcNow.AddHours(-Math.Clamp(settings.VersionCacheHours, 1, 168)))
                {
                    installation.Version = entry.Version; checkedAt = entry.Updated; Interlocked.Increment(ref reused);
                }
                else await ReadVersionAsync(installation, definition, Math.Clamp(settings.ProbeTimeoutSeconds, 2, 60), cancellation);
                if (installation.Version is not ("检测超时" or "入口不可用" or "无法读取" or "未检测")) cacheWrites[installation.Id] = new(fingerprint, installation.Version, checkedAt);
                progress?.Invoke($"检查 {Interlocked.Increment(ref completed)}/{known.Count} · {installation.Name} · {installation.Version}");
            }
            finally { semaphore.Release(); }
        }));
        foreach (var dotnet in results.Where(t => t.ToolId == "dotnet").ToList())
        {
            var listed = await runner.RunAsync(new(dotnet.ExecutablePath, ["--list-sdks"], TimeoutSeconds: 8), cancellation: cancellation);
            if (!listed.Success) continue;
            var sdkVersions = listed.Output.Split('\n').Select(l => Regex.Match(l.Trim(), @"^(\S+)\s+\[(.+)\]$")).Where(m => m.Success).ToList();
            if (sdkVersions.Count > 0) results.Remove(dotnet);
            foreach (var match in sdkVersions)
                results.Add(dotnet with { Id = StableId(dotnet.Id + "|sdk|" + match.Groups[1].Value), Version = match.Groups[1].Value, SelectionVersion = match.Groups[1].Value, IsActive = dotnet.IsActive && match == sdkVersions.Last() });
            var runtimes = await runner.RunAsync(new(dotnet.ExecutablePath, ["--list-runtimes"], TimeoutSeconds: 8), cancellation: cancellation);
            foreach (var line in runtimes.Output.Split('\n'))
            {
                var match = Regex.Match(line.Trim(), @"^(\S+)\s+(\S+)\s+\[(.+)\]$");
                if (match.Success) results.Add(dotnet with { Id = StableId(dotnet.Id + "|runtime|" + line.Trim()), ToolId = "dotnet-runtime", Name = match.Groups[1].Value, Version = match.Groups[2].Value + " · Runtime", IsActive = false, Owner = ".NET 安装目录" });
            }
        }
        var rustup = results.FirstOrDefault(t => t.ToolId == "rustup" && t.IsActive) ?? results.FirstOrDefault(t => t.ToolId == "rustup");
        if (rustup is not null)
        {
            var list = await runner.RunAsync(new(rustup.ExecutablePath, ["toolchain", "list"], TimeoutSeconds: 8), cancellation: cancellation);
            foreach (var line in list.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var toolchain = line.Split(' ')[0].Trim();
                if (string.IsNullOrWhiteSpace(toolchain) || toolchain.Contains("error")) continue;
                var which = await runner.RunAsync(new(rustup.ExecutablePath, ["which", "--toolchain", toolchain, "rustc"], TimeoutSeconds: 8), cancellation: cancellation);
                if (!which.Success || !File.Exists(which.Output.Trim())) continue;
                var identified = Identify(catalog.First(t => t.Id == "rust"), which.Output.Trim());
                results.RemoveAll(t => t.ToolId == "rust" && SamePath(t.RealPath, identified.RealPath));
                results.Add(identified with { SelectionVersion = toolchain, Version = toolchain, Source = "rustup", IsActive = line.Contains("default") });
            }
        }
        Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
        var tempCache = cachePath + ".tmp";
        await File.WriteAllTextAsync(tempCache, JsonSerializer.Serialize(cacheWrites, JsonDefaults.Options), cancellation); File.Move(tempCache, cachePath, true);
        progress?.Invoke($"发现 {results.Select(r => r.ToolId).Distinct().Count()} 种工具、{results.Count} 个入口 · 复用 {reused} 条版本记录 · {stopwatch.Elapsed.TotalSeconds:F1} 秒");
        return results.OrderBy(t => t.Category).ThenBy(t => t.Name).ThenByDescending(t => t.IsActive).ThenBy(t => t.HomePath).ToList();
    }

    public ToolInstallation Identify(ToolDefinition definition, string executable)
    {
        executable = Path.GetFullPath(executable);
        var dir = Path.GetDirectoryName(executable)!;
        var home = dir;
        if (definition.Id is "java" or "flutter" or "dart" or "go" or "maven" or "gradle" && Path.GetFileName(dir).Equals("bin", StringComparison.OrdinalIgnoreCase)) home = Path.GetDirectoryName(dir)!;
        if (definition.Id == "android") home = FindAndroidRoot(dir);
        var real = executable;
        try { real = new FileInfo(executable).ResolveLinkTarget(true)?.FullName ?? executable; } catch (IOException) { }
        string? owner = null;
        if (executable.Contains("codex-runtimes", StringComparison.OrdinalIgnoreCase) || executable.Contains("\\resources\\", StringComparison.OrdinalIgnoreCase)) owner = "应用运行环境";
        else if (executable.Contains("\\msys", StringComparison.OrdinalIgnoreCase)) owner = "MSYS2";
        else if (executable.Contains("\\flutter\\", StringComparison.OrdinalIgnoreCase) && definition.Id == "dart") owner = "Flutter";
        else if (executable.Contains("\\jbr\\", StringComparison.OrdinalIgnoreCase)) owner = "JetBrains Runtime";
        else if (definition.Id is "npm" or "pip" or "cargo") owner = definition.Id switch { "npm" => "Node.js", "pip" => "Python", _ => "Rust" };
        var managed = HubPaths.IsWithin(home, paths.Sdks) && File.Exists(Path.Combine(home, ".devhub-install.json"));
        var source = managed ? "DevHub" : executable.Contains("\\scoop\\", StringComparison.OrdinalIgnoreCase) ? "Scoop" : executable.Contains("\\chocolatey\\", StringComparison.OrdinalIgnoreCase) ? "Chocolatey" : executable.Contains("\\WindowsApps\\", StringComparison.OrdinalIgnoreCase) ? "Windows 应用入口" : "本地目录";
        if (definition.Id == "python" && HubPaths.IsWithin(home, Path.Combine(paths.Sdks, "python"))) source = "uv";
        if (executable.Contains("\\Common Files\\Oracle\\Java\\", StringComparison.OrdinalIgnoreCase)) owner = "Oracle 命令入口";
        var vendor = "";
        var releaseFile = Path.Combine(home, "release");
        if (definition.Id == "java" && File.Exists(releaseFile))
        {
            var release = ReadReleaseFile(releaseFile);
            release.TryGetValue("IMPLEMENTOR", out vendor);
        }
        return new()
        {
            Id = StableId(definition.Id + "|" + home), ToolId = definition.Id, Name = definition.Name, Category = definition.Category,
            ExecutablePath = executable, HomePath = home, RealPath = real, Source = source, Owner = owner, Vendor = vendor ?? "",
            Managed = managed, IsJdk = definition.Id == "java" && File.Exists(Path.Combine(home, "bin", "javac.exe"))
        };
    }

    private async Task ReadVersionAsync(ToolInstallation installation, ToolDefinition definition, int timeoutSeconds, CancellationToken cancellation)
    {
        try
        {
            if (installation.ExecutablePath.Contains("\\WindowsApps\\", StringComparison.OrdinalIgnoreCase) && definition.Id is "python" or "pip")
            { installation.Version = "应用执行别名"; return; }
            var release = Path.Combine(installation.HomePath, "release");
            if (installation.ToolId == "java" && File.Exists(release))
            {
                var data = ReadReleaseFile(release);
                installation.Version = data.GetValueOrDefault("JAVA_VERSION", "未知版本") + (installation.IsJdk ? " · JDK" : " · JRE");
                return;
            }
            if (installation.ToolId == "flutter")
            {
                var file = Path.Combine(installation.HomePath, "bin", "cache", "flutter.version.json");
                if (File.Exists(file))
                {
                    using var json = JsonDocument.Parse(await File.ReadAllTextAsync(file, cancellation));
                    installation.Version = json.RootElement.GetProperty("frameworkVersion").GetString() + " · " + json.RootElement.GetProperty("channel").GetString();
                    return;
                }
                installation.Version = "SDK 待初始化"; return;
            }
            if (installation.Owner == "应用运行环境")
            {
                var version = FileVersionInfo.GetVersionInfo(installation.ExecutablePath).ProductVersion;
                installation.Version = version ?? "应用随附"; return;
            }
            var result = await runner.RunAsync(new(installation.ExecutablePath, definition.VersionArgs, TimeoutSeconds: definition.Id == "android" ? Math.Max(15, timeoutSeconds) : timeoutSeconds), cancellation: cancellation);
            var text = result.Combined;
            if (!result.Success) { installation.Version = text.Contains("超时") ? "检测超时" : "入口不可用"; return; }
            var match = Regex.Match(text, @"\d+\.\d+(?:\.\d+)?(?:[.+_-][\w.-]+)?");
            installation.Version = match.Success ? match.Value : text.Split('\n').FirstOrDefault()?.Trim() ?? "未知版本";
        }
        catch (Exception e) when (e is IOException or InvalidOperationException or System.ComponentModel.Win32Exception or JsonException) { installation.Version = "无法读取"; }
    }

    public static Dictionary<string, string> ReadReleaseFile(string path) => File.ReadLines(path).Where(l => l.Contains('='))
        .Select(l => l.Split('=', 2)).GroupBy(p => p[0]).ToDictionary(g => g.Key, g => g.Last()[1].Trim('"'));
    public static string Fingerprint(ToolInstallation installation, ToolDefinition definition)
    {
        var files = new[] { installation.ExecutablePath, installation.RealPath, Path.Combine(installation.HomePath, "release"), Path.Combine(installation.HomePath, "bin", "cache", "flutter.version.json") };
        var values = files.Distinct(StringComparer.OrdinalIgnoreCase).Select(f =>
        {
            try { var info = new FileInfo(f); return info.Exists ? $"{info.FullName}|{info.Length}|{info.LastWriteTimeUtc.Ticks}" : f + "|missing"; }
            catch (IOException) { return f + "|unavailable"; }
        });
        return StableId(string.Join(';', values) + "|" + string.Join('|', definition.VersionArgs));
    }
    public static string StableId(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value.ToLowerInvariant())))[..20].ToLowerInvariant();
    public static bool SamePath(string a, string b) => string.Equals(Path.GetFullPath(a).TrimEnd('\\'), Path.GetFullPath(b).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
    public static List<string> EnvironmentPaths() => (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries)
        .Select(p => Environment.ExpandEnvironmentVariables(p.Trim().Trim('"'))).Where(Directory.Exists).ToList();
    public static string? ResolveCommand(IEnumerable<string> names)
    {
        foreach (var directory in EnvironmentPaths())
            foreach (var name in names)
                if (File.Exists(Path.Combine(directory, name))) return Path.Combine(directory, name);
        return null;
    }
    public static string FindAndroidRoot(string path)
    {
        for (var dir = new DirectoryInfo(path); dir is not null; dir = dir.Parent)
            if (Directory.Exists(Path.Combine(dir.FullName, "cmdline-tools")) || Directory.Exists(Path.Combine(dir.FullName, "platform-tools"))) return dir.FullName;
        return Path.GetFullPath(Path.Combine(path, "..", "..", ".."));
    }
    private static bool IsSystemDirectory(string path) => path.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.Windows) + "\\", StringComparison.OrdinalIgnoreCase);
    private static void AddTree(string root, HashSet<string> dirs, int depth, CancellationToken cancellation)
    {
        if (!Directory.Exists(root) || dirs.Count > 16000) return;
        var queue = new Queue<(string Path, int Depth)>(); queue.Enqueue((root, 0));
        while (queue.TryDequeue(out var item))
        {
            if (dirs.Count >= 16000) break;
            cancellation.ThrowIfCancellationRequested();
            dirs.Add(item.Path);
            if (item.Depth >= depth) continue;
            try
            {
                foreach (var child in Directory.EnumerateDirectories(item.Path).Take(200))
                {
                    var name = Path.GetFileName(child);
                    if (name is "node_modules" or ".git" or "site-packages" or "cache" or "caches" or "lib" or "include" or "jmods" or "licenses" or "src" or "system-images") continue;
                    if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) != 0) continue;
                    queue.Enqueue((child, item.Depth + 1));
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static IEnumerable<string> ReadInstallations()
    {
        var result = new List<string>();
        foreach (var hive in new[] { Registry.LocalMachine, Registry.CurrentUser })
            foreach (var root in new[] { @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall", @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall" })
            {
                try
                {
                    using var key = hive.OpenSubKey(root);
                    foreach (var name in key?.GetSubKeyNames() ?? [])
                    {
                        using var item = key!.OpenSubKey(name);
                        var display = item?.GetValue("DisplayName") as string ?? "";
                        if (!Regex.IsMatch(display, "Java|JDK|Python|Flutter|Node.js|Go Programming|Temurin|Zulu|Corretto|Git|CMake|LLVM|Miniconda|Anaconda|.NET SDK", RegexOptions.IgnoreCase)) continue;
                        if (item?.GetValue("InstallLocation") is string location && Directory.Exists(location)) result.Add(location);
                    }
                }
                catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException) { }
            }
        return result;
    }
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static IEnumerable<string> ReadPythonInstallations()
    {
        var results = new List<string>();
        foreach (var hive in new[] { Registry.CurrentUser, Registry.LocalMachine })
            try
            {
                using var companies = hive.OpenSubKey(@"SOFTWARE\Python");
                foreach (var company in companies?.GetSubKeyNames() ?? [])
                {
                    using var tags = companies!.OpenSubKey(company);
                    foreach (var tag in tags?.GetSubKeyNames() ?? [])
                    {
                        using var install = tags!.OpenSubKey(tag + @"\InstallPath");
                        if (install?.GetValue("") is string location) results.Add(location);
                    }
                }
            }
            catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException) { }
        return results;
    }
}
