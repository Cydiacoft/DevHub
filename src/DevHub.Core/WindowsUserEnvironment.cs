using System.Runtime.InteropServices;
using System.Security.Principal;

namespace DevHub.Core;

internal static class WindowsUserEnvironment
{
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public static Dictionary<string, string> Read()
    {
        using var identity = WindowsIdentity.GetCurrent();
        if (!CreateEnvironmentBlock(out var block, identity.Token, false)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "无法读取 Windows 用户环境。");
        try
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (var pointer = block; ;)
            {
                var value = Marshal.PtrToStringUni(pointer)!; if (value.Length == 0) break;
                var separator = value.IndexOf('='); if (separator > 0) result[value[..separator]] = value[(separator + 1)..];
                pointer = IntPtr.Add(pointer, (value.Length + 1) * 2);
            }
            return result;
        }
        finally { DestroyEnvironmentBlock(block); }
    }
    [DllImport("userenv.dll", SetLastError = true)]
    private static extern bool CreateEnvironmentBlock(out IntPtr environment, IntPtr token, bool inherit);
    [DllImport("userenv.dll")]
    private static extern bool DestroyEnvironmentBlock(IntPtr environment);
}
