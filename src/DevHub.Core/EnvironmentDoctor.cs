using System.Text.Json;
using System.Text.RegularExpressions;
using Tomlyn;
using Tomlyn.Model;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace DevHub.Core;

public sealed record CommandCandidate(string Path, string RealPath, int Order, string Source);
public sealed record CommandTrace(string ToolId, string Name, string Command, List<CommandCandidate> Candidates, string? ExpectedPath, string Explanation)
{
    public string? SelectedPath => Candidates.FirstOrDefault()?.Path;
}
public sealed record EnvironmentEvidence(string Name, List<CommandTrace> Commands, Dictionary<string, string> Variables, List<Diagnostic> Issues);

public sealed class EnvironmentDoctor(HubPaths paths, ICommandRunner runner)
{
    public static readonly string[] VisibleVariables = ["PATH", "PATHEXT", "JAVA_HOME", "JDK_HOME", "PYTHONHOME", "PYTHONPATH", "VIRTUAL_ENV", "FLUTTER_ROOT", "ANDROID_HOME", "ANDROID_SDK_ROOT", "GOROOT", "GOTOOLCHAIN", "RUSTUP_TOOLCHAIN", "DOTNET_ROOT", "NVM_HOME", "NVM_SYMLINK"];
    public static Dictionary<string, string> CurrentEnvironment() => Environment.GetEnvironmentVariables().Cast<System.Collections.DictionaryEntry>().ToDictionary(p => (string)p.Key, p => (string)p.Value!, StringComparer.OrdinalIgnoreCase);

    public static List<CommandCandidate> Resolve(string command, IReadOnlyDictionary<string, string> environment)
    {
        var result = new List<CommandCandidate>();
        var extensions = (environment.GetValueOrDefault("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD").Split(';', StringSplitOptions.RemoveEmptyEntries);
        foreach (var directory in (environment.GetValueOrDefault("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var extension in Path.HasExtension(command) ? new[] { "" } : extensions.Concat(new[] { ".ps1" }))
            {
                try
                {
                    var candidate = Path.GetFullPath(Path.Combine(directory.Trim().Trim('"'), command + extension.ToLowerInvariant()));
                    if (!File.Exists(candidate) || result.Any(p => DiscoveryService.SamePath(p.Path, candidate))) continue;
                    result.Add(new(candidate, RealPath(candidate), result.Count + 1, "PATH · " + directory));
                }
                catch (Exception e) when (e is ArgumentException or IOException or NotSupportedException) { }
            }
        }
        return result;
    }
    public async Task<EnvironmentEvidence> InspectAsync(string name, IReadOnlyDictionary<string, string> environment, IReadOnlyList<ToolDefinition> catalog,
        IReadOnlyList<ToolInstallation> inventory, ProjectProfile? project = null, CancellationToken ct = default)
    {
        var commands = catalog.Where(d => !d.Id.StartsWith("unknown:")).Select(d => (d.Id, d.Name, Command: Path.GetFileNameWithoutExtension(d.Executables.First()))).ToList();
        commands.Add(("java", "Java 编译器", "javac"));
        var resolution = new Dictionary<string, List<CommandCandidate>>(StringComparer.OrdinalIgnoreCase);
        var resolutionFailure = "";
        if (OperatingSystem.IsWindows())
        {
            // Get-Command resolves applications/scripts only; this never runs the discovered commands or user profiles.
            var names = string.Join(',', commands.Select(c => ProcessRunner.PowerShellLiteral(c.Command)).Distinct());
            var script = "[Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false); $OutputEncoding = [Console]::OutputEncoding; $result = foreach ($n in @(" + names + ")) { $items = @(Get-Command -Name $n -CommandType Application,ExternalScript -All -ErrorAction SilentlyContinue); [pscustomobject]@{ name=$n; paths=@($items | ForEach-Object { $_.Path }) } }; ConvertTo-Json -InputObject @($result) -Depth 4 -Compress";
            var result = await runner.RunAsync(new(ProcessRunner.PowerShellExe, ["-NoProfile", "-NonInteractive", "-EncodedCommand", ProcessRunner.EncodedScript(script)], project?.Directory ?? paths.Root, environment, 20, PreserveHostEnvironment: true), cancellation: ct);
            if (result.Success)
            {
                using var json = JsonDocument.Parse(result.Output);
                foreach (var entry in json.RootElement.EnumerateArray())
                {
                    var candidates = entry.GetProperty("paths").EnumerateArray().Where(p => p.ValueKind == JsonValueKind.String).Select(p => p.GetString()!).Distinct(StringComparer.OrdinalIgnoreCase)
                        .Select((p, i) => new CommandCandidate(p, RealPath(p), i + 1, "PowerShell · 无个人配置")).ToList();
                    resolution[entry.GetProperty("name").GetString()!] = candidates;
                }
            }
            else resolutionFailure = "Shell 解析失败，当前候选仅按 PATH 静态计算，不能视为已验证。";
        }
        var traces = new List<CommandTrace>(); var issues = new List<Diagnostic>();
        if (resolutionFailure.Length > 0) issues.Add(new("警告", "命令解析尚未验证", resolutionFailure));
        foreach (var command in commands)
        {
            var candidates = resolution.GetValueOrDefault(command.Command) ?? Resolve(command.Command, environment);
            candidates = candidates.Select(c => c with { Source = c.Source + " · " + PathSource(c.Path, environment, project, inventory) }).ToList();
            var selectedId = project?.Bindings.GetValueOrDefault(command.Id);
            var expected = selectedId is null ? null : inventory.FirstOrDefault(i => i.Id == selectedId)?.ExecutablePath;
            if (command.Command == "javac" && expected is not null) expected = Path.Combine(Path.GetDirectoryName(expected)!, "javac.exe");
            var explanation = candidates.Count == 0 ? "此环境未解析到命令。" : $"按此环境的命令优先级命中第 1 项，其余入口被遮挡。{(resolutionFailure.Length > 0 ? resolutionFailure : "")}";
            if (expected is not null && (candidates.Count == 0 || !DiscoveryService.SamePath(candidates[0].RealPath, RealPath(expected))))
                issues.Add(new("警告", command.Name + " 与项目绑定不一致", "项目期望：" + expected + "\n实际：" + (candidates.FirstOrDefault()?.Path ?? "未找到"), command.Id));
            if (candidates.Count > 1) explanation += " 多个入口不一定是多个独立安装，请查看真实路径。";
            traces.Add(new(command.Id, command.Name, command.Command, candidates, expected, explanation));
        }
        var java = traces.FirstOrDefault(t => t.Command == "java")?.Candidates.FirstOrDefault(); var javac = traces.FirstOrDefault(t => t.Command == "javac")?.Candidates.FirstOrDefault();
        if (environment.GetValueOrDefault("JAVA_HOME") is { Length: > 0 } javaHome)
        {
            var expected = Path.Combine(javaHome, "bin", "java.exe");
            if (!File.Exists(expected)) issues.Add(new("错误", "JAVA_HOME 已失效", javaHome, "java"));
            else if (java is null || !DiscoveryService.SamePath(RealPath(expected), java.RealPath)) issues.Add(new("警告", "JAVA_HOME 与 java 命中位置不一致", $"JAVA_HOME：{javaHome}\njava：{java?.Path ?? "未找到"}\n影响：构建工具与终端可能使用不同 Java。建议：选择 JDK 后预览修复。", "java"));
            var expectedCompiler = Path.Combine(javaHome, "bin", "javac.exe");
            if (!File.Exists(expectedCompiler)) issues.Add(new("提示", "JAVA_HOME 不含 Java 编译器", javaHome + "\n运行 Java 应用可以使用 JRE，开发项目应选择包含 javac 的 JDK。", "java"));
            else if (javac is null || !DiscoveryService.SamePath(javac.RealPath, RealPath(expectedCompiler))) issues.Add(new("警告", "javac 与 JAVA_HOME 不一致", javac?.Path ?? "未找到 javac", "java"));
        }
        foreach (var duplicate in (environment.GetValueOrDefault("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries).GroupBy(p => p.Trim().Trim('"').TrimEnd('\\'), StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
            issues.Add(new("提示", "PATH 存在重复项", duplicate.Key));
        foreach (var directory in (environment.GetValueOrDefault("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries).Select(p => p.Trim().Trim('"')).Distinct(StringComparer.OrdinalIgnoreCase))
            if (Path.IsPathFullyQualified(directory) && !directory.StartsWith(@"\\", StringComparison.Ordinal) && !Directory.Exists(directory)) issues.Add(new("提示", "PATH 目录已失效", directory));
        foreach (var variable in new[] { "FLUTTER_ROOT", "ANDROID_HOME", "ANDROID_SDK_ROOT", "GOROOT", "DOTNET_ROOT", "VIRTUAL_ENV" })
            if (environment.GetValueOrDefault(variable) is { Length: > 0 } directory && !Directory.Exists(directory)) issues.Add(new("错误", variable + " 目录不存在", directory));
        if (environment.GetValueOrDefault("ANDROID_HOME") is { } android && environment.GetValueOrDefault("ANDROID_SDK_ROOT") is { } sdk && !DiscoveryService.SamePath(android, sdk)) issues.Add(new("警告", "Android SDK 环境变量指向不同目录", $"ANDROID_HOME：{android}\nANDROID_SDK_ROOT：{sdk}", "android"));
        if (environment.GetValueOrDefault("PYTHONHOME") is { Length: > 0 } pythonHome) issues.Add(new("提示", "PYTHONHOME 会影响解释器启动", pythonHome + "\n建议：通过项目虚拟环境使用明确的解释器。", "python"));
        var pythonTrace = traces.FirstOrDefault(t => t.ToolId == "python"); var pipTrace = traces.FirstOrDefault(t => t.ToolId == "pip");
        if (pythonTrace?.SelectedPath is { } python && !python.Contains("\\WindowsApps\\", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var info = await ProbeAsync(pythonTrace, environment, project?.Directory ?? paths.Root, ct);
                using var json = JsonDocument.Parse(info); var prefix = json.RootElement.GetProperty("prefix").GetString()!;
                if (pipTrace?.SelectedPath is not null)
                {
                    var pipInfo = await ProbeAsync(pipTrace, environment, project?.Directory ?? paths.Root, ct);
                    var module = Regex.Match(pipInfo, @"\bfrom\s+(.+?)\s+\(python\s+", RegexOptions.IgnoreCase);
                    if (module.Success && !HubPaths.IsWithin(module.Groups[1].Value.Trim(), prefix)) issues.Add(new("警告", "python 与 pip 使用不同环境", "Python 环境：" + prefix + "\npip 模块：" + module.Groups[1].Value + "\n建议：使用选定解释器的 python -m pip，或在项目环境中绑定正确解释器。", "python"));
                }
            }
            catch (Exception e) when (e is InvalidOperationException or JsonException or IOException or System.ComponentModel.Win32Exception) { issues.Add(new("警告", "Python / pip 对应关系尚未验证", "解释器或 pip 查询失败，请查看明确入口版本查询结果。", "python")); }
        }
        var node = traces.FirstOrDefault(t => t.ToolId == "node")?.SelectedPath;
        foreach (var id in new[] { "npm", "pnpm", "yarn" })
        {
            var entry = traces.FirstOrDefault(t => t.ToolId == id)?.SelectedPath;
            if (node is not null && entry is not null && !HubPaths.IsWithin(entry, Path.GetDirectoryName(node)!)) issues.Add(new("提示", id + " 与 Node.js 不在同一目录", entry + "\nNode.js：" + node + "\n可能是全局包或版本管理器入口；包操作应绑定明确 Node.js。", id));
        }
        var flutter = traces.FirstOrDefault(t => t.ToolId == "flutter")?.SelectedPath;
        var dart = traces.FirstOrDefault(t => t.ToolId == "dart")?.SelectedPath;
        if (flutter is not null && dart is not null)
        {
            var bundled = Path.Combine(Path.GetDirectoryName(flutter)!, "cache", "dart-sdk", "bin", "dart.exe");
            if (File.Exists(bundled) && !DiscoveryService.SamePath(RealPath(dart), RealPath(bundled))) issues.Add(new("提示", "终端 Dart 与 Flutter 随附 Dart 不同", "终端：" + dart + "\nFlutter 随附：" + bundled + "\n独立 Dart 选择不会替换 Flutter 内部的 Dart SDK。", "dart"));
        }
        var androidHome = environment.GetValueOrDefault("ANDROID_HOME") ?? environment.GetValueOrDefault("ANDROID_SDK_ROOT");
        var adb = traces.FirstOrDefault(t => t.ToolId == "adb")?.SelectedPath;
        if (androidHome is not null && adb is not null && !HubPaths.IsWithin(RealPath(adb), androidHome)) issues.Add(new("警告", "ADB 与 Android SDK 目录不一致", "ADB：" + adb + "\nSDK：" + androidHome, "android"));
        foreach (var pair in new[] { (Id: "go", Variable: "GOROOT"), (Id: "dotnet", Variable: "DOTNET_ROOT") })
        {
            var entry = traces.FirstOrDefault(t => t.ToolId == pair.Id)?.SelectedPath;
            if (entry is not null && environment.GetValueOrDefault(pair.Variable) is { Length: > 0 } root && !HubPaths.IsWithin(RealPath(entry), root)) issues.Add(new("提示", pair.Variable + " 与命令入口目录不同", pair.Variable + "：" + root + "\n入口：" + entry + "\n启动器或项目工具链选择可能继续覆盖版本，请结合项目声明检查。", pair.Id));
        }
        if (environment.GetValueOrDefault("RUSTUP_TOOLCHAIN") is { Length: > 0 } rust) issues.Add(new("提示", "Rust 工具链由 RUSTUP_TOOLCHAIN 选择", rust + "\n此选择优先于 rustup 默认工具链；不触发缺失工具链安装。", "rust"));
        if (traces.Any(t => t.ToolId == "rust" && t.SelectedPath is not null)) issues.Add(new("提示", "Rust 入口可能是 rustup 代理", "PATH 入口、RUSTUP_TOOLCHAIN、目录 override、rust-toolchain 文件与默认工具链共同影响最终版本。本次不会安装或更新工具链。", "rust"));
        if (environment.GetValueOrDefault("GOTOOLCHAIN") is { Length: > 0 } goToolchain) issues.Add(new("提示", "Go 使用 GOTOOLCHAIN 选择规则", goToolchain + "\n自动模式可能下载工具链，诊断不触发下载。", "go"));
        if (project is not null) issues.AddRange(ProjectDeclarations.Inspect(project, inventory));
        return new(name, traces, VisibleVariables.Where(environment.ContainsKey).ToDictionary(k => k, k => environment[k], StringComparer.OrdinalIgnoreCase), issues);
    }
    public async Task<string> ProbeAsync(CommandTrace trace, IReadOnlyDictionary<string, string> environment, string directory, CancellationToken ct = default)
    {
        var file = trace.SelectedPath ?? throw new InvalidOperationException("未找到命令入口。");
        if (trace.ToolId is "flutter" or "dart" or "rust" or "cargo" or "rustup" or "go")
            return "入口已解析。此工具可能按项目初始化或下载工具链；诊断不会触发这些操作。请结合版本清单和项目声明检查。";
        if (trace.ToolId == "python" && file.Contains("\\WindowsApps\\", StringComparison.OrdinalIgnoreCase)) return "Windows 应用执行别名，不会启动商店入口。";
        var definition = DiscoveryService.BuiltInCatalog().Tools.FirstOrDefault(t => t.Id == trace.ToolId) ?? throw new InvalidOperationException("没有内置的安全查询规则。");
        IReadOnlyList<string> args = trace.Command == "javac" ? ["-version"] : definition.VersionArgs;
        if (trace.ToolId == "python") args = ["-c", "import sys,json; print(json.dumps({'executable':sys.executable,'version':sys.version,'prefix':sys.prefix,'base_prefix':sys.base_prefix}))"];
        if (trace.ToolId == "pip") args = ["--version"];
        if (trace.ToolId == "dotnet") args = ["--version"];
        var probeEnvironment = new Dictionary<string, string>(environment, StringComparer.OrdinalIgnoreCase);
        if (trace.ToolId is "python" or "pip") probeEnvironment["PYTHONIOENCODING"] = "utf-8";
        // Cache redirection does not change command resolution or SDK selection.
        foreach (var pair in paths.CacheEnvironment().Where(p => p.Key is "TEMP" or "TMP" or "PIP_CACHE_DIR" or "npm_config_cache" or "NUGET_PACKAGES" or "NUGET_HTTP_CACHE_PATH" or "GRADLE_USER_HOME")) probeEnvironment[pair.Key] = pair.Value;
        var result = await runner.RunAsync(new(file, args, directory, probeEnvironment, 15, PreserveHostEnvironment: true), cancellation: ct);
        result.EnsureSuccess(); return result.Combined;
    }
    public static string RealPath(string path)
    {
        try
        {
            if (OperatingSystem.IsWindows() && File.Exists(path))
            {
                using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                var buffer = new StringBuilder(32768); var length = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Capacity, 0);
                if (length > 0 && length < buffer.Capacity)
                {
                    var final = buffer.ToString(); return final.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase) ? @"\\" + final[8..] : final.StartsWith(@"\\?\", StringComparison.Ordinal) ? final[4..] : final;
                }
            }
            return new FileInfo(path).ResolveLinkTarget(true)?.FullName ?? path;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return path; }
    }
    private static string PathSource(string file, IReadOnlyDictionary<string, string> environment, ProjectProfile? project, IReadOnlyList<ToolInstallation> inventory)
    {
        if (project is not null && project.Bindings.Values.Any(id => inventory.Any(i => i.Id == id && HubPaths.IsWithin(file, i.HomePath)))) return "项目版本绑定";
        if (environment.GetValueOrDefault("VIRTUAL_ENV") is { Length: > 0 } venv && HubPaths.IsWithin(file, venv)) return "虚拟环境";
        var scopes = new List<string>();
        if (OperatingSystem.IsWindows())
            foreach (var target in new[] { EnvironmentVariableTarget.Machine, EnvironmentVariableTarget.User })
                foreach (var entry in (Environment.GetEnvironmentVariable("PATH", target) ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
                {
                    try
                    {
                        var expanded = Regex.Replace(entry.Trim().Trim('"'), "%([^%]+)%", match => environment.GetValueOrDefault(match.Groups[1].Value) ?? match.Value);
                        if (DiscoveryService.SamePath(Path.GetDirectoryName(file)!, expanded)) { scopes.Add(target == EnvironmentVariableTarget.Machine ? "系统 PATH" : "用户 PATH"); break; }
                    }
                    catch (Exception e) when (e is ArgumentException or NotSupportedException) { }
                }
        return scopes.Count > 0 ? string.Join(" / ", scopes) : "进程或项目注入的 PATH";
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(SafeFileHandle handle, StringBuilder path, uint size, uint flags);
}

public static class ProjectDeclarations
{
    public static List<Diagnostic> Inspect(ProjectProfile project, IReadOnlyList<ToolInstallation> inventory)
    {
        var result = new List<Diagnostic>();
        foreach (var binding in project.Bindings)
        {
            var selected = inventory.FirstOrDefault(i => i.Id == binding.Value);
            if (selected is null) { result.Add(new("错误", "项目绑定的版本已不存在", binding.Key)); continue; }
            try
            {
                var declaration = Read(project.Directory, binding.Key);
                if (declaration is null) continue;
                var selectedVersion = ProjectService.CleanVersion(selected.Version);
                if (declaration.Value.Range) result.Add(new("提示", selected.Name + " 使用版本范围 / 渠道声明", declaration.Value.File + "：" + declaration.Value.Version + "\n由所属工具解释，不按字符串包含判断匹配。", binding.Key));
                else if (selectedVersion != declaration.Value.Version.TrimStart('v') && !(binding.Key is "python" or "node" && selectedVersion.StartsWith(declaration.Value.Version.TrimStart('v') + ".", StringComparison.Ordinal)))
                    result.Add(new("警告", selected.Name + " 的项目声明与绑定不同", declaration.Value.File + "：" + declaration.Value.Version + "\n绑定：" + selected.Version + "\n建议：在项目页预览声明修改，或选择匹配版本。", binding.Key));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or TomlException or ArgumentException or KeyNotFoundException or InvalidOperationException or InvalidCastException)
            { result.Add(new("警告", selected.Name + " 项目声明无法解析", "请检查声明文件，未据此判断环境正常。", binding.Key)); }
        }
        foreach (var file in new[] { "mvnw.cmd", "gradlew.bat" }) if (File.Exists(Path.Combine(project.Directory, file))) result.Add(new("提示", "项目使用构建 Wrapper", file + " 可能覆盖全局构建工具版本；诊断不下载或运行 Wrapper。"));
        var gradle = Path.Combine(project.Directory, "gradle.properties");
        try
        {
            if (File.Exists(gradle))
                foreach (var line in File.ReadLines(gradle).Where(l => l.TrimStart().StartsWith("org.gradle.java.home=", StringComparison.Ordinal))) result.Add(new("提示", "Gradle 显式指定 JVM", gradle + "\n" + line, "java"));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { result.Add(new("警告", "Gradle 项目配置暂时无法读取", gradle, "gradle")); }
        if (File.Exists(Path.Combine(project.Directory, ".node-version")) && File.Exists(Path.Combine(project.Directory, ".nvmrc"))) result.Add(new("提示", "项目包含多种 Node 版本声明", ".node-version 与 .nvmrc 的生效关系由所用版本管理器决定；本次不自动覆盖声明。", "node"));
        foreach (var file in new[] { ".tool-versions", ".sdkmanrc", ".idea/misc.xml", ".idea/gradle.xml" })
            if (File.Exists(Path.Combine(project.Directory, file))) result.Add(new("提示", "存在额外的项目版本选择来源", file + " 可能覆盖终端或 IDE 的版本选择；需要在所属管理器 / IDE 中核对。"));
        if (File.Exists(Path.Combine(project.Directory, "pubspec.yaml"))) result.Add(new("提示", "Dart / Flutter 受项目 SDK 约束影响", "pubspec.yaml 中的 SDK 范围由 pub 解析，本次不执行依赖解析或下载。", "dart"));
        var goMod = Path.Combine(project.Directory, "go.mod");
        try
        {
            if (File.Exists(goMod))
            {
                var selected = Regex.Match(File.ReadAllText(goMod), @"(?m)^\s*toolchain\s+(\S+)");
                if (selected.Success) result.Add(new("提示", "Go 项目指定首选工具链", "go.mod toolchain：" + selected.Groups[1].Value + "\n由 GOTOOLCHAIN 模式决定是否采用，本次不下载工具链。", "go"));
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { result.Add(new("警告", "Go 项目配置暂时无法读取", goMod, "go")); }
        return result;
    }
    internal static (string File, string Version, bool Range)? Read(string directory, string id)
    {
        foreach (var name in id switch { "python" => new[] { ".python-version" }, "node" => [".node-version", ".nvmrc"], "dotnet" => ["global.json"], "rust" => ["rust-toolchain.toml", "rust-toolchain"], "flutter" => [".fvmrc"], "go" => ["go.mod"], _ => Array.Empty<string>() })
        {
            var file = Path.Combine(directory, name); if (!File.Exists(file)) continue;
            var text = File.ReadAllText(file); string? version = null; var range = false;
            if (name == "global.json") { using var json = JsonDocument.Parse(text); version = json.RootElement.GetProperty("sdk").GetProperty("version").GetString(); range = json.RootElement.GetProperty("sdk").TryGetProperty("rollForward", out _); }
            else if (name == ".fvmrc") { using var json = JsonDocument.Parse(text); version = json.RootElement.GetProperty("flutter").GetString(); }
            else if (name == "rust-toolchain.toml") { var table = TomlSerializer.Deserialize<TomlTable>(text)!; version = ((TomlTable)table["toolchain"]!)["channel"]?.ToString(); }
            else if (name == "go.mod") { version = Regex.Match(text, @"(?m)^\s*go\s+(\S+)").Groups[1].Value; range = true; }
            else version = text.Split(['\r', '\n', '#'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            if (string.IsNullOrWhiteSpace(version)) throw new InvalidDataException("版本声明为空。");
            range |= !Regex.IsMatch(version.TrimStart('v'), @"^\d+(\.\d+){0,2}$");
            return (name, version, range);
        }
        if (id == "node" && File.Exists(Path.Combine(directory, "package.json")))
        {
            using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "package.json")));
            if (json.RootElement.TryGetProperty("engines", out var engines) && engines.TryGetProperty("node", out var node)) return ("package.json engines.node", node.GetString() ?? "", true);
        }
        return null;
    }
}
