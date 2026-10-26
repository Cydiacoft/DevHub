using System.Collections.ObjectModel;
using DevHub.Core;
using Microsoft.UI.Dispatching;

namespace DevHub.App.ViewModels;

public sealed class MainViewModel : ObservableObject, IDisposable
{
    public HubPaths Paths { get; } = HubPaths.Discover();
    public HubSettings Settings { get; private set; } = new();
    public List<ToolInstallation> Installations { get; private set; } = [];
    public List<ProjectProfile> Projects { get; private set; } = [];
    public List<CacheEntry>? CacheEntries { get; private set; }
    public DateTimeOffset? CacheScannedAt { get; private set; }
    public AgentSnapshot? Agents { get; private set; }
    public List<string> StartupWarnings { get; } = [];
    public ObservableCollection<HubTask> Tasks { get; } = [];
    public ProcessRunner Runner { get; }
    public DiscoveryService Discovery { get; }
    public DownloadService Downloads { get; }
    public ProjectService ProjectService { get; }
    public PackageService Packages { get; }
    public ManagementService Management { get; }
    public CacheService Cache { get; }
    public EnvironmentService EnvironmentService { get; }
    public JsonStore Store { get; }
    public List<ToolDefinition> Definitions => DiscoveryService.BuiltInCatalog().Tools.Concat(Settings.CustomTools).ToList();
    private readonly DispatcherQueue dispatcher;
    private readonly SemaphoreSlim operationLock = new(1);
    private HubTask? currentTask;
    public HubTask? CurrentTask { get => currentTask; private set => Set(ref currentTask, value); }
    public event Action? DataChanged;
    public event Action? StatusChanged;
    public bool Busy => CurrentTask?.Status == "进行中";

    public MainViewModel(DispatcherQueue dispatcher)
    {
        this.dispatcher = dispatcher;
        Runner = new(Paths); Discovery = new(Paths, Runner); Downloads = new(Paths); ProjectService = new(Paths, Runner);
        Packages = new(Paths, Runner); Management = new(Paths); Cache = new(Paths); Store = new(Paths);
        EnvironmentService = new(Paths, new WindowsEnvironmentStore());
    }
    public async Task InitializeAsync()
    {
        var marker = Path.Combine(Paths.Root, "DevHub.root");
        if (!File.Exists(marker)) await File.WriteAllTextAsync(marker, "DevHub portable data root\n");
        Settings = await Store.LoadRecoveringAsync("settings", () => new HubSettings(), StartupWarnings);
        Projects = await Store.LoadRecoveringAsync("projects", () => new List<ProjectProfile>(), StartupWarnings);
        Installations = await Store.LoadRecoveringAsync("inventory", () => new List<ToolInstallation>(), StartupWarnings);
        DataChanged?.Invoke();
    }
    public async Task ScanAsync()
        => await ScanCoreAsync(false);
    public async Task FullScanAsync()
        => await ScanCoreAsync(true);
    private async Task ScanCoreAsync(bool full)
    {
        var result = await RunAsync(full ? "完整扫描开发工具" : "快速扫描开发工具", (task, ct) => Discovery.ScanAsync(Settings, line => Output(task, line), ct, full));
        Installations = result;
        await Store.SaveAsync("inventory", Installations);
        DataChanged?.Invoke();
    }
    public async Task SaveSettingsAsync() => await Store.SaveAsync("settings", Settings);
    public async Task RefreshAgentsAsync()
    {
        Agents = await RunAsync("检测 Agent 与 MCP 配置", async (task, ct) =>
        {
            var roots = Settings.CacheProjectRoots.Concat(Projects.Select(p => p.Directory))
                .Append(Directory.GetParent(Paths.Root)?.FullName ?? Paths.Root);
            var discovered = CacheService.DiscoverProjects(roots, ct, line => Output(task, line));
            var directories = discovered.Concat(Projects.Select(p => p.Directory)).Append(Paths.Root);
            return await new AgentDiscoveryService().ScanAsync(Installations, directories, line => Output(task, line), ct);
        });
    }
    public async Task RefreshCachesAsync()
    {
        CacheEntries = await RunAsync("扫描电脑与项目缓存", (task, ct) => Cache.InspectComputerAsync(Settings, Projects, Installations, line => Output(task, line), ct));
        CacheScannedAt = DateTimeOffset.Now;
    }
    public async Task SaveProjectsAsync() { await Store.SaveAsync("projects", Projects); DataChanged?.Invoke(); }
    public async Task<T> RunAsync<T>(string title, Func<HubTask, CancellationToken, Task<T>> work)
    {
        if (!await operationLock.WaitAsync(0)) throw new InvalidOperationException("已有任务正在执行，请等待完成或取消当前任务。");
        var task = new HubTask { Title = title, Status = "进行中" }; Tasks.Insert(0, task); CurrentTask = task;
        task.PropertyChanged += (_, _) => StatusChanged?.Invoke();
        StatusChanged?.Invoke();
        try
        {
            var result = await Task.Run(() => work(task, task.Cancellation.Token));
            task.Status = "已完成"; task.Progress = 100;
            if (string.IsNullOrWhiteSpace(task.Detail)) task.Detail = "操作完成";
            return result;
        }
        catch (OperationCanceledException) { task.Status = "已取消"; task.Detail = "操作已取消"; throw; }
        catch (Exception e)
        {
            task.Status = "失败"; task.Detail = e.Message;
            await File.AppendAllTextAsync(Path.Combine(Paths.Logs, "tasks-errors.log"), $"{DateTimeOffset.Now:O} {title}\n{e}\n");
            throw;
        }
        finally
        {
            operationLock.Release();
            await Store.SaveAsync("tasks", Tasks.Take(100).Select(t => new { t.Id, t.Title, t.Started, t.Status, t.Detail, t.Progress }).ToArray());
            StatusChanged?.Invoke();
        }
    }
    public void Output(HubTask task, string text) => dispatcher.TryEnqueue(() => task.Detail = text.Length > 600 ? text[..600] : text);
    public IProgress<(double Progress, string Detail)> DownloadProgress(HubTask task) => new Progress<(double Progress, string Detail)>(p => dispatcher.TryEnqueue(() => { task.Progress = p.Progress; task.Detail = p.Detail; }));
    public async Task<string> ExecuteAsync(string title, CommandSpec command)
    {
        return await RunAsync(title, async (task, ct) =>
        {
            var result = await Runner.RunAsync(command, line => Output(task, line), ct); result.EnsureSuccess(); return result.Combined;
        });
    }
    public void Cancel() => CurrentTask?.Cancellation.Cancel();
    public void Dispose() { Cancel(); Downloads.Dispose(); }
}
