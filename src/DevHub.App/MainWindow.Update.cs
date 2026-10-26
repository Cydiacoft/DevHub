using System.Diagnostics;
using System.Text.Json;
using DevHub.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DevHub.App;

public sealed partial class MainWindow
{
    private AvailableUpdate? availableUpdate;
    private string updateMessage = "尚未检查更新";
    private bool checkingUpdate;

    private void AddUpdateSettings()
    {
        var automatic = new ToggleSwitch { Header = "自动检查 DevHub 稳定版更新", IsOn = vm.Settings.CheckForUpdates, OnContent = "每天最多检查一次，更新前询问", OffContent = "仅手动检查" };
        automatic.Toggled += async (_, _) => await Guard(async () => { vm.Settings.CheckForUpdates = automatic.IsOn; await vm.SaveSettingsAsync(); });
        var body = Stack(Text("DevHub " + AppVersion.Current, 20, true), automatic, Muted(updateMessage),
            Button(checkingUpdate ? "正在检查…" : "检查更新", async () => await CheckUpdatesAsync(false)),
            ActionButton("打开 GitHub 发布页", () => ProcessRunner.OpenWebsite("https://github.com/" + AppUpdateService.Repository + "/releases")));
        if (availableUpdate is { } update)
        {
            body.Children.Add(Text("可用版本：" + update.Descriptor.Version, 16, true));
            body.Children.Add(Muted(update.Notes.Length > 4000 ? update.Notes[..4000] : update.Notes));
            body.Children.Add(Button("下载并重启更新", () => InstallUpdateAsync(update), true));
        }
        foreach (var journal in PendingUpdates())
            body.Children.Add(Button("恢复未完成的更新 · " + journal.Request.Version, () => RecoverUpdateAsync(journal)));
        PageContent.Children.Add(Card(body));
    }
    private async Task CheckForUpdatesOnStartupAsync()
    {
        if (PendingUpdates().Count > 0)
        {
            vm.StartupWarnings.Add("上次程序更新未完成。请在设置中选择“恢复未完成的更新”。");
            if (currentPage == "home") RenderPage();
            return;
        }
        if (!vm.Settings.CheckForUpdates || vm.Settings.LastUpdateCheck > DateTimeOffset.UtcNow.AddDays(-1)) return;
        await CheckUpdatesAsync(true);
    }
    private async Task CheckUpdatesAsync(bool background)
    {
        if (checkingUpdate) return; checkingUpdate = true;
        try
        {
            vm.Settings.LastUpdateCheck = DateTimeOffset.UtcNow; await vm.SaveSettingsAsync();
            using var service = new AppUpdateService(vm.Paths);
            availableUpdate = await service.CheckAsync(AppVersion.Current);
            updateMessage = availableUpdate is null ? "当前没有更高的公开稳定版。" : "发现新版本 " + availableUpdate.Descriptor.Version + "，可在设置中查看说明并更新。";
            if (background && availableUpdate is not null)
            {
                vm.StartupWarnings.Add(updateMessage); if (currentPage == "home") RenderPage();
            }
        }
        catch (Exception e)
        {
            updateMessage = "检查更新未完成：" + e.Message;
            if (!background) await MessageAsync("检查更新", updateMessage);
        }
        finally { checkingUpdate = false; if (currentPage == "settings") RenderPage(); }
    }
    private async Task InstallUpdateAsync(AvailableUpdate update)
    {
        if (vm.Busy) throw new InvalidOperationException("请先等待当前任务完成或取消任务，再更新程序。");
        if (PendingUpdates().Count > 0) throw new InvalidOperationException("请先恢复上次未完成的更新。");
        if (!await ConfirmAsync("更新 DevHub", $"当前：{AppVersion.Current}\n新版：{update.Descriptor.Version}\n\n将下载并校验更新包，保存旧版程序，再退出替换并重新打开。项目配置、SDK 和虚拟环境不会被替换。", "下载更新")) return;
        var prepared = await vm.RunAsync("下载 DevHub 更新", async (task, ct) =>
        {
            using var service = new AppUpdateService(vm.Paths); return await service.DownloadAsync(update, vm.DownloadProgress(task), ct);
        });
        if (vm.Busy) throw new InvalidOperationException("已有任务开始执行，已保留下载包，请任务完成后重试。");
        if (!await ConfirmAsync("更新包已准备", "现在将关闭 DevHub，替换程序后重新打开。", "重启更新")) return;
        var request = new UpdateRequest(vm.Paths.Root, AppContext.BaseDirectory, prepared.StageDirectory, update.Descriptor.Version, Environment.ProcessId, Guid.NewGuid().ToString("N"));
        var requestFile = await vm.RunAsync("准备程序更新", (_, ct) => UpdateTransaction.PrepareRunnerAsync(request, ct));
        StartUpdater(request, requestFile, "--apply"); Application.Current.Exit();
    }
    private List<UpdateJournal> PendingUpdates()
    {
        var lockPath = Path.Combine(vm.Paths.Data, "app-update.lock");
        try { if (File.Exists(lockPath)) { using var check = new FileStream(lockPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None); } }
        catch (IOException) { return []; } // An active updater is not an interrupted transaction.
        var root = Path.Combine(vm.Paths.Data, "app-updates"); var result = new List<UpdateJournal>();
        if (!Directory.Exists(root)) return result;
        foreach (var directory in Directory.EnumerateDirectories(root).Take(500))
        {
            try
            {
                var file = Path.Combine(directory, "journal.json"); if (!File.Exists(file)) continue;
                var journal = JsonSerializer.Deserialize<UpdateJournal>(File.ReadAllText(file), JsonDefaults.Options);
                if (journal is not null && journal.Status is "applying" or "awaiting-startup" && DiscoveryService.SamePath(journal.Request.TargetDirectory, AppContext.BaseDirectory) && DiscoveryService.SamePath(journal.Request.Root, vm.Paths.Root) && Path.GetFileName(directory) == journal.Request.Id) result.Add(journal);
            }
            catch (Exception e) when (e is IOException or JsonException or ArgumentException) { }
        }
        return result;
    }
    private async Task RecoverUpdateAsync(UpdateJournal journal)
    {
        if (vm.Busy) throw new InvalidOperationException("请先完成当前任务。");
        if (!await ConfirmAsync("恢复程序更新", "将退出 DevHub，并从上次更新事务备份恢复原程序。SDK 和配置保持独立。", "退出并恢复")) return;
        StartUpdater(journal.Request, Path.Combine(UpdateTransaction.WorkDirectory(journal.Request), "request.json"), "--recover"); Application.Current.Exit();
    }
    private static void StartUpdater(UpdateRequest request, string requestFile, string mode)
    {
        var exe = Path.Combine(UpdateTransaction.WorkDirectory(request), "runner", "DevHub.Updater.exe");
        var info = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden, WorkingDirectory = Path.GetDirectoryName(exe)! };
        info.ArgumentList.Add(mode); info.ArgumentList.Add(requestFile); info.Environment["DEVHUB_WAIT_PID"] = Environment.ProcessId.ToString();
        _ = Process.Start(info) ?? throw new InvalidOperationException("无法启动独立更新程序。");
    }
}
