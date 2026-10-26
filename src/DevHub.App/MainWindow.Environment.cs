using DevHub.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DevHub.App;

public sealed partial class MainWindow
{
    private async Task ShowEnvironmentEvidenceAsync()
    {
        var contexts = new[] { "DevHub 当前进程", "新终端环境" }.Concat(vm.Projects.Select(p => "项目 · " + p.Name)).ToArray();
        var selector = new ComboBox { Header = "要检查的环境", ItemsSource = contexts, SelectedIndex = 1, MinWidth = 440 };
        if (await ShowAsync(new ContentDialog { Title = "环境冲突解释", Content = Stack(selector, Muted("使用 PowerShell 无个人配置模式解析，不执行个人脚本、后台服务或项目构建。")), PrimaryButtonText = "检查", CloseButtonText = "取消" }) != ContentDialogResult.Primary) return;
        var project = selector.SelectedIndex >= 2 ? vm.Projects[selector.SelectedIndex - 2] : null;
        var environment = selector.SelectedIndex == 0 ? EnvironmentDoctor.CurrentEnvironment() : vm.EnvironmentService.FreshEnvironment();
        if (project is not null) environment = vm.ProjectService.BuildEnvironment(project, vm.Installations, environment);
        var doctor = new EnvironmentDoctor(vm.Paths, vm.Runner);
        var contextName = contexts[selector.SelectedIndex];
        var evidence = await vm.RunAsync("解析环境命令", (_, ct) => doctor.InspectAsync(contextName, environment, vm.Definitions, vm.Installations, project, ct));
        var search = new AutoSuggestBox { PlaceholderText = "搜索命令名称…", MinWidth = 440 };
        var list = new ListView { Height = 330, IsItemClickEnabled = true, SelectionMode = ListViewSelectionMode.None };
        void Populate()
        {
            list.Items.Clear();
            foreach (var trace in evidence.Commands.Where(t => (t.Name + t.Command).Contains(search.Text, StringComparison.OrdinalIgnoreCase)))
            {
                var row = Stack(Text(trace.Name + " · " + trace.Command, 16, true), Muted(trace.SelectedPath ?? "未找到命令")); row.Tag = trace; list.Items.Add(row);
            }
        }
        search.TextChanged += (_, _) => Populate(); Populate();
        var issues = string.Join("\n\n", evidence.Issues.Select(i => i.Title + "\n" + i.Detail));
        var dialog = new ContentDialog { Title = evidence.Name, Content = Stack(search, list, Muted($"{evidence.Issues.Count} 项诊断 · 点击命令查看完整候选顺序")), CloseButtonText = "关闭", PrimaryButtonText = "诊断与变量" };
        CommandTrace? selected = null;
        list.ItemClick += (_, e) => { selected = (e.ClickedItem as FrameworkElement)?.Tag as CommandTrace; dialog.Hide(); };
        var choice = await ShowAsync(dialog);
        if (selected is not null)
        {
            var details = selected.Explanation + "\n\n" + string.Join("\n\n", selected.Candidates.Select(c => $"{c.Order}. {c.Path}\n真实路径：{c.RealPath}\n{c.Source}"));
            if (await ShowAsync(new ContentDialog { Title = selected.Name + " · 命令解析", Content = new ScrollViewer { Content = Text(details), MaxHeight = 450 }, PrimaryButtonText = "查询入口版本", CloseButtonText = "关闭" }) == ContentDialogResult.Primary)
            {
                var output = await vm.RunAsync("核验入口版本", (_, ct) => doctor.ProbeAsync(selected, environment, project?.Directory ?? vm.Paths.Root, ct));
                await MessageAsync(selected.Name + " · 核验结果", output);
            }
        }
        else if (choice == ContentDialogResult.Primary)
            await MessageAsync("诊断与环境变量", (issues.Length == 0 ? "未发现已覆盖的环境冲突。" : issues) + "\n\n" + string.Join("\n", evidence.Variables.Select(v => v.Key + "=" + v.Value)));
    }
}
