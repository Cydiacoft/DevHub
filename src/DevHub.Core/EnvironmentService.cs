using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text.Json;
using Microsoft.Win32;

namespace DevHub.Core;

public sealed record EnvironmentValue(string Scope, string Name, string? Value, int Kind = 2);
public sealed record EnvironmentChange(EnvironmentValue Before, EnvironmentValue After);
public sealed record EnvironmentRequest(string Root, string UserSid, List<EnvironmentChange> Changes, string Description);
public sealed record EnvironmentBackup(DateTimeOffset Created, string Description, List<EnvironmentChange> Changes);

public interface IEnvironmentStore
{
    EnvironmentValue Read(string scope, string name);
    void Write(EnvironmentValue value);
    void Broadcast();
}

[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class WindowsEnvironmentStore : IEnvironmentStore
{
    public EnvironmentValue Read(string scope, string name)
    {
        using var key = Open(scope, false);
        var value = key?.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
        return new(scope, name, value, value is null ? 2 : (int)key!.GetValueKind(name));
    }
    public void Write(EnvironmentValue value)
    {
        using var key = Open(value.Scope, true) ?? throw new InvalidOperationException("无法打开环境变量配置。");
        if (value.Value is null) key.DeleteValue(value.Name, false);
        else key.SetValue(value.Name, value.Value, (RegistryValueKind)value.Kind);
    }
    private static RegistryKey? Open(string scope, bool write) => scope switch
    {
        "Machine" => Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Session Manager\Environment", write),
        "User" => write ? Registry.CurrentUser.CreateSubKey("Environment") : Registry.CurrentUser.OpenSubKey("Environment"),
        _ => throw new InvalidOperationException("无效环境变量范围。")
    };
    public void Broadcast() => SendMessageTimeout(new IntPtr(0xffff), 0x001A, IntPtr.Zero, "Environment", 2, 5000, out _);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SendMessageTimeout(IntPtr hwnd, uint msg, IntPtr wParam, string lParam, uint flags, uint timeout, out IntPtr result);
}

public sealed class EnvironmentService(HubPaths paths, IEnvironmentStore store)
{
    public static readonly string[] AllowedNames = ["Path", "JAVA_HOME", "JDK_HOME", "FLUTTER_ROOT", "GOROOT", "ANDROID_HOME", "ANDROID_SDK_ROOT"];

    public EnvironmentRequest PlanSwitch(ToolInstallation installation)
    {
        if (!File.Exists(installation.ExecutablePath)) throw new FileNotFoundException("工具入口已不存在。", installation.ExecutablePath);
        if (installation.Owner is not null) throw new InvalidOperationException("该工具由其他软件提供，请先安装独立版本后再设置为全局默认。");
        if (installation.ToolId == "java" && !installation.IsJdk) throw new InvalidOperationException("全局开发环境请选择包含 javac 的 JDK。");
        if (installation.ToolId is not ("java" or "python" or "flutter" or "dart" or "node" or "go" or "android" or "maven" or "gradle"))
            throw new InvalidOperationException("该工具请使用所属管理器或项目版本设置。");
        var changes = new List<EnvironmentChange>();
        var bin = Path.GetDirectoryName(installation.ExecutablePath)!;
        var before = store.Read("Machine", "Path");
        var entries = (before.Value ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries).ToList();
        entries.RemoveAll(e => SameExpandedPath(e, bin));
        entries.Insert(0, bin);
        if (installation.ToolId == "python" && Directory.Exists(Path.Combine(installation.HomePath, "Scripts")))
        {
            var scripts = Path.Combine(installation.HomePath, "Scripts");
            entries.RemoveAll(e => SameExpandedPath(e, scripts)); entries.Insert(1, scripts);
        }
        changes.Add(new(before, before with { Value = string.Join(';', entries) }));
        var variables = installation.ToolId switch
        {
            "java" => new[] { "JAVA_HOME" }, "go" => ["GOROOT"], "flutter" => ["FLUTTER_ROOT"],
            "android" => ["ANDROID_HOME", "ANDROID_SDK_ROOT"], _ => []
        };
        foreach (var variable in variables)
        {
            var machine = store.Read("Machine", variable);
            changes.Add(new(machine, machine with { Value = installation.HomePath }));
            var user = store.Read("User", variable);
            if (user.Value is not null) changes.Add(new(user, user with { Value = installation.HomePath }));
        }
        if (installation.ToolId == "java")
            foreach (var scope in new[] { "Machine", "User" })
            {
                var jdk = store.Read(scope, "JDK_HOME");
                if (jdk.Value is not null) changes.Add(new(jdk, jdk with { Value = installation.HomePath }));
            }
        return new(paths.Root, CurrentSid(), changes, $"将 {installation.Name} {installation.Version} 设为全局默认");
    }

    public EnvironmentRequest PlanRestore(EnvironmentBackup backup)
    {
        var changes = backup.Changes.Select(c => new EnvironmentChange(store.Read(c.Before.Scope, c.Before.Name), c.Before)).ToList();
        return new(paths.Root, CurrentSid(), changes, "恢复：" + backup.Description);
    }

    public async Task ApplyElevatedAsync(EnvironmentRequest request)
    {
        var requestDir = Path.Combine(paths.Data, "environment-requests"); Directory.CreateDirectory(requestDir);
        var requestPath = Path.Combine(requestDir, Guid.NewGuid().ToString("N") + ".json");
        await File.WriteAllTextAsync(requestPath, JsonSerializer.Serialize(request, JsonDefaults.Options));
        var helper = Path.Combine(AppContext.BaseDirectory, "helper", "DevHub.Helper.exe");
        if (!File.Exists(helper)) throw new InvalidOperationException("找不到系统配置辅助程序，请使用完整发布版本。配置尚未修改。");
        try
        {
            using var process = Process.Start(new ProcessStartInfo(helper) { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden, ArgumentList = { "--environment", requestPath } })
                ?? throw new InvalidOperationException("无法启动配置辅助程序。");
            await process.WaitForExitAsync();
            var resultPath = requestPath + ".result";
            var message = File.Exists(resultPath) ? await File.ReadAllTextAsync(resultPath) : "辅助程序没有返回结果。";
            if (process.ExitCode != 0 || message != "OK") throw new InvalidOperationException(message);
        }
        catch (System.ComponentModel.Win32Exception e) when (e.NativeErrorCode == 1223) { throw new OperationCanceledException("已取消管理员授权，配置未修改。"); }
    }

    public static void ApplyTransaction(EnvironmentRequest request, IEnvironmentStore store, HubPaths paths)
    {
        if (request.Changes.Count == 0 || request.Changes.Count > 12) throw new InvalidOperationException("环境修改请求无效。");
        foreach (var change in request.Changes)
        {
            if (!AllowedNames.Contains(change.Before.Name, StringComparer.OrdinalIgnoreCase) || change.Before.Scope is not ("Machine" or "User") ||
                change.After.Name != change.Before.Name || change.After.Scope != change.Before.Scope || change.After.Kind is not (1 or 2) ||
                (change.After.Value?.Contains('\0') ?? false)) throw new InvalidOperationException("环境修改请求包含不允许的字段。");
            if (store.Read(change.Before.Scope, change.Before.Name) != change.Before) throw new InvalidOperationException("环境配置在预览后发生变化，请重新预览。");
        }
        var backup = new EnvironmentBackup(DateTimeOffset.Now, request.Description, request.Changes);
        File.WriteAllText(Path.Combine(paths.Backups, $"environment-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.json"), JsonSerializer.Serialize(backup, JsonDefaults.Options));
        var applied = new List<EnvironmentChange>();
        try
        {
            foreach (var change in request.Changes) { applied.Add(change); store.Write(change.After); }
            foreach (var change in request.Changes)
                if (store.Read(change.After.Scope, change.After.Name) != change.After) throw new IOException("环境变量写入后验证失败。");
        }
        catch
        {
            foreach (var change in Enumerable.Reverse(applied)) store.Write(change.Before);
            store.Broadcast(); throw;
        }
        store.Broadcast();
    }
    public Dictionary<string, string> FreshEnvironment()
    {
        var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var path = new List<string>();
        foreach (var scope in new[] { "Machine", "User" })
        {
            foreach (var name in AllowedNames.Where(n => n != "Path"))
                if (store.Read(scope, name).Value is { } value) environment[name] = value;
            if (store.Read(scope, "Path").Value is { } valuePath) path.Add(valuePath);
        }
        string Expand(string value) => System.Text.RegularExpressions.Regex.Replace(value, "%([^%]+)%", m => environment.GetValueOrDefault(m.Groups[1].Value) ?? Environment.GetEnvironmentVariable(m.Groups[1].Value) ?? m.Value);
        foreach (var key in environment.Keys.ToArray()) environment[key] = Expand(environment[key]);
        environment["PATH"] = Expand(string.Join(';', path));
        return environment;
    }
    private static bool SameExpandedPath(string a, string b)
    {
        try { return DiscoveryService.SamePath(Environment.ExpandEnvironmentVariables(a.Trim('"')), b); } catch (Exception e) when (e is ArgumentException or NotSupportedException) { return false; }
    }
    public static string CurrentSid() => OperatingSystem.IsWindows() ? WindowsIdentity.GetCurrent().User?.Value ?? "" : "test";
}
