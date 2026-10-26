using DevHub.App.ViewModels;
using DevHub.Core;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using Windows.Storage.Pickers;

namespace DevHub.App;

public sealed partial class MainWindow : Window
{
    private readonly MainViewModel vm;
    private string currentPage = "home";
    private bool initialized;
    private string? requestedVersionTool;
    private ProjectProfile? selectedProject;
    private int toolsSection;
    private string toolsSearch = "";
    private bool toolsInstalledOnly = true;
    private int cacheSection;
    private string cacheSearch = "";
    private string cacheDrive = "C:\\";
    private int agentSection;
    private string agentSearch = "";
    private readonly SemaphoreSlim dialogLock = new(1);

    public MainWindow(string initialPage = "home")
    {
        InitializeComponent();
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "DevHub.ico"));
        vm = new(DispatcherQueue);
        ExtendsContentIntoTitleBar = true; SetTitleBar(AppTitleBar);
        Root.ActualThemeChanged += (_, _) => UpdateTitleTheme();
        AppWindow.Resize(new SizeInt32(1380, 940));
        vm.DataChanged += () => { ApplyTheme(); RenderPage(); };
        vm.StatusChanged += RefreshStatus;
        Root.Loaded += async (_, _) =>
        {
            if (initialized) return; initialized = true;
            Navigation.SelectedItem = Navigation.MenuItems[0];
            RenderPage();
            try { await vm.InitializeAsync(); }
            catch (Exception error) { Program.ShowFailure(error); Application.Current.Exit(); return; }
            if (initialPage == "cache") cacheSection = 2;
            if (initialPage != "home") Navigate(initialPage);
            if (App.AutoExitSmoke)
            {
                try { await ValidateSmokeLayoutAsync(); }
                catch (Exception error) { Program.ShowFailure(error); Environment.ExitCode = 1; Application.Current.Exit(); return; }
            }
            App.WriteStartupReport("ready");
            if (App.AutoExitSmoke)
            {
                var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
                timer.Tick += (_, _) => { timer.Stop(); Application.Current.Exit(); }; timer.Start();
            }
            else if (!App.SmokeTest)
            {
                _ = Guard(vm.ScanAsync);
                _ = CheckForUpdatesOnStartupAsync();
            }
        };
        Closed += (_, _) => vm.Dispose();
    }
    private void Navigation_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is NavigationViewItem item) { currentPage = item.Tag?.ToString() ?? "home"; if (initialized) { RenderPage(); PageScroll.ChangeView(null, 0, null, true); } }
    }
    private void Navigate(string page)
    {
        currentPage = page;
        var item = Navigation.MenuItems.Concat(Navigation.FooterMenuItems).OfType<NavigationViewItem>().FirstOrDefault(i => i.Tag?.ToString() == page);
        if (item is not null) Navigation.SelectedItem = item; else RenderPage();
    }
    private void RenderPage()
    {
        PageContent.Children.Clear();
        switch (currentPage)
        {
            case "tools": RenderTools(); break; case "versions": RenderVersions(); break;
            case "agents": RenderAgents(); break;
            case "projects": RenderProjects(); break; case "diagnostics": RenderDiagnostics(); break;
            case "cache": RenderCache(); break; case "settings": RenderSettings(); break;
            case "tasks": RenderTasks(); break; default: RenderHome(); break;
        }
    }
    private void RefreshStatus()
    {
        if (!DispatcherQueue.HasThreadAccess) { DispatcherQueue.TryEnqueue(RefreshStatus); return; }
        var task = vm.CurrentTask;
        StatusTitle.Text = task is null ? "准备就绪" : task.Title + " · " + task.Status;
        StatusDetail.Text = task?.Detail ?? "所有运行数据保存在项目目录";
        TaskProgress.Visibility = vm.Busy ? Visibility.Visible : Visibility.Collapsed;
        TaskProgress.IsIndeterminate = vm.Busy && task?.Progress == 0;
        TaskProgress.Value = task?.Progress ?? 0;
        CancelTaskButton.Visibility = vm.Busy ? Visibility.Visible : Visibility.Collapsed;
    }
    private void CancelTask_Click(object sender, RoutedEventArgs e) => vm.Cancel();
    private void ApplyTheme()
    {
        Root.RequestedTheme = vm.Settings.Theme switch { "Light" => ElementTheme.Light, "Dark" => ElementTheme.Dark, _ => ElementTheme.Default };
        UpdateTitleTheme();
    }
    private void UpdateTitleTheme()
    {
        AppWindow.TitleBar.ButtonBackgroundColor = Colors.Transparent;
        AppWindow.TitleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
        AppWindow.TitleBar.ButtonForegroundColor = Root.ActualTheme == ElementTheme.Dark ? Colors.White : Colors.Black;
    }

    private static TextBlock Text(string value, double size = 14, bool strong = false) => new()
    {
        Text = value, FontSize = size, FontWeight = strong ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal,
        TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true
    };
    private static TextBlock Muted(string value, double size = 12) { var t = Text(value, size); t.Opacity = .65; return t; }
    private static StackPanel Stack(params UIElement[] children)
    {
        var panel = new StackPanel { Spacing = 12 }; foreach (var child in children) panel.Children.Add(child); return panel;
    }
    private static StackPanel Row(params UIElement[] children)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 }; foreach (var child in children) panel.Children.Add(child); return panel;
    }
    private static ListView PageList(bool clickable = false)
    {
        var list = new ListView { SelectionMode = clickable ? ListViewSelectionMode.None : ListViewSelectionMode.Single, IsItemClickEnabled = clickable };
        ScrollViewer.SetVerticalScrollBarVisibility(list, ScrollBarVisibility.Disabled);
        ScrollViewer.SetVerticalScrollMode(list, ScrollMode.Disabled);
        ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Disabled);
        return list;
    }
    private static Border Card(UIElement content) => new()
    {
        Child = content, Style = (Style)Application.Current.Resources["DevHubCardStyle"]
    };
    private Button Button(string label, Func<Task> action, bool accent = false)
    {
        var button = new Button { Content = label, HorizontalAlignment = HorizontalAlignment.Left };
        if (accent) button.Style = (Style)Application.Current.Resources["AccentButtonStyle"];
        button.Click += async (_, _) => await Guard(action); return button;
    }
    private Button ActionButton(string label, Action action, bool accent = false) => Button(label, () => { action(); return Task.CompletedTask; }, accent);
    private void Heading(string title, string subtitle, FrameworkElement? action = null)
    {
        var grid = new Grid { ColumnSpacing = 18 };
        grid.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) }); grid.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        grid.Children.Add(Stack(Text(title, 28, true), Muted(subtitle, 14)));
        if (action is not null) { Grid.SetColumn(action, 1); grid.Children.Add(action); }
        PageContent.Children.Add(grid);
    }
    private async Task Guard(Func<Task> action)
    {
        try { await action(); }
        catch (OperationCanceledException) { }
        catch (Exception e) { await MessageAsync("操作未完成", e.Message); }
    }
    private async Task<ContentDialogResult> ShowAsync(ContentDialog dialog)
    {
        await dialogLock.WaitAsync();
        try { dialog.XamlRoot = Root.XamlRoot; dialog.RequestedTheme = Root.RequestedTheme; return await dialog.ShowAsync(); }
        finally { dialogLock.Release(); }
    }
    private async Task MessageAsync(string title, string message)
    {
        await ShowAsync(new ContentDialog { Title = title, Content = new ScrollViewer { Content = Text(message), MaxHeight = 500 }, CloseButtonText = "关闭" });
    }
    private async Task<bool> ConfirmAsync(string title, string detail, string primary = "确认执行", bool acknowledgement = false)
    {
        var check = new CheckBox { Content = "我已阅读操作内容，并同意所选来源适用的许可与安装条款。", IsChecked = false };
        var body = Stack(Text(detail)); if (acknowledgement) body.Children.Add(check);
        var dialog = new ContentDialog { Title = title, Content = new ScrollViewer { Content = body, MaxHeight = 480 }, PrimaryButtonText = primary, CloseButtonText = "取消", DefaultButton = ContentDialogButton.Close };
        if (acknowledgement)
        {
            dialog.IsPrimaryButtonEnabled = false;
            check.Checked += (_, _) => dialog.IsPrimaryButtonEnabled = true; check.Unchecked += (_, _) => dialog.IsPrimaryButtonEnabled = false;
        }
        return await ShowAsync(dialog) == ContentDialogResult.Primary;
    }
    private async Task<string?> PickFolderAsync()
    {
        var picker = new FolderPicker(); picker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        return (await picker.PickSingleFolderAsync())?.Path;
    }
    private async Task<string?> PickZipAsync()
    {
        var picker = new FileOpenPicker(); picker.FileTypeFilter.Add(".zip");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        return (await picker.PickSingleFileAsync())?.Path;
    }

    private void RenderHome()
    {
        Heading("开发环境，一处掌握", "找到工具的位置，理清版本与项目之间的关系。", Button("重新扫描", vm.ScanAsync, true));
        foreach (var warning in vm.StartupWarnings) PageContent.Children.Add(new InfoBar { Title = "已使用恢复配置", Message = warning, Severity = InfoBarSeverity.Warning, IsOpen = true, IsClosable = false });
        var issues = DiagnosticService.Analyze(vm.Installations, vm.Projects).Where(d => d.Severity is "错误" or "警告").ToList();
        var stats = new Grid { ColumnSpacing = 14 };
        var values = new[] { ("已识别工具", vm.Installations.Where(i => !i.ToolId.StartsWith("unknown:")).Select(i => i.ToolId).Distinct().Count().ToString()), ("安装与版本", vm.Installations.Count(i => !i.ToolId.StartsWith("unknown:")).ToString()), ("项目环境", vm.Projects.Count.ToString()), ("待检查问题", issues.Count.ToString()) };
        for (var i = 0; i < values.Length; i++)
        {
            stats.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
            var index = i;
            var label = Muted(values[i].Item1, 13); label.IsTextSelectionEnabled = false;
            var value = Text(values[i].Item2, 34, true); value.IsTextSelectionEnabled = false;
            var content = Stack(label, value);
            var card = new Button
            {
                Content = content, Style = (Style)Application.Current.Resources["DevHubStatStyle"]
            };
            var hint = new[] { "查看已识别的工具清单", "查看全部安装与版本记录", "查看项目环境", "查看需要检查的问题" }[i];
            ToolTipService.SetToolTip(card, hint);
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(card, values[i].Item1 + " " + values[i].Item2 + "，" + hint);
            card.Click += async (_, _) => await Guard(async () =>
            {
                if (index is 0 or 1) await ShowInventoryDetailsAsync(index == 1);
                else Navigate(index == 2 ? "projects" : "diagnostics");
            });
            Grid.SetColumn(card, i); stats.Children.Add(card);
        }
        PageContent.Children.Add(stats);
        PageContent.Children.Add(Text("常用开发环境", 19, true));
        var grid = new Grid { ColumnSpacing = 14, RowSpacing = 14 };
        grid.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) }); grid.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        var ids = new[] { "java", "python", "flutter", "node", "go", "rust", "dotnet", "android" };
        for (var i = 0; i < ids.Length; i++)
        {
            if (i % 2 == 0) grid.RowDefinitions.Add(new() { Height = GridLength.Auto });
            var definition = vm.Definitions.First(d => d.Id == ids[i]);
            var installs = vm.Installations.Where(t => t.ToolId == definition.Id).ToList();
            var active = installs.FirstOrDefault(t => t.IsActive) ?? installs.FirstOrDefault();
            var card = Card(Stack(Row(Text(definition.Name, 18, true), Muted($"{installs.Count} 个版本 / 入口")),
                Text(active?.Version ?? "尚未发现", 16), Muted(active?.ExecutablePath ?? definition.Description),
                Button("管理 " + definition.Name, () => ShowToolAsync(definition))));
            Grid.SetColumn(card, i % 2); Grid.SetRow(card, i / 2); grid.Children.Add(card);
        }
        PageContent.Children.Add(grid);
        if (issues.Count > 0)
        {
            var item = issues[0];
            PageContent.Children.Add(Card(Stack(Text(item.Title, 16, true), Muted(item.Detail), ActionButton("查看环境诊断", () => Navigate("diagnostics")))));
        }
        PageContent.Children.Add(Muted("运行数据位置：" + vm.Paths.State));
    }

    private void RenderTools()
    {
        Heading("工具库", "按开发环境和用途整理。版本、安装位置与随附命令，在工具详情中查看。", Row(Button("快速扫描", vm.ScanAsync), Button("完整扫描", vm.FullScanAsync)));
        var section = new RadioButtons { MaxColumns = 3, ItemsSource = new[] { "开发环境", "开发工具", "待识别命令" }, SelectedIndex = toolsSection };
        PageContent.Children.Add(section);
        var search = new AutoSuggestBox { PlaceholderText = "搜索工具名称或用途…", Width = 400, Text = toolsSearch };
        var installedOnly = new CheckBox { Content = "仅显示已发现", IsChecked = toolsInstalledOnly, VerticalAlignment = VerticalAlignment.Center };
        PageContent.Children.Add(Row(search, installedOnly));
        var results = new StackPanel { Spacing = 16 }; PageContent.Children.Add(results);
        var environmentIds = new[] { "java", "python", "flutter", "dart", "node", "go", "rust", "dotnet", "android" };
        void Populate()
        {
            results.Children.Clear();
            var query = search.Text.Trim();
            bool Matches(ToolDefinition d) => (d.Name + " " + d.Description + " " + string.Join(' ', vm.Installations.Where(i => i.ToolId == d.Id).Select(i => i.ExecutablePath))).Contains(query, StringComparison.OrdinalIgnoreCase);
            var known = vm.Definitions.Where(d => (toolsSection == 0 ? environmentIds.Contains(d.Id) : !environmentIds.Contains(d.Id)) && Matches(d) && (!toolsInstalledOnly || vm.Installations.Any(i => i.ToolId == d.Id))).ToList();
            if (toolsSection == 0)
            {
                results.Children.Add(Muted($"{known.Count} 种开发环境 · 同一环境的所有版本合并展示"));
                var grid = new Grid { ColumnSpacing = 16, RowSpacing = 16 };
                grid.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) }); grid.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
                for (var i = 0; i < known.Count; i++)
                {
                    if (i % 2 == 0) grid.RowDefinitions.Add(new() { Height = GridLength.Auto });
                    var def = known[i]; var installs = vm.Installations.Where(t => t.ToolId == def.Id).ToList();
                    var active = installs.FirstOrDefault(t => t.IsActive && t.Owner is null) ?? installs.FirstOrDefault(t => t.Owner is null) ?? installs.FirstOrDefault();
                    var title = new Grid(); title.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) }); title.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
                    title.Children.Add(Text(def.Name, 21, true)); var badge = new Border { Padding = new Thickness(9, 4, 9, 4), CornerRadius = new CornerRadius(12), Background = (Brush)Application.Current.Resources["SubtleFillColorSecondaryBrush"], Child = Text(installs.Count == 0 ? "未安装" : $"{installs.Count} 个版本 / 入口", 11) }; Grid.SetColumn(badge, 1); title.Children.Add(badge);
                    var card = Card(Stack(title, Muted(def.Description, 13), Text(active?.Version ?? "选择一个版本开始", 17), Button("管理版本与配置", () => ShowToolAsync(def))));
                    Grid.SetColumn(card, i % 2); Grid.SetRow(card, i / 2); grid.Children.Add(card);
                }
                results.Children.Add(grid);
            }
            else if (toolsSection == 1)
            {
                foreach (var group in known.GroupBy(d => d.Category).OrderBy(g => g.Key))
                {
                    var content = new StackPanel { Spacing = 8 }; content.Children.Add(Text(group.Key, 18, true));
                    foreach (var def in group.OrderBy(d => d.Name))
                    {
                        var installs = vm.Installations.Where(i => i.ToolId == def.Id).ToList(); var active = installs.FirstOrDefault(i => i.IsActive) ?? installs.FirstOrDefault();
                        var line = new Grid { ColumnSpacing = 16, Padding = new Thickness(0, 8, 0, 8) }; line.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) }); line.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
                        var info = Stack(Text(def.Name, 16, true), Muted(def.Description + (active is null ? "" : " · " + active.Version))); info.Spacing = 4; line.Children.Add(info);
                        var manage = Button(installs.Count == 0 ? "安装" : "管理", () => ShowToolAsync(def)); Grid.SetColumn(manage, 1); line.Children.Add(manage); content.Children.Add(line);
                    }
                    results.Children.Add(Card(content));
                }
            }
            else
            {
                var unknown = vm.Installations.Where(i => i.ToolId.StartsWith("unknown:") && (i.Name + " " + i.ExecutablePath).Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();
                results.Children.Add(new InfoBar { IsOpen = true, IsClosable = false, Severity = InfoBarSeverity.Informational, Title = "这些通常是工具链附带的小命令", Message = "按来源收起显示，需要时展开或搜索。不会自动执行未知命令来查询版本。" });
                foreach (var group in unknown.GroupBy(i => i.Owner ?? Path.GetDirectoryName(i.ExecutablePath) ?? i.Source).OrderByDescending(g => g.Count()))
                {
                    var list = PageList(true);
                    foreach (var item in group.OrderBy(i => i.Name))
                    {
                        var row = Stack(Text(item.Name, 14, true), Muted(item.ExecutablePath, 11)); row.Tag = item; row.Spacing = 3; row.Padding = new Thickness(4, 6, 4, 6); list.Items.Add(row);
                    }
                    list.ItemClick += async (_, e) => { if (e.ClickedItem is StackPanel { Tag: ToolInstallation item }) await Guard(() => ShowToolAsync(new() { Id = item.ToolId, Name = item.Name, Category = "待识别 CLI", Description = "已发现的命令入口", Executables = [Path.GetFileName(item.ExecutablePath)] })); };
                    results.Children.Add(new Expander { Header = group.Key + $" · {group.Count()} 个命令", Content = list, IsExpanded = query.Length > 0, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch });
                }
                if (unknown.Count == 0) results.Children.Add(Muted("没有匹配的待识别命令。"));
            }
            if (toolsSection != 2 && known.Count == 0) results.Children.Add(Card(Stack(Text("没有匹配的工具", 17, true), Muted("可取消“仅显示已发现”、调整搜索条件，或在设置中添加扫描目录。"))));
        }
        section.SelectionChanged += (_, _) => { toolsSection = Math.Max(0, section.SelectedIndex); Populate(); };
        search.TextChanged += (_, _) => { toolsSearch = search.Text; Populate(); };
        installedOnly.Checked += (_, _) => { toolsInstalledOnly = true; Populate(); }; installedOnly.Unchecked += (_, _) => { toolsInstalledOnly = false; Populate(); };
        Populate();
    }
}
