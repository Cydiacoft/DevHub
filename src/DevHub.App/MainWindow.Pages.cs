using System.Text.Json;
using DevHub.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DevHub.App;

public sealed partial class MainWindow
{
    private void RenderProjects()
    {
        Heading("项目环境", "给每个项目选择工具版本，在专用终端中使用。", Row(Button("发现项目", DiscoverProjectsAsync), Button("添加项目", AddProjectAsync, true)));
        if (vm.Projects.Count == 0)
        {
            PageContent.Children.Add(Card(Stack(Text("从你的第一个项目开始", 20, true), Muted("添加项目目录后，可绑定 Java、Python、Flutter 等版本，创建虚拟环境，并配置 IDEA。"), Button("选择项目目录", AddProjectAsync)))); return;
        }
        selectedProject = vm.Projects.FirstOrDefault(p => p.Id == selectedProject?.Id) ?? vm.Projects[0];
        var selector = new ComboBox { Header = "项目", ItemsSource = vm.Projects, SelectedItem = selectedProject, MinWidth = 540, MaxWidth = 900 };
        selector.SelectionChanged += (_, _) => { selectedProject = selector.SelectedItem as ProjectProfile; RenderPage(); };
        PageContent.Children.Add(selector);
        var project = selectedProject;
        PageContent.Children.Add(Card(Stack(Text(project.Name, 20, true), Muted(project.Directory),
            Row(ActionButton("打开项目目录", () => ProcessRunner.OpenDirectory(project.Directory)), ActionButton("打开项目终端", () => vm.ProjectService.OpenTerminal(project, vm.Installations, vm.EnvironmentService.FreshEnvironment()))))));
        var form = new StackPanel { Spacing = 14 };
        form.Children.Add(Text("工具版本绑定", 19, true));
        var selectors = new Dictionary<string, ComboBox>();
        foreach (var toolId in new[] { "java", "python", "flutter", "dart", "android", "node", "go", "rust", "dotnet", "maven", "gradle" })
        {
            var definition = vm.Definitions.First(d => d.Id == toolId);
            var list = vm.Installations.Where(i => i.ToolId == toolId && (toolId != "java" || i.IsJdk) && (toolId != "rust" || i.SelectionVersion is not null) && i.Version != "应用执行别名").ToList();
            var combo = new ComboBox { Header = definition.Name, HorizontalAlignment = HorizontalAlignment.Stretch, MaxWidth = 1000 };
            combo.Items.Add("使用默认环境（不绑定）"); foreach (var i in list) combo.Items.Add(i);
            combo.SelectedIndex = 0;
            if (project.Bindings.TryGetValue(toolId, out var selectedId))
            {
                var index = list.FindIndex(i => i.Id == selectedId);
                if (index >= 0) combo.SelectedIndex = index + 1;
                else form.Children.Add(new InfoBar { IsOpen = true, Severity = InfoBarSeverity.Warning, IsClosable = false, Title = definition.Name + " 已绑定的版本未找到", Message = "保存设置时可重新选择或解除此绑定。" });
            }
            selectors[toolId] = combo; form.Children.Add(combo);
        }
        var venv = new TextBox { Header = "Python 虚拟环境路径（可选）", Text = project.VirtualEnvironment ?? "", PlaceholderText = Path.Combine(project.Directory, ".venv") };
        var idea = new TextBox { Header = "IntelliJ IDEA 配置目录（可选，目录下包含 options）", Text = project.IdeaConfigDirectory ?? "", PlaceholderText = "%APPDATA%\\JetBrains\\IntelliJIdea…" };
        form.Children.Add(venv); form.Children.Add(idea);
        form.Children.Add(Row(Button("选择虚拟环境目录", async () => { var dir = await PickFolderAsync(); if (dir is not null) venv.Text = dir; }), Button("选择 IDEA 配置目录", async () => { var dir = await PickFolderAsync(); if (dir is not null) idea.Text = dir; })));
        form.Children.Add(Button("保存项目环境", async () =>
        {
            if (!string.IsNullOrWhiteSpace(venv.Text) && !File.Exists(Path.Combine(venv.Text.Trim(), "Scripts", "python.exe"))) throw new ArgumentException("虚拟环境目录中未找到 Scripts\\python.exe。");
            var bindings = selectors.Where(p => p.Value.SelectedItem is ToolInstallation).ToDictionary(p => p.Key, p => ((ToolInstallation)p.Value.SelectedItem).Id);
            if (selectors["dotnet"].SelectedItem is ToolInstallation sdk)
            {
                var target = Path.Combine(project.Directory, "global.json");
                var content = vm.ProjectService.VersionFileContent("dotnet", sdk);
                if (!File.Exists(target) || File.ReadAllText(target) != content)
                {
                    if (!await ConfirmAsync("为项目指定 .NET SDK", "为确保 .NET 使用所选版本，需要写入：\n" + target + "\n\n新内容：\n" + content + (File.Exists(target) ? "\n\n已有内容将备份后替换。" : ""), "写入并保存")) return;
                    await vm.ProjectService.WriteVersionFileAsync(project, "dotnet", sdk, true);
                }
            }
            project.Bindings = bindings; project.VirtualEnvironment = string.IsNullOrWhiteSpace(venv.Text) ? null : Path.GetFullPath(venv.Text.Trim());
            project.IdeaConfigDirectory = string.IsNullOrWhiteSpace(idea.Text) ? null : Path.GetFullPath(Environment.ExpandEnvironmentVariables(idea.Text.Trim()));
            await vm.SaveProjectsAsync(); await MessageAsync("项目环境已保存", "从此项目的“打开项目终端”进入，命令将使用保存的环境。");
        }, true));
        PageContent.Children.Add(Card(form));
        PageContent.Children.Add(Card(Stack(Text("项目操作", 19, true), Row(
            Button("创建 Python .venv", async () =>
            {
                if (!project.Bindings.TryGetValue("python", out var id)) throw new InvalidOperationException("请先选择 Python 并保存项目环境。");
                var python = vm.Installations.First(i => i.Id == id);
                if (!await ConfirmAsync("创建项目虚拟环境", "Python：" + python.ExecutablePath + "\n目标：" + Path.Combine(project.Directory, ".venv"), "创建")) return;
                await vm.RunAsync("创建 " + project.Name + " 虚拟环境", (task, ct) => vm.ProjectService.CreateVenvAsync(project, python, line => vm.Output(task, line), ct));
                await vm.SaveProjectsAsync();
            }),
            Button("配置 IntelliJ IDEA", async () =>
            {
                if (!project.Bindings.TryGetValue("java", out var id)) throw new InvalidOperationException("请先绑定 JDK 并保存 IDEA 配置目录。");
                var java = vm.Installations.First(i => i.Id == id);
                if (!await ConfirmAsync("配置 IDEA 项目 JDK", "项目：" + project.Directory + "\nIDEA 配置：" + project.IdeaConfigDirectory + "\nJDK：" + java.HomePath + "\n\n将备份并更新 SDK 注册、项目 JDK 和已有 Gradle JVM 配置。已有对应配置将替换，请先关闭 IDEA。", "应用配置")) return;
                var result = await vm.RunAsync("配置 IDEA", (_, _) => Task.FromResult(vm.ProjectService.ConfigureIdea(project, java, true)));
                await MessageAsync("IDEA 配置已更新", result);
            }),
            Button("写入版本声明", () => WriteProjectVersionAsync(project))
        ), Row(Button("查看项目版本声明", () => MessageAsync("项目声明与 Wrapper", string.Join("\n\n────────\n\n", vm.ProjectService.InspectDeclarations(project)) is { Length: > 0 } content ? content : "未发现常见版本声明。")),
            Button("移除项目记录…", async () =>
            {
                if (!await ConfirmAsync("移除项目记录", "将移除此项目在 DevHub 中的绑定记录。\n项目目录：" + project.Directory, "移除记录")) return;
                vm.Projects.Remove(project); selectedProject = null; await vm.SaveProjectsAsync();
            })) )));
    }
    private async Task AddProjectAsync()
    {
        var directory = await PickFolderAsync(); if (directory is null) return;
        directory = Path.GetFullPath(directory);
        if (vm.Projects.Any(p => DiscoveryService.SamePath(p.Directory, directory))) throw new InvalidOperationException("该目录已经添加。");
        var project = new ProjectProfile { Name = Path.GetFileName(directory.TrimEnd('\\')), Directory = directory };
        if (File.Exists(Path.Combine(directory, ".venv", "Scripts", "python.exe"))) project.VirtualEnvironment = Path.Combine(directory, ".venv");
        vm.Projects.Add(project); selectedProject = project; await vm.SaveProjectsAsync();
    }
    private async Task DiscoverProjectsAsync()
    {
        var roots = vm.Cache.DefaultProjectRoots().Concat(vm.Settings.CacheProjectRoots).Concat(vm.Projects.Select(p => p.Directory)).ToList();
        var discovered = await vm.RunAsync("发现项目", (task, ct) => Task.FromResult(CacheService.DiscoverProjects(roots, ct, line => vm.Output(task, line))));
        var available = discovered.Where(d => !vm.Projects.Any(p => DiscoveryService.SamePath(p.Directory, d))).ToList();
        if (available.Count == 0) { await MessageAsync("项目发现", "没有发现尚未添加的项目。可在设置中增加项目发现范围。"); return; }
        var list = new ListView { ItemsSource = available, SelectionMode = ListViewSelectionMode.Multiple, Height = 430, MinWidth = 440, MaxWidth = 650 };
        var selectAll = new CheckBox { Content = "选择全部" }; selectAll.Checked += (_, _) => list.SelectAll(); selectAll.Unchecked += (_, _) => list.SelectedItems.Clear();
        var dialog = new ContentDialog { Title = $"发现 {available.Count} 个项目", Content = Stack(Muted("选择要添加环境配置的项目，项目文件不会因此改变。"), selectAll, list), PrimaryButtonText = "添加所选项目", CloseButtonText = "取消" };
        if (await ShowAsync(dialog) != ContentDialogResult.Primary) return;
        foreach (var directory in list.SelectedItems.Cast<string>())
            vm.Projects.Add(new() { Name = Path.GetFileName(directory), Directory = directory, VirtualEnvironment = File.Exists(Path.Combine(directory, ".venv", "Scripts", "python.exe")) ? Path.Combine(directory, ".venv") : null });
        await vm.SaveProjectsAsync();
    }
    private async Task WriteProjectVersionAsync(ProjectProfile project)
    {
        var choices = project.Bindings.Where(p => p.Key is "python" or "node" or "rust" or "flutter" or "dotnet").Select(p => vm.Installations.FirstOrDefault(i => i.Id == p.Value)).Where(i => i is not null).Cast<ToolInstallation>().ToList();
        if (choices.Count == 0) throw new InvalidOperationException("请先保存 Python、Node.js、Rust、Flutter 或 .NET 的项目绑定。");
        var choice = new ComboBox { Header = "写入哪个工具的版本声明", ItemsSource = choices, SelectedIndex = 0, MinWidth = 430 };
        var preview = Text(""); void Refresh() { var t = (ToolInstallation)choice.SelectedItem; preview.Text = ProjectService.VersionFileName(t.ToolId) + "\n\n" + vm.ProjectService.VersionFileContent(t.ToolId, t); }
        choice.SelectionChanged += (_, _) => Refresh(); Refresh();
        var dialog = new ContentDialog { Title = "写入项目版本声明", Content = Stack(choice, preview, Muted("已有文件将备份后替换。Flutter 声明用于 FVM；DevHub 项目终端也会按绑定设置 PATH。")), PrimaryButtonText = "备份并写入", CloseButtonText = "取消" };
        if (await ShowAsync(dialog) != ContentDialogResult.Primary) return;
        var selected = (ToolInstallation)choice.SelectedItem; await vm.ProjectService.WriteVersionFileAsync(project, selected.ToolId, selected, true);
        await MessageAsync("版本声明已写入", Path.Combine(project.Directory, ProjectService.VersionFileName(selected.ToolId)));
    }

    private void RenderDiagnostics()
    {
        Heading("环境诊断", "检查命令入口、环境变量、重复路径和项目绑定。", Button("重新扫描并检查", vm.ScanAsync, true));
        foreach (var issue in DiagnosticService.Analyze(vm.Installations, vm.Projects))
        {
            var severity = issue.Severity switch { "错误" => InfoBarSeverity.Error, "警告" => InfoBarSeverity.Warning, "正常" => InfoBarSeverity.Success, _ => InfoBarSeverity.Informational };
            var body = Stack(new InfoBar { Title = issue.Title, Message = issue.Detail, Severity = severity, IsOpen = true, IsClosable = false });
            if (issue.ToolId is not null && vm.Definitions.FirstOrDefault(d => d.Id == issue.ToolId) is { } tool) body.Children.Add(Button("查看 " + tool.Name, () => ShowToolAsync(tool)));
            PageContent.Children.Add(Card(body));
        }
        PageContent.Children.Add(Card(Stack(Text("专项检查", 18, true),
            Button("Flutter doctor", async () =>
            {
                var flutter = vm.Installations.FirstOrDefault(i => i.ToolId == "flutter" && i.IsActive) ?? vm.Installations.FirstOrDefault(i => i.ToolId == "flutter") ?? throw new InvalidOperationException("未发现 Flutter。");
                await MessageAsync("Flutter doctor", await vm.ExecuteAsync("Flutter 环境诊断", new(flutter.ExecutablePath, ["doctor", "-v"], vm.Paths.Root, vm.EnvironmentService.FreshEnvironment(), 240)));
            }),
            Button("查看当前环境入口", () => MessageAsync("环境入口", string.Join("\n\n", vm.Installations.Where(i => i.IsActive).Select(i => i.Name + "\n" + i.ExecutablePath + "\n" + i.Version)))))));
    }
    private void RenderCache()
    {
        Heading("缓存与空间", "查看临时文件、开发工具缓存、项目构建输出和需保留的软件维护数据。", Button("扫描缓存占用", RefreshCachePageAsync, true));
        var diskSummary = new StackPanel { Spacing = 12 };
        foreach (var drive in DriveInfo.GetDrives().Where(d => d.IsReady && d.DriveType == DriveType.Fixed))
        {
            var used = drive.TotalSize - drive.AvailableFreeSpace;
            diskSummary.Children.Add(Stack(Text($"{drive.Name}  可用 {drive.AvailableFreeSpace / 1073741824d:F1} GB / 共 {drive.TotalSize / 1073741824d:F1} GB", 15, true),
                new ProgressBar { Minimum = 0, Maximum = 100, Value = used * 100d / drive.TotalSize }));
        }
        PageContent.Children.Add(Card(diskSummary));
        var entries = vm.CacheEntries;
        if (entries is null)
        {
            PageContent.Children.Add(Card(Stack(Text(vm.Busy ? "等待当前任务完成后统计" : "正在准备缓存统计", 20, true), Muted("会检查常见工具缓存位置、项目目录和 DevHub 自身缓存。统计在后台运行，可以切换页面或取消。"))));
            if (!vm.Busy) DispatcherQueue.TryEnqueue(async () => { if (!vm.Busy && vm.CacheEntries is null) await Guard(RefreshCachePageAsync); });
            return;
        }
        var driveSelector = new ComboBox { Header = "磁盘", ItemsSource = new[] { "所有磁盘" }.Concat(DriveInfo.GetDrives().Where(d => d.IsReady && d.DriveType == DriveType.Fixed).Select(d => d.Name)).ToArray(), SelectedItem = cacheDrive, Width = 220 };
        PageContent.Children.Add(driveSelector);
        var driveEntries = entries.Where(e => cacheDrive == "所有磁盘" || e.Directory.StartsWith(cacheDrive, StringComparison.OrdinalIgnoreCase)).ToList();
        var summary = new Grid { ColumnSpacing = 14 };
        var totals = new[] { ("所选磁盘 · 已发现占用", $"{driveEntries.Sum(e => e.Bytes) / 1073741824d:F2} GB"), ("可清理的缓存与输出", $"{driveEntries.Sum(e => e.Reclaimable) / 1073741824d:F2} GB"), ("已发现项目", vm.Cache.LastDiscoveredProjects.Count.ToString()) };
        for (var i = 0; i < totals.Length; i++)
        {
            summary.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
            var card = Card(Stack(Muted(totals[i].Item1), Text(totals[i].Item2, 29, true))); Grid.SetColumn(card, i); summary.Children.Add(card);
        }
        PageContent.Children.Add(summary);
        PageContent.Children.Add(Muted($"占用估算于 {vm.CacheScannedAt:yyyy-MM-dd HH:mm} · 跳过不可访问目录和目录链接 · SDK / 环境 / 本地仓库单独标记"));
        var categories = new[] { "开发工具缓存", "项目缓存与依赖", "系统与应用占用", "DevHub 自身" };
        var tabs = new RadioButtons { MaxColumns = 4, ItemsSource = new[] { "开发工具", "各项目缓存与依赖", "系统与应用", "DevHub 自身" }, SelectedIndex = cacheSection };
        var search = new AutoSuggestBox { Text = cacheSearch, PlaceholderText = "搜索工具、项目名称或路径…", Width = 420 };
        PageContent.Children.Add(tabs); PageContent.Children.Add(search);
        var content = new StackPanel { Spacing = 14 }; PageContent.Children.Add(content);
        void Populate()
        {
            content.Children.Clear();
            var filtered = driveEntries.Where(e => e.Category == categories[cacheSection] && (e.Name + " " + e.Owner + " " + e.Directory).Contains(search.Text ?? "", StringComparison.OrdinalIgnoreCase)).ToList();
            if (filtered.Count == 0) content.Children.Add(Card(Stack(Text("此分类暂未发现匹配项", 17, true), Muted("可在设置中添加其他项目根目录，再重新扫描。"))));
            foreach (var group in filtered.GroupBy(e => e.Owner).OrderByDescending(g => g.Sum(e => e.Bytes)))
            {
                var groupContent = new StackPanel { Spacing = 14 };
                foreach (var entry in group.OrderByDescending(e => e.Bytes))
                {
                    var actions = Row(ActionButton("打开目录", () => ProcessRunner.OpenDirectory(entry.Directory)));
                    if (entry.CanClean) actions.Children.Add(Button("清理此项…", async () =>
                    {
                        var estimate = entry.CleanupTool is null ? $"可清理估算：{entry.Reclaimable / 1048576d:F1} MB" : "可回收量由所属工具清理后确定。";
                        if (!await ConfirmAsync("清理 " + entry.Name, "所属：" + entry.Owner + "\n目录：" + entry.Directory + "\n总占用估算：" + entry.Size + "\n" + estimate + "\n\n" + entry.Description, "清理")) return;
                        var result = await vm.RunAsync("清理 " + entry.Name, (_, ct) => vm.Cache.ClearAsync(entry, vm.Runner, ct));
                        var counts = result.DeletedFiles >= 0 ? $"删除文件：{result.DeletedFiles}\n保留文件 / 受限位置：{result.RetainedFiles}\n" : "已通过所属工具清理未使用项。\n";
                        await MessageAsync("缓存清理结果", counts + $"释放空间约：{result.ReleasedBytes / 1073741824d:F2} GB\n\n" + (result.Note ?? "正在使用、近期需保留或无法访问的文件会跳过。占用会在重新扫描后更新。"));
                        await RefreshCachePageAsync();
                    }));
                    groupContent.Children.Add(Card(Stack(Row(Text(entry.Name, 16, true), Text(entry.Size, 15)), Muted(entry.Directory),
                        Muted(entry.CanClean ? entry.Description : "通过所属工具管理 · " + entry.Description), actions)));
                }
                content.Children.Add(new Expander
                {
                    Header = group.Key + $" · {group.Sum(e => e.Bytes) / 1073741824d:F2} GB · {group.Count()} 项", Content = groupContent,
                    IsExpanded = filtered.GroupBy(e => e.Owner).Count() <= 4 || search.Text.Length > 0,
                    HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch
                });
            }
        }
        tabs.SelectionChanged += (_, _) => { cacheSection = Math.Max(0, tabs.SelectedIndex); Populate(); };
        driveSelector.SelectionChanged += (_, _) => { cacheDrive = driveSelector.SelectedItem?.ToString() ?? "所有磁盘"; RenderPage(); };
        search.TextChanged += (_, _) => { cacheSearch = search.Text; Populate(); }; Populate();
        PageContent.Children.Add(ActionButton("打开 Windows 存储设置", () => System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("ms-settings:storagesense") { UseShellExecute = true })));
    }
    private async Task RefreshCachePageAsync()
    {
        await vm.RefreshCachesAsync(); if (currentPage == "cache") RenderPage();
    }
    private void RenderTasks()
    {
        Heading("任务与日志", "查看本次运行的任务状态，完整命令输出保存在项目日志目录。", ActionButton("打开日志目录", () => ProcessRunner.OpenDirectory(vm.Paths.Logs)));
        if (vm.Tasks.Count == 0) PageContent.Children.Add(Card(Text("当前还没有任务。")));
        foreach (var task in vm.Tasks)
        {
            var progress = new ProgressBar { Minimum = 0, Maximum = 100, Value = task.Progress, IsIndeterminate = task.Status == "进行中" && task.Progress == 0 };
            var title = Text(task.Title + " · " + task.Status, 17, true); var detail = Muted(task.Detail);
            task.PropertyChanged += (_, _) => { title.Text = task.Title + " · " + task.Status; detail.Text = task.Detail; progress.Value = task.Progress; progress.IsIndeterminate = task.Status == "进行中" && task.Progress == 0; };
            PageContent.Children.Add(Card(Stack(title, Muted(task.Started.ToString("yyyy-MM-dd HH:mm:ss")), progress, detail)));
        }
    }
    private void RenderSettings()
    {
        Heading("设置", "外观、扫描范围、自定义工具和配置恢复。");
        var theme = new ComboBox { Header = "外观", ItemsSource = new[] { "跟随系统", "浅色", "深色" }, SelectedIndex = vm.Settings.Theme switch { "Light" => 1, "Dark" => 2, _ => 0 }, Width = 240 };
        theme.SelectionChanged += async (_, _) => await Guard(async () => { vm.Settings.Theme = new[] { "Default", "Light", "Dark" }[theme.SelectedIndex]; ApplyTheme(); await vm.SaveSettingsAsync(); });
        var manager = new ComboBox { Header = "首选包管理器", ItemsSource = new[] { "winget", "scoop", "choco" }, SelectedItem = vm.Settings.PreferredPackageManager, Width = 240 };
        manager.SelectionChanged += async (_, _) => await Guard(async () => { vm.Settings.PreferredPackageManager = manager.SelectedItem!.ToString()!; await vm.SaveSettingsAsync(); });
        PageContent.Children.Add(Card(Row(theme, manager)));
        var concurrency = new NumberBox { Header = "版本查询并发数", Minimum = 1, Maximum = 12, Value = vm.Settings.ProbeConcurrency, Width = 190, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact };
        var timeout = new NumberBox { Header = "单个查询超时（秒）", Minimum = 2, Maximum = 60, Value = vm.Settings.ProbeTimeoutSeconds, Width = 190, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact };
        var cacheHours = new NumberBox { Header = "版本缓存有效期（小时）", Minimum = 1, Maximum = 168, Value = vm.Settings.VersionCacheHours, Width = 190, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact };
        var depth = new NumberBox { Header = "额外目录扫描深度", Minimum = 1, Maximum = 8, Value = vm.Settings.AdditionalScanDepth, Width = 190, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact };
        var everything = new ToggleSwitch { Header = "Everything 索引加速", IsOn = vm.Settings.UseEverything, OnContent = "可用时查询现成索引", OffContent = "仅使用目录和安装记录" };
        var everythingPath = new TextBox { Header = "Everything ES 路径（留空自动查找）", Text = vm.Settings.EverythingCliPath ?? "", PlaceholderText = "es.exe 的完整路径" };
        var detectedCli = new EverythingService(vm.Paths, vm.Runner).FindCli(vm.Settings);
        PageContent.Children.Add(Card(Stack(Text("扫描性能与索引", 18, true),
            Muted("快速扫描复用文件指纹未变化的版本记录；完整扫描强制重新核验。Everything 只提供候选路径，工具归属仍由 DevHub 检查。"),
            Row(concurrency, timeout), Row(cacheHours, depth), everything, everythingPath,
            Muted(detectedCli is null ? "Everything ES：未找到；扫描会自动使用目录与安装记录。" : "Everything ES：" + detectedCli),
            Button("保存扫描设置", async () =>
            {
                if (new[] { concurrency.Value, timeout.Value, cacheHours.Value, depth.Value }.Any(double.IsNaN)) throw new ArgumentException("请填写有效的扫描参数。");
                var cli = everythingPath.Text.Trim(); if (cli.Length > 0 && !File.Exists(cli)) throw new FileNotFoundException("ES 路径不存在。");
                vm.Settings.ProbeConcurrency = (int)Math.Clamp(concurrency.Value, 1, 12); vm.Settings.ProbeTimeoutSeconds = (int)Math.Clamp(timeout.Value, 2, 60);
                vm.Settings.VersionCacheHours = (int)Math.Clamp(cacheHours.Value, 1, 168); vm.Settings.AdditionalScanDepth = (int)Math.Clamp(depth.Value, 1, 8);
                vm.Settings.UseEverything = everything.IsOn; vm.Settings.EverythingCliPath = cli.Length == 0 ? null : Path.GetFullPath(cli);
                await vm.SaveSettingsAsync(); await MessageAsync("扫描设置已保存", "将在下一次扫描时使用。");
            }))));
        var directories = PageList(); directories.ItemsSource = vm.Settings.ScanDirectories.ToList();
        PageContent.Children.Add(Card(Stack(Text("额外扫描目录", 18, true), Muted("默认扫描 PATH、安装记录及常见 SDK 目录。扫描不会执行未知 CLI。"), directories, Row(
            Button("添加目录", async () => { var folder = await PickFolderAsync(); if (folder is null) return; if (!vm.Settings.ScanDirectories.Contains(folder, StringComparer.OrdinalIgnoreCase)) vm.Settings.ScanDirectories.Add(folder); await vm.SaveSettingsAsync(); RenderPage(); }),
            Button("移除所选目录", async () => { if (directories.SelectedItem is not string directory) return; vm.Settings.ScanDirectories.Remove(directory); await vm.SaveSettingsAsync(); RenderPage(); }), Button("重新扫描", vm.ScanAsync)))));
        var cacheRoots = PageList(); cacheRoots.ItemsSource = vm.Settings.CacheProjectRoots.ToList();
        PageContent.Children.Add(Card(Stack(Text("项目缓存发现范围", 18, true),
            Muted("默认检查：" + string.Join("；", vm.Cache.DefaultProjectRoots()) + "。向下查找 4 层项目目录，跳过依赖、SDK 和构建目录。"),
            cacheRoots, Row(Button("添加项目根目录", async () =>
            {
                var dir = await PickFolderAsync(); if (dir is null) return;
                if (!vm.Settings.CacheProjectRoots.Contains(dir, StringComparer.OrdinalIgnoreCase)) vm.Settings.CacheProjectRoots.Add(dir);
                await vm.SaveSettingsAsync(); RenderPage();
            }), Button("移除所选范围", async () =>
            {
                if (cacheRoots.SelectedItem is not string dir) return; vm.Settings.CacheProjectRoots.Remove(dir); await vm.SaveSettingsAsync(); RenderPage();
            })))));
        var android = new TextBox { Header = "Android SDK 根目录（可选）", Text = vm.Settings.AndroidSdkRoot ?? "", PlaceholderText = Environment.GetEnvironmentVariable("ANDROID_HOME") ?? "包含 cmdline-tools 或 platform-tools 的目录" };
        PageContent.Children.Add(Card(Stack(android, Button("保存 Android 目录", async () => { var value = android.Text.Trim(); if (value.Length > 0 && !Directory.Exists(value)) throw new DirectoryNotFoundException("目录不存在。"); vm.Settings.AndroidSdkRoot = value.Length == 0 ? null : Path.GetFullPath(value); await vm.SaveSettingsAsync(); }))));
        var custom = PageList(); custom.ItemsSource = vm.Settings.CustomTools.Select(t => t.Name + " · " + t.Id).ToList();
        PageContent.Children.Add(Card(Stack(Text("自定义 CLI", 18, true), custom, Row(Button("添加工具规则", () => AddCustomToolAsync()), Button("移除所选规则", async () => { if (custom.SelectedIndex < 0) return; vm.Settings.CustomTools.RemoveAt(custom.SelectedIndex); await vm.SaveSettingsAsync(); RenderPage(); })))));
        PageContent.Children.Add(Card(Stack(Text("配置备份与恢复", 18, true), Muted("可恢复全局环境切换或项目配置；恢复前同样生成备份。"), Row(Button("选择备份并恢复", RestoreBackupAsync), ActionButton("打开备份目录", () => ProcessRunner.OpenDirectory(vm.Paths.Backups))))));
        PageContent.Children.Add(Card(Stack(Text("数据位置", 18, true), Muted("项目根目录：" + vm.Paths.Root), Muted("缓存：" + vm.Paths.Cache), Muted("SDK：" + vm.Paths.Sdks), Muted("设置与工具工作目录：" + vm.Paths.Data), ActionButton("打开数据目录", () => ProcessRunner.OpenDirectory(vm.Paths.State)))));
    }
    private async Task AddCustomToolAsync(ToolDefinition? initial = null)
    {
        var name = new TextBox { Header = "工具名称", Text = initial?.Name ?? "" };
        var executable = new TextBox { Header = "命令文件名（如 mytool.exe）", Text = initial?.Executables.FirstOrDefault() ?? "" };
        var args = new TextBox { Header = "版本参数（每行一个参数）", Text = "--version", AcceptsReturn = true, Height = 80 };
        var website = new TextBox { Header = "官网 HTTPS 地址（可选）" };
        var dialog = new ContentDialog { Title = "添加自定义工具", Content = Stack(name, executable, args, website, Muted("保存规则后，扫描会运行该命令的版本参数。请只添加你认识的工具。")), PrimaryButtonText = "保存规则", CloseButtonText = "取消" };
        if (await ShowAsync(dialog) != ContentDialogResult.Primary) return;
        var file = executable.Text.Trim();
        if (string.IsNullOrWhiteSpace(name.Text) || Path.GetFileName(file) != file || !new[] { ".exe", ".cmd", ".bat", ".ps1" }.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase)) throw new ArgumentException("请填写名称和有效的命令文件名。");
        if (vm.Definitions.Any(d => d.Executables.Contains(file, StringComparer.OrdinalIgnoreCase))) throw new InvalidOperationException("已有此命令的识别规则。");
        if (!string.IsNullOrWhiteSpace(website.Text) && (!Uri.TryCreate(website.Text.Trim(), UriKind.Absolute, out var uri) || uri.Scheme != "https")) throw new ArgumentException("官网地址必须为 HTTPS。");
        vm.Settings.CustomTools.Add(new() { Id = "custom:" + DiscoveryService.StableId(file), Name = name.Text.Trim(), Category = "自定义工具", Description = "用户添加的 CLI", Executables = [file], VersionArgs = args.Text.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries), Website = string.IsNullOrWhiteSpace(website.Text) ? null : website.Text.Trim(), Custom = true });
        await vm.SaveSettingsAsync(); await vm.ScanAsync();
    }
    private async Task RestoreBackupAsync()
    {
        var options = Directory.GetFiles(vm.Paths.Backups, "environment-*.json").Concat(Directory.GetDirectories(vm.Paths.Backups).Where(d => File.Exists(Path.Combine(d, "manifest.json")))).OrderByDescending(p => p).ToList();
        if (options.Count == 0) { await MessageAsync("配置恢复", "还没有配置备份。修改环境或项目配置时会自动创建。"); return; }
        var select = new ComboBox { Header = "备份", ItemsSource = options, SelectedIndex = 0, MaxWidth = 650, MinWidth = 440 };
        var dialog = new ContentDialog { Title = "恢复配置备份", Content = Stack(select, Muted("选择后可查看恢复内容。恢复会覆盖对应的当前配置。")), PrimaryButtonText = "查看恢复预览", CloseButtonText = "取消" };
        if (await ShowAsync(dialog) != ContentDialogResult.Primary) return;
        var path = select.SelectedItem!.ToString()!;
        if (File.Exists(path))
        {
            var backup = JsonSerializer.Deserialize<EnvironmentBackup>(await File.ReadAllTextAsync(path), JsonDefaults.Options) ?? throw new InvalidDataException("备份损坏。");
            var request = vm.EnvironmentService.PlanRestore(backup);
            var detail = string.Join("\n\n", request.Changes.Select(c => $"[{c.Before.Scope}] {c.Before.Name}\n当前：{c.Before.Value}\n恢复为：{c.After.Value}"));
            if (!await ConfirmAsync("恢复环境配置", detail, "恢复")) return;
            await vm.RunAsync("恢复环境配置", async (_, _) => { await vm.EnvironmentService.ApplyElevatedAsync(request); return true; });
            foreach (var pair in vm.EnvironmentService.FreshEnvironment()) Environment.SetEnvironmentVariable(pair.Key, pair.Value);
            await vm.ScanAsync();
        }
        else
        {
            var manifest = await File.ReadAllTextAsync(Path.Combine(path, "manifest.json"));
            if (!await ConfirmAsync("恢复项目配置", manifest, "恢复")) return;
            await vm.RunAsync("恢复项目配置", (_, _) => { vm.ProjectService.RestoreFiles(path); return Task.FromResult(true); });
        }
        await MessageAsync("配置已恢复", "请重新打开相关终端或开发工具。");
    }
}
