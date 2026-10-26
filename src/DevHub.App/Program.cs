using System.Runtime.InteropServices;
using DevHub.Core;
using Microsoft.UI.Xaml;

namespace DevHub.App;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        try
        {
            WinRT.ComWrappersSupport.InitializeComWrappers();
            Application.Start(_ =>
            {
                SynchronizationContext.SetSynchronizationContext(new Microsoft.UI.Dispatching.DispatcherQueueSynchronizationContext(Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread()));
                new App();
            });
        }
        catch (Exception error) { ShowFailure(error); Environment.ExitCode = 1; }
    }
    internal static void ShowFailure(Exception error)
    {
        var log = StartupLifecycle.LogFailure(error);
        try { App.WriteStartupReport("failed", error.GetType().Name); } catch { }
        if (!App.SmokeTest) MessageBox(IntPtr.Zero, "DevHub 无法完成启动或运行。\n\n" + error.Message + "\n\n日志：" + log + "\n\n请保留完整发布目录；若为配置损坏，可在重新启动后使用默认配置。", "DevHub · 操作失败", 0x10);
    }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBox(IntPtr window, string message, string title, uint flags);
}
