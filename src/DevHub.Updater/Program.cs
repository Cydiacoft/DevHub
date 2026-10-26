using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using DevHub.Core;

if (args.Length is not (2 or 3) || args[0] is not ("--apply" or "--recover") || args.Length == 3 && args[2] != "--test-mode") return 2;
var testMode = args.Length == 3;
UpdateRequest? request = null; UpdateJournal? journal = null; Process? started = null; var validated = false;
try
{
    request = JsonSerializer.Deserialize<UpdateRequest>(await File.ReadAllTextAsync(args[1]), JsonDefaults.Options) ?? throw new InvalidDataException("更新请求为空。");
    UpdateTransaction.Validate(request, false);
    if (!DiscoveryService.SamePath(args[1], Path.Combine(UpdateTransaction.WorkDirectory(request), "request.json"))) throw new InvalidDataException("更新请求位置无效。");
    validated = true;
    using var updateLock = new FileStream(Path.Combine(new HubPaths(request.Root).Data, "app-update.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    {
        var waitPid = int.TryParse(Environment.GetEnvironmentVariable("DEVHUB_WAIT_PID"), out var callerPid) ? callerPid : request.ProcessId;
        if (waitPid > 0)
        {
            try
            {
                using var current = Process.GetProcessById(waitPid);
                if (current.MainModule?.FileName is not { } file || !DiscoveryService.SamePath(file, Path.Combine(request.TargetDirectory, "DevHub.App.exe"))) throw new InvalidOperationException("请求进程身份已变化。");
                using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(45)); await current.WaitForExitAsync(wait.Token);
            }
            catch (ArgumentException) { }
        }
        UpdateTransaction.EnsureNoOtherInstances(request.TargetDirectory, -1);
        var journalPath = Path.Combine(UpdateTransaction.WorkDirectory(request), "journal.json");
        if (File.Exists(journalPath))
        {
            journal = JsonSerializer.Deserialize<UpdateJournal>(await File.ReadAllTextAsync(journalPath), JsonDefaults.Options)!;
            if (journal.Request != request) throw new InvalidDataException("恢复事务与请求不匹配。");
            if (journal.Status is "applying" or "awaiting-startup") { await UpdateTransaction.RollbackAsync(journal, "interrupted"); if (!testMode) StartApplication(request, null); return 0; }
            throw new InvalidOperationException("该更新事务已完成或已恢复。");
        }
        if (args[0] == "--recover") throw new InvalidOperationException("没有可恢复的更新事务。");
        journal = await UpdateTransaction.ApplyFilesAsync(request);
        var paths = new HubPaths(request.Root); var reportPath = StartupLifecycle.ReportPath(paths, request.Id);
        if (File.Exists(reportPath)) File.Delete(reportPath);
        started = StartApplication(request, request.Id, testMode);
        var deadline = DateTime.UtcNow.AddSeconds(45); var ready = false;
        while (DateTime.UtcNow < deadline && !started.HasExited)
        {
            if (File.Exists(reportPath))
            {
                var report = JsonSerializer.Deserialize<StartupReport>(await File.ReadAllTextAsync(reportPath), JsonDefaults.Options)!;
                if (report.Status == "failed") break;
                if (report.Status == "ready" && report.Version != request.Version) throw new InvalidOperationException("新版启动返回的版本号不一致。");
                if (report.Status == "ready" && report.ProcessId == started.Id && report.Version == request.Version && DiscoveryService.SamePath(report.Directory, request.TargetDirectory)) { ready = true; break; }
            }
            await Task.Delay(250);
        }
        if (!ready) throw new InvalidOperationException("新版未能完成启动。");
        await Task.Delay(2000); if (started.HasExited) throw new InvalidOperationException("新版启动后提前退出。");
        await UpdateTransaction.CompleteAsync(journal);
        if (testMode) { started.CloseMainWindow(); if (!started.WaitForExit(5000)) started.Kill(true); }
        return 0;
    }
}
catch (Exception error)
{
    if (started is not null && !started.HasExited)
    {
        started.CloseMainWindow(); if (!started.WaitForExit(5000)) { started.Kill(true); started.WaitForExit(); }
    }
    var detail = error.Message;
    try
    {
        if (journal is not null && journal.Status is "applying" or "awaiting-startup") { await UpdateTransaction.RollbackAsync(journal, error.GetType().Name); if (!testMode) StartApplication(journal.Request, null); detail += "\n已恢复旧版。"; }
        else if (journal is null && validated && request is not null && !testMode)
        {
            var file = Path.Combine(UpdateTransaction.WorkDirectory(request), "journal.json");
            var previous = File.Exists(file) ? JsonSerializer.Deserialize<UpdateJournal>(await File.ReadAllTextAsync(file), JsonDefaults.Options) : null;
            if (previous is null || previous.Status == "rolled-back") StartApplication(request, null);
        }
        if (request is not null) File.AppendAllText(Path.Combine(new HubPaths(request.Root).Logs, "update-errors.log"), DateTimeOffset.Now + " " + error + "\n");
    }
    catch (Exception recovery) { detail += "\n恢复尚未完成：" + recovery.Message + "\n请重新运行更新恢复入口。"; }
    if (!testMode) Native.MessageBox(IntPtr.Zero, detail, "DevHub · 更新未完成", 0x10); return 1;
}
finally { started?.Dispose(); }

static Process StartApplication(UpdateRequest request, string? startupId, bool testMode = false)
{
    var info = new ProcessStartInfo(Path.Combine(request.TargetDirectory, "DevHub.App.exe")) { UseShellExecute = false, WorkingDirectory = request.TargetDirectory };
    info.Environment["DEVHUB_ROOT"] = request.Root;
    if (testMode) { info.ArgumentList.Add("--update-smoke"); info.WindowStyle = ProcessWindowStyle.Hidden; }
    if (startupId is not null) { info.ArgumentList.Add("--startup-id"); info.ArgumentList.Add(startupId); }
    return Process.Start(info) ?? throw new InvalidOperationException("无法启动 DevHub。");
}
static class Native
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int MessageBox(IntPtr window, string text, string title, uint flags);
}
