using Microsoft.Win32;

namespace DevHub.Core;

public static class UserDirectories
{
    public static string LocalAppData => ShellFolder("Local AppData", "Local", Environment.SpecialFolder.LocalApplicationData);
    public static string RoamingAppData => ShellFolder("AppData", "Roaming", Environment.SpecialFolder.ApplicationData);
    private static string ShellFolder(string name, string conventionalName, Environment.SpecialFolder fallback)
    {
        if (OperatingSystem.IsWindows())
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\User Shell Folders");
                if (key?.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) is string registered)
                {
                    var directory = Environment.ExpandEnvironmentVariables(registered);
                    if (Directory.Exists(directory)) return directory;
                }
            }
            catch (Exception e) when (e is UnauthorizedAccessException or System.Security.SecurityException) { }
            var profilePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "AppData", conventionalName);
            if (Directory.Exists(profilePath)) return profilePath;
        }
        return Environment.GetFolderPath(fallback);
    }
}
