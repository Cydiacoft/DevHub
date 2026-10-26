using System.Text.Json;
using System.Text.RegularExpressions;

namespace DevHub.Core;

public sealed class PackageService(HubPaths paths, ICommandRunner runner)
{
    public async Task<List<PackageInfo>> ListPythonAsync(string python, CancellationToken ct = default)
    {
        var result = await runner.RunAsync(new(python, ["-m", "pip", "list", "--format=json", "--disable-pip-version-check"]), cancellation: ct);
        result.EnsureSuccess();
        using var json = JsonDocument.Parse(result.Output);
        return json.RootElement.EnumerateArray().Select(p => new PackageInfo(p.GetProperty("name").GetString()!, p.GetProperty("version").GetString()!)).OrderBy(p => p.Name).ToList();
    }
    public CommandSpec PythonPackageCommand(string python, string action, string name, string? workingDirectory = null)
    {
        ValidatePackage(name);
        var args = new List<string> { "-m", "pip", "--disable-pip-version-check" };
        args.AddRange(action switch { "install" => new[] { "install", name }, "upgrade" => ["install", "--upgrade", name], "uninstall" => ["uninstall", "-y", name], _ => throw new ArgumentException("无效包操作。") });
        return new(python, args, workingDirectory, TimeoutSeconds: 1200);
    }
    public static void ValidatePackage(string value)
    {
        if (!Regex.IsMatch(value, @"^[A-Za-z0-9][A-Za-z0-9._\-\[\],<>=!~+@/]*$")) throw new ArgumentException("请输入包名及可选版本，不接受命令、路径或安装选项。");
    }
    public CommandSpec NodePackageCommand(ToolInstallation node, string manager, string action, string name, string? project = null)
    {
        ValidatePackage(name);
        if (node.ToolId != "node") throw new ArgumentException("请选择 Node.js 安装。");
        var npm = Path.Combine(node.HomePath, "npm.cmd");
        var env = paths.CacheEnvironment(); env["PATH"] = node.HomePath + ";" + Environment.GetEnvironmentVariable("PATH");
        env["npm_config_prefix"] = Path.Combine(paths.Data, "node-global", node.Id);
        Directory.CreateDirectory(env["npm_config_prefix"]);
        if (manager != "npm")
        {
            var tool = Path.Combine(env["npm_config_prefix"], manager + ".cmd");
            if (!File.Exists(tool)) throw new InvalidOperationException($"请先使用 npm 为所选 Node.js 安装 {manager}（全局包）。");
            npm = tool;
        }
        var args = action switch
        {
            "install" => new List<string> { "install", name }, "upgrade" => [manager == "npm" ? "update" : "upgrade", name],
            "uninstall" => [manager == "npm" ? "uninstall" : "remove", name], _ => throw new ArgumentException("无效包操作。")
        };
        if (project is null) args.Add(manager == "yarn" ? "--global" : "--global");
        return new(npm, args, project ?? node.HomePath, env, 1200);
    }
    public async Task<string> ListNodeAsync(ToolInstallation node, string? project, CancellationToken ct)
    {
        var env = paths.CacheEnvironment(); env["PATH"] = node.HomePath + ";" + Environment.GetEnvironmentVariable("PATH");
        env["npm_config_prefix"] = Path.Combine(paths.Data, "node-global", node.Id);
        var args = new List<string> { "list", "--depth=0", "--json" }; if (project is null) args.Add("--global");
        var result = await runner.RunAsync(new(Path.Combine(node.HomePath, "npm.cmd"), args, project ?? node.HomePath, env), cancellation: ct);
        if (string.IsNullOrWhiteSpace(result.Output)) result.EnsureSuccess();
        return result.Combined;
    }

    public CommandSpec ManagerCommand(ToolDefinition definition, string manager, string action, string? explicitPackageId = null)
    {
        var package = explicitPackageId ?? manager switch { "winget" => definition.WingetId, "scoop" => definition.ScoopId, "choco" => definition.ChocoId, _ => null };
        if (string.IsNullOrWhiteSpace(package)) throw new InvalidOperationException("该来源未配置此工具，请换一个来源或打开官网。");
        var exe = DiscoveryService.ResolveCommand(manager switch { "winget" => ["winget.exe"], "scoop" => ["scoop.cmd", "scoop.ps1"], "choco" => ["choco.exe"], _ => throw new ArgumentException("未知包管理器。") })
            ?? throw new InvalidOperationException($"未找到 {manager}，请先安装或选择已有来源。");
        IReadOnlyList<string> arguments = manager switch
        {
            "winget" => new[] { action switch { "install" => "install", "update" => "upgrade", "remove" => "uninstall", _ => throw new ArgumentException("无效操作。") }, "--id", package, "--exact", "--disable-interactivity" }.Concat(action == "remove" ? [] : new[] { "--accept-package-agreements", "--accept-source-agreements", "--source", "winget" }).ToArray(),
            "scoop" => [action switch { "install" => "install", "update" => "update", "remove" => "uninstall", _ => throw new ArgumentException("无效操作。") }, package],
            "choco" => [action switch { "install" => "install", "update" => "upgrade", "remove" => "uninstall", _ => throw new ArgumentException("无效操作。") }, package, "--yes", "--no-progress", "--cache-location=" + Path.Combine(paths.Cache, "choco")],
            _ => throw new ArgumentException("未知包管理器。")
        };
        return new(exe, arguments, paths.Root, TimeoutSeconds: 1800);
    }
    public async Task VerifyManagerOwnershipAsync(ToolDefinition definition, string manager, CancellationToken ct)
    {
        var package = manager switch { "winget" => definition.WingetId, "scoop" => definition.ScoopId, "choco" => definition.ChocoId, _ => null };
        if (package is null) throw new InvalidOperationException("无法确认该工具的安装来源。");
        var exe = DiscoveryService.ResolveCommand(manager switch { "winget" => ["winget.exe"], "scoop" => ["scoop.cmd", "scoop.ps1"], _ => new[] { "choco.exe" } }) ?? throw new InvalidOperationException("未找到管理器。");
        var args = manager switch
        {
            "winget" => new[] { "list", "--id", package, "--exact", "--disable-interactivity" },
            "scoop" => ["list", package], _ => new[] { "list", "--exact", package, "--limit-output" }
        };
        var result = await runner.RunAsync(new(exe, args, TimeoutSeconds: 90), cancellation: ct);
        if (!result.Success || !result.Combined.Contains(package.Split('/').Last(), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("所选管理器未确认安装此工具。请从原安装来源操作，或在 Windows 已安装应用中卸载。");
    }

    public CommandSpec AndroidCommand(ToolInstallation android, string action, string? packageId = null)
    {
        if (android.ToolId != "android") throw new ArgumentException("请选择 Android SDK。");
        var args = new List<string> { "--sdk_root=" + android.HomePath };
        if (action == "list") args.Add("--list");
        else if (action == "update") args.Add("--update");
        else
        {
            if (string.IsNullOrWhiteSpace(packageId) || !Regex.IsMatch(packageId, @"^[A-Za-z0-9_.;+\-]+$")) throw new ArgumentException("请输入 sdkmanager 包标识，例如 platforms;android-35。");
            if (action == "remove") args.Add("--uninstall");
            else if (action != "install") throw new ArgumentException("无效 Android 操作。");
            args.Add(packageId);
        }
        return new(android.ExecutablePath, args, paths.Root, TimeoutSeconds: 1800);
    }
    public void OpenAndroidLicenses(ToolInstallation android, ToolInstallation? java)
    {
        var env = paths.CacheEnvironment();
        if (java is not null) { env["JAVA_HOME"] = java.HomePath; env["PATH"] = Path.GetDirectoryName(java.ExecutablePath) + ";" + Environment.GetEnvironmentVariable("PATH"); }
        var script = "& " + ProcessRunner.PowerShellLiteral(android.ExecutablePath) + " " + ProcessRunner.PowerShellLiteral("--sdk_root=" + android.HomePath) + " '--licenses'";
        var info = new System.Diagnostics.ProcessStartInfo(ProcessRunner.PowerShellExe) { UseShellExecute = false, CreateNoWindow = false, WorkingDirectory = paths.Root };
        foreach (var arg in new[] { "-NoProfile", "-NoExit", "-EncodedCommand", ProcessRunner.EncodedScript(script) }) info.ArgumentList.Add(arg);
        foreach (var pair in env) info.Environment[pair.Key] = pair.Value;
        System.Diagnostics.Process.Start(info);
    }
}
