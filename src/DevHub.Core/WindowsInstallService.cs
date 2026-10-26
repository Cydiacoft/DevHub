using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace DevHub.Core;

public sealed record InstalledApplication(string Name, string Command, string[] Arguments);
[SupportedOSPlatform("windows")]
public static class WindowsInstallService
{
    public static InstalledApplication? FindUninstaller(ToolInstallation installation)
    {
        if (installation.Owner is not null || installation.Managed) return null;
        foreach (var hive in new[] { Registry.LocalMachine, Registry.CurrentUser })
            foreach (var root in new[] { @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall", @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall" })
            {
                try
                {
                    using var key = hive.OpenSubKey(root);
                    foreach (var name in key?.GetSubKeyNames() ?? [])
                    {
                        using var app = key!.OpenSubKey(name);
                        if (app?.GetValue("InstallLocation") is not string location || string.IsNullOrWhiteSpace(location)) continue;
                        if (!DiscoveryService.SamePath(location, installation.HomePath)) continue;
                        var display = app.GetValue("DisplayName") as string ?? "";
                        var registeredVersion = ProjectService.CleanVersion(app.GetValue("DisplayVersion") as string ?? "");
                        var selectedVersion = ProjectService.CleanVersion(installation.Version);
                        if (installation.ToolId == "dotnet" && (!display.Contains("SDK", StringComparison.OrdinalIgnoreCase) || registeredVersion != selectedVersion)) continue;
                        if (selectedVersion.Length > 0 && registeredVersion.Length > 0 && selectedVersion != registeredVersion) continue;
                        if (app.GetValue("UninstallString") is not string uninstall) continue;
                        var parts = SplitCommand(uninstall);
                        if (parts.Length == 0) continue;
                        var exe = Environment.ExpandEnvironmentVariables(parts[0]);
                        if (Path.GetFileName(exe).Equals("msiexec.exe", StringComparison.OrdinalIgnoreCase))
                        {
                            exe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "msiexec.exe");
                            return new(app.GetValue("DisplayName") as string ?? installation.Name, exe, parts.Skip(1).Select(p => p.StartsWith("/I", StringComparison.OrdinalIgnoreCase) ? "/X" + p[2..] : p).ToArray());
                        }
                        if (Path.IsPathRooted(exe) && File.Exists(exe) && Path.GetExtension(exe).Equals(".exe", StringComparison.OrdinalIgnoreCase))
                            return new(app.GetValue("DisplayName") as string ?? installation.Name, exe, parts.Skip(1).ToArray());
                    }
                }
                catch (Exception e) when (e is UnauthorizedAccessException or System.Security.SecurityException or IOException) { }
            }
        return null;
    }
    public static async Task LaunchUninstallerAsync(InstalledApplication application)
    {
        var info = new ProcessStartInfo(application.Command) { UseShellExecute = true };
        foreach (var argument in application.Arguments) info.ArgumentList.Add(argument);
        using var process = Process.Start(info) ?? throw new InvalidOperationException("无法启动原安装器。");
        await process.WaitForExitAsync();
        if (process.ExitCode != 0 && process.ExitCode != 3010) throw new InvalidOperationException($"原安装器退出码：{process.ExitCode}。请检查安装器窗口。 ");
    }
    private static string[] SplitCommand(string value)
    {
        var ptr = CommandLineToArgvW(value, out var count); if (ptr == IntPtr.Zero) return [];
        try { return Enumerable.Range(0, count).Select(i => Marshal.PtrToStringUni(Marshal.ReadIntPtr(ptr, i * IntPtr.Size)) ?? "").ToArray(); }
        finally { LocalFree(ptr); }
    }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr CommandLineToArgvW(string command, out int count);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr memory);
}
