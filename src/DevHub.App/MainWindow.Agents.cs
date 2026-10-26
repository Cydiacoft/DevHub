using DevHub.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DevHub.App;

public sealed partial class MainWindow
{
    private void RenderAgents()
    {
        Heading("AI 工具", "查看 Agent / AI 客户端、MCP 配置和项目里的 Agent、Skills 与规则。", Button("重新检测", RefreshAgentsPageAsync, true));
        if (vm.Agents is not { } snapshot)
        {
            PageContent.Children.Add(Card(Stack(Text("检测你的 AI 开发环境", 20, true),
                Muted("支持 Codex CLI、Claude Code、Gemini CLI、OpenCode、Aider、Goose、GitHub Copilot CLI，以及 Cursor、VS Code 和 Claude Desktop。"),
                Muted("检查常见配置位置和项目目录；只读取配置结构与文件位置，不启动 Agent 或 MCP。"), Button("开始检测", RefreshAgentsPageAsync))));
            return;
        }
        var counts = new Grid { ColumnSpacing = 14 };
        var values = new[] { ("已发现安装", snapshot.Clients.Count(c => c.Installations.Count > 0).ToString()), ("MCP 配置", snapshot.McpServers.Count.ToString()),
            ("Agent / Skills / 规则", snapshot.Definitions.Count.ToString()), ("检查项目", snapshot.ProjectCount.ToString()) };
        for (var i = 0; i < values.Length; i++)
        {
            counts.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
            var card = Card(Stack(Muted(values[i].Item1), Text(values[i].Item2, 28, true))); Grid.SetColumn(card, i); counts.Children.Add(card);
        }
        PageContent.Children.Add(counts);
        PageContent.Children.Add(Muted($"检测于 {snapshot.ScannedAt:yyyy-MM-dd HH:mm} · 配置按来源展示，客户端的信任、授权和会话覆盖可能影响实际生效结果。"));
        var tabs = new RadioButtons { MaxColumns = 4, ItemsSource = new[] { "Agent 与客户端", "MCP 配置", "子 Agent / Skills / 规则", "配置文件" }, SelectedIndex = agentSection };
        var search = new AutoSuggestBox { Text = agentSearch, PlaceholderText = "搜索名称、客户端或项目路径…", MaxWidth = 560, HorizontalAlignment = HorizontalAlignment.Stretch };
        var showMissing = new CheckBox { Content = "显示未发现的工具", Visibility = agentSection == 0 ? Visibility.Visible : Visibility.Collapsed };
        PageContent.Children.Add(tabs); PageContent.Children.Add(search);
        PageContent.Children.Add(showMissing);
        var content = new StackPanel { Spacing = 14 }; PageContent.Children.Add(content);
        void Populate()
        {
            content.Children.Clear(); var query = search.Text.Trim();
            bool Matches(string value) => value.Contains(query, StringComparison.OrdinalIgnoreCase);
            if (agentSection == 0)
            {
                foreach (var client in snapshot.Clients.Where(c => (showMissing.IsChecked == true || c.Installations.Count > 0 || c.ConfigCount > 0 || c.ProcessCount > 0) && Matches(c.Name + " " + c.Status)).OrderByDescending(c => c.Installations.Count > 0).ThenByDescending(c => c.ConfigCount))
                {
                    var body = Stack(Text(client.Name, 19, true), Muted(client.Kind + " · " + client.Status));
                    if (client.Installations.Count > 0)
                        foreach (var install in client.Installations)
                        {
                            body.Children.Add(Text(install.Version + (install.IsActive ? " · PATH 当前入口" : ""), 15));
                            body.Children.Add(Muted(install.ExecutablePath));
                            body.Children.Add(Muted(install.Source + (install.Owner is null ? "" : " · 随附于 " + install.Owner)));
                            body.Children.Add(ActionButton("打开安装目录", () => ProcessRunner.OpenDirectory(Path.GetDirectoryName(install.ExecutablePath)!)));
                        }
                    body.Children.Add(Muted($"{client.ConfigCount} 个专用配置 / 定义记录 · {client.ProcessCount} 个同名进程（仅作参考，不代表会话或连接状态）"));
                    if (client.ConfigCount > 0)
                        body.Children.Add(ActionButton("查看相关配置", () => { agentSection = snapshot.Configurations.Any(c => c.ClientId == client.Id) ? 3 : 2; agentSearch = client.Name; RenderPage(); }));
                    content.Children.Add(Card(body));
                }
            }
            else if (agentSection == 1)
            {
                foreach (var group in snapshot.McpServers.Where(m => Matches(m.Name + " " + m.ClientName + " " + m.Scope + " " + m.ConfigPath)).GroupBy(m => m.ClientName))
                {
                    var entries = new StackPanel { Spacing = 12 };
                    foreach (var server in group)
                    {
                        var body = Stack(Text(server.Name, 17, true), Text(server.DependencyStatus), Muted(server.Scope), Muted(server.Transport));
                        if (server.ResolvedExecutable is not null) body.Children.Add(Muted("启动入口：" + server.ResolvedExecutable));
                        if (server.Endpoint.Length > 0) body.Children.Add(Muted("服务地址：" + server.Endpoint + "（隐藏路径、参数和凭据）"));
                        if (server.EnvironmentNames.Length > 0) body.Children.Add(Muted("环境变量名称：" + string.Join("、", server.EnvironmentNames)));
                        body.Children.Add(Muted("配置来源：" + server.ConfigPath));
                        body.Children.Add(ActionButton("打开配置所在目录", () => ProcessRunner.OpenDirectory(Path.GetDirectoryName(server.ConfigPath)!)));
                        entries.Children.Add(Card(body));
                    }
                    content.Children.Add(new Expander { Header = group.Key + $" · {group.Count()} 条配置", Content = entries, IsExpanded = query.Length > 0,
                        HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch });
                }
                content.Children.Add(Muted("这里检查配置和启动入口；连接、登录和工具调用均未测试。相同名称的多条配置保留各自来源，不按有效服务数合并。"));
            }
            else if (agentSection == 2)
            {
                foreach (var group in snapshot.Definitions.Where(d => Matches(d.Name + " " + d.ClientName + " " + d.Scope + " " + d.Path)).GroupBy(d => d.Kind + " · " + d.ClientName))
                {
                    var entries = new StackPanel { Spacing = 10 };
                    foreach (var definition in group)
                        entries.Children.Add(Card(Stack(Text(definition.Name, 16, true), Muted(definition.Scope), Muted(definition.Path),
                            ActionButton("打开所在目录", () => ProcessRunner.OpenDirectory(Path.GetDirectoryName(definition.Path)!)))));
                    content.Children.Add(new Expander { Header = group.Key + $" · {group.Count()} 项", Content = entries, IsExpanded = query.Length > 0,
                        HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch });
                }
                content.Children.Add(Muted("按文件和配置中的定义发现，不读取提示词正文，不代表客户端已加载。内置 Agent、会话临时 Agent 和云端定义不在此列表。"));
            }
            else
            {
                foreach (var file in snapshot.Configurations.Where(c => Matches(c.ClientName + " " + c.Scope + " " + c.Path)))
                    content.Children.Add(Card(Stack(Text(file.ClientName, 17, true), Text(file.Status), Muted(file.Scope), Muted(file.Path),
                        ActionButton("打开配置所在目录", () => ProcessRunner.OpenDirectory(Path.GetDirectoryName(file.Path)!)))));
            }
            if (content.Children.Count == 0) content.Children.Add(Card(Text("没有匹配的记录。")));
        }
        tabs.SelectionChanged += (_, _) => { agentSection = tabs.SelectedIndex; showMissing.Visibility = agentSection == 0 ? Visibility.Visible : Visibility.Collapsed; Populate(); };
        showMissing.Checked += (_, _) => Populate(); showMissing.Unchecked += (_, _) => Populate();
        search.TextChanged += (_, _) => { agentSearch = search.Text; Populate(); };
        Populate();
    }
    private async Task RefreshAgentsPageAsync()
    {
        await vm.RefreshAgentsAsync(); if (currentPage == "agents") RenderPage();
    }
}
