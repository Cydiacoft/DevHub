using DevHub.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DevHub.App;

public sealed partial class MainWindow
{
    private async Task ShowToolAsync(ToolDefinition definition, string? selectedInstallationId = null)
    {
        var installations = vm.Installations.Where(t => t.ToolId == definition.Id || definition.Id == "dotnet" && t.ToolId == "dotnet-runtime").ToList();
        var initialIndex = selectedInstallationId is null ? 0 : Math.Max(0, installations.FindIndex(t => t.Id == selectedInstallationId));
        var combo = new ComboBox { Header = "安装 / 命令入口", ItemsSource = installations, SelectedIndex = installations.Count > 0 ? initialIndex : -1, MinWidth = 440, MaxWidth = 650, HorizontalAlignment = HorizontalAlignment.Stretch };
        var detail = new TextBox { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 135, MaxWidth = 650 };
        var actions = new StackPanel { Spacing = 10 };
        var body = Stack(Muted(definition.Description, 14), combo, detail, actions);
        Func<Task>? next = null;
        var dialog = new ContentDialog { Title = definition.Name, Content = new ScrollViewer { Content = body, MaxHeight = 560 }, CloseButtonText = "关闭" };
        Button Then(string label, Func<Task> operation, bool accent = false) => Button(label, () => { next = operation; dialog.Hide(); return Task.CompletedTask; }, accent);
        void Populate()
        {
            actions.Children.Clear();
            var selected = combo.SelectedItem as ToolInstallation;
            detail.Text = selected is null ? "尚未发现此工具。可安装新版本，或在设置中添加扫描目录。" :
                $"版本：{selected.Version}\n位置：{selected.HomePath}\n入口：{selected.ExecutablePath}\n真实入口：{selected.RealPath}\n来源：{selected.Source}\n状态：{selected.Status}{(selected.Vendor.Length > 0 ? "\n发行方：" + selected.Vendor : "")}";
            if (definition.Id.StartsWith("unknown:"))
            {
                actions.Children.Add(Then("添加版本识别规则", () => AddCustomToolAsync(definition)));
                if (selected is not null) actions.Children.Add(ActionButton("打开所在目录", () => ProcessRunner.OpenDirectory(selected.HomePath)));
                return;
            }
            if (definition.Id is "java" or "python" or "flutter" or "dart" or "node" or "go" or "rust" or "dotnet" or "android")
                actions.Children.Add(Then("安装 / 下载其他版本", () => { requestedVersionTool = definition.Id; Navigate("versions"); return Task.CompletedTask; }, true));
            if (selected is not null)
            {
                actions.Children.Add(ActionButton("打开所在目录", () => ProcessRunner.OpenDirectory(selected.HomePath)));
                if (selected.Owner is null && definition.Id is "java" or "python" or "flutter" or "dart" or "node" or "go" or "android" or "maven" or "gradle")
                    actions.Children.Add(Then("预览全局切换", () => SwitchGlobalAsync(selected)));
                if (definition.Id == "rust" && selected.SelectionVersion is not null)
                    actions.Children.Add(Then("设为 Rust 默认工具链", () => RunPreviewedCommandAsync("切换 Rust 默认版本", vm.Management.RustCommand("default", selected.SelectionVersion))));
                if (definition.Id == "python") actions.Children.Add(Then("Python 环境与 pip 包", PythonPackagesAsync));
                if (definition.Id == "python" && selected.Source == "uv")
                {
                    actions.Children.Add(Then("通过 uv 更新补丁版本", () => RunPreviewedCommandAsync("更新 Python 补丁版本", vm.Management.PythonMaintenance(selected, "update"))));
                    actions.Children.Add(Then("通过 uv 移除此版本…", () => RemoveVersionAsync(selected)));
                }
                if (definition.Id == "node") actions.Children.Add(Then("npm / pnpm / Yarn 包", () => NodePackagesAsync(selected)));
                if (definition.Id == "android") actions.Children.Add(Then("Android 平台与组件", () => AndroidPackagesAsync(selected)));
                if (definition.Id == "flutter") actions.Children.Add(Then("运行 Flutter 环境诊断", async () =>
                {
                    var output = await vm.ExecuteAsync("Flutter doctor", new(selected.ExecutablePath, ["doctor", "-v"], vm.Paths.Root, TimeoutSeconds: 240));
                    await MessageAsync("Flutter 环境诊断", output);
                }));
                if (definition.Id is "cargo" or "dotnet" or "conda" or "poetry" or "uv") actions.Children.Add(Then("查看环境 / 已安装工具", async () =>
                {
                    var args = definition.Id switch { "cargo" => new[] { "install", "--list" }, "dotnet" => ["tool", "list", "--global"], "conda" => ["env", "list", "--json"], "poetry" => ["env", "list", "--full-path"], _ => new[] { "tool", "list" } };
                    await MessageAsync(definition.Name + " 清单", await vm.ExecuteAsync("查询 " + definition.Name, new(selected.ExecutablePath, args, vm.Paths.Root)));
                }));
                var references = vm.Projects.Where(p => p.Bindings.Values.Contains(selected.Id)).Select(p => p.Name).ToList();
                if (references.Count > 0) actions.Children.Add(Muted("项目引用：" + string.Join("、", references)));
                if (selected.Owner is null && (selected.Managed || selected.SelectionVersion is not null && definition.Id == "rust"))
                    actions.Children.Add(Then("移除此版本…", () => RemoveVersionAsync(selected)));
                else if (selected.Owner is null && WindowsInstallService.FindUninstaller(selected) is { } uninstaller)
                    actions.Children.Add(Then("通过原安装器卸载…", async () =>
                    {
                        if (references.Count > 0) throw new InvalidOperationException("请先解除项目绑定：" + string.Join("、", references));
                        var home = Environment.GetEnvironmentVariable("JAVA_HOME");
                        if (selected.IsActive || home is not null && DiscoveryService.SamePath(home, selected.HomePath)) throw new InvalidOperationException("此版本仍在默认环境中使用，请先切换后再卸载。");
                        if (selected.ToolId == "java" && vm.Installations.Any(i => i.ToolId == "java" && i.IsActive && i.Owner == "Oracle 命令入口" && ProjectService.CleanVersion(i.Version) == ProjectService.CleanVersion(selected.Version))) throw new InvalidOperationException("Oracle 的当前命令入口仍使用此 Java 版本，请先切换默认版本。");
                        if (!await ConfirmAsync("卸载 " + uninstaller.Name, $"安装位置：{selected.HomePath}\n启动原安装器：{uninstaller.Command}\n将打开安装器自己的确认界面。", "打开卸载器")) return;
                        await vm.RunAsync("卸载 " + uninstaller.Name, async (_, _) => { await WindowsInstallService.LaunchUninstallerAsync(uninstaller); return true; }); await vm.ScanAsync();
                    }));
            }
            if (definition.WingetId is not null || definition.ScoopId is not null || definition.ChocoId is not null)
                actions.Children.Add(Then("通过包管理器安装 / 更新 / 卸载…", () => ManageViaPackageManagerAsync(definition, selected)));
            if (definition.Website is not null) actions.Children.Add(ActionButton("打开官方网站", () => ProcessRunner.OpenWebsite(definition.Website)));
        }
        combo.SelectionChanged += (_, _) => Populate(); Populate();
        await ShowAsync(dialog); if (next is not null) await next();
    }

    private async Task ShowInventoryDetailsAsync(bool showVersions)
    {
        var inventory = vm.Installations.Where(i => !i.ToolId.StartsWith("unknown:")).ToList();
        var search = new AutoSuggestBox { PlaceholderText = "搜索工具、版本或安装位置…", MinWidth = 440 };
        var list = new ListView { IsItemClickEnabled = true, SelectionMode = ListViewSelectionMode.None, Height = 430, MinWidth = 440, MaxWidth = 680 };
        var summary = Muted("");
        void Populate()
        {
            list.Items.Clear();
            var filtered = inventory.Where(i => (i.Name + " " + i.Version + " " + i.HomePath).Contains(search.Text ?? "", StringComparison.OrdinalIgnoreCase));
            if (showVersions)
            {
                foreach (var item in filtered.OrderBy(i => i.Name).ThenBy(i => i.HomePath))
                {
                    var row = Stack(Text(item.Title, 15, true), Muted(item.HomePath, 11), Muted(item.Source + " · " + item.Status, 11)); row.Tag = item; row.Spacing = 4; row.Padding = new Thickness(4, 8, 4, 8); list.Items.Add(row);
                }
            }
            else
            {
                foreach (var group in filtered.GroupBy(i => i.ToolId).OrderBy(g => g.First().Name))
                {
                    var item = group.FirstOrDefault(i => i.IsActive) ?? group.First();
                    var row = Stack(Text(item.Name, 16, true), Muted($"{group.Count()} 个安装 / 入口 · {item.Category}")); row.Tag = item; row.Spacing = 4; row.Padding = new Thickness(4, 10, 4, 10); list.Items.Add(row);
                }
            }
            summary.Text = $"{list.Items.Count} 条记录 · 点击可查看位置、版本和管理操作";
        }
        search.TextChanged += (_, _) => Populate(); Populate();
        ToolInstallation? selected = null;
        var dialog = new ContentDialog { Title = showVersions ? "安装与版本详情" : "已识别工具详情", Content = Stack(search, summary, list), CloseButtonText = "关闭" };
        list.ItemClick += (_, e) => { if (e.ClickedItem is StackPanel { Tag: ToolInstallation item }) { selected = item; dialog.Hide(); } };
        await ShowAsync(dialog);
        if (selected is not null)
        {
            var toolId = selected.ToolId == "dotnet-runtime" ? "dotnet" : selected.ToolId;
            var definition = vm.Definitions.FirstOrDefault(d => d.Id == toolId);
            if (definition is not null) await ShowToolAsync(definition, selected.Id);
        }
    }

    private async Task SwitchGlobalAsync(ToolInstallation installation)
    {
        var scope = new ComboBox { Header = "修改范围", ItemsSource = new[] { "当前用户（默认，不请求管理员权限）", "整个系统（需要管理员授权）" }, SelectedIndex = 0, MinWidth = 430 };
        if (await ShowAsync(new ContentDialog { Title = "设置默认 " + installation.Name, Content = Stack(scope, Muted("系统 PATH 中已有的入口可能优先于用户 PATH；会先检查修改后的候选顺序。")), PrimaryButtonText = "生成预览", CloseButtonText = "取消" }) != ContentDialogResult.Primary) return;
        var request = vm.EnvironmentService.PlanSwitch(installation, scope.SelectedIndex == 0 ? "User" : "Machine");
        var expectedEnvironment = vm.EnvironmentService.PreviewEnvironment(request);
        var commandName = Path.GetFileNameWithoutExtension(installation.ExecutablePath);
        var candidate = EnvironmentDoctor.Resolve(commandName, expectedEnvironment).FirstOrDefault();
        if (request.Scope == "User" && (candidate is null || !DiscoveryService.SamePath(candidate.RealPath, EnvironmentDoctor.RealPath(installation.ExecutablePath))))
        {
            await MessageAsync("用户级修改无法改变当前优先入口", "系统 PATH 中的入口仍会优先命中：\n" + (candidate?.Path ?? "未找到") + "\n\n配置尚未修改。可以使用项目环境终端，或重新选择系统级修复范围。"); return;
        }
        var preview = string.Join("\n\n", request.Changes.Select(c => $"[{c.Before.Scope}] {c.Before.Name}\n原值：{c.Before.Value ?? "（未设置）"}\n新值：{c.After.Value}"));
        if (!await ConfirmAsync("预览默认环境修改", request.Description + "\n\n" + preview + "\n\n将保存备份。" + (request.Scope == "Machine" ? "需要 Windows 管理员授权。" : "只修改当前用户配置。") + "已有终端需要重新打开。", "应用切换")) return;
        await vm.RunAsync("切换全局 " + installation.Name, async (_, _) => { await vm.EnvironmentService.ApplyElevatedAsync(request); return true; });
        var fresh = vm.EnvironmentService.FreshEnvironment();
        var evidence = await vm.RunAsync("验证修改后的命令解析", (_, ct) => new EnvironmentDoctor(vm.Paths, vm.Runner).InspectAsync("新终端", fresh, vm.Definitions, vm.Installations, ct: ct));
        var verified = evidence.Commands.FirstOrDefault(t => t.ToolId == installation.ToolId);
        if (verified?.SelectedPath is null || !DiscoveryService.SamePath(EnvironmentDoctor.RealPath(verified.SelectedPath), EnvironmentDoctor.RealPath(installation.ExecutablePath)))
            throw new InvalidOperationException("配置已写入，但新终端命令没有命中所选入口：" + (verified?.SelectedPath ?? "未找到") + "。请查看环境诊断，或在设置中恢复备份。");
        if (installation.ToolId == "java")
        {
            var javaTrace = evidence.Commands.First(t => t.Command == "java"); var javacTrace = evidence.Commands.First(t => t.Command == "javac");
            if (javaTrace.SelectedPath is null || javacTrace.SelectedPath is null || !DiscoveryService.SamePath(EnvironmentDoctor.RealPath(javaTrace.SelectedPath), EnvironmentDoctor.RealPath(Path.Combine(installation.HomePath, "bin", "java.exe"))) || !DiscoveryService.SamePath(EnvironmentDoctor.RealPath(javacTrace.SelectedPath), EnvironmentDoctor.RealPath(Path.Combine(installation.HomePath, "bin", "javac.exe"))))
                throw new InvalidOperationException("配置已写入，但新环境中的 java / javac 没有命中所选 JDK。请查看环境诊断，或在设置中恢复备份。");
            var java = await vm.ExecuteAsync("验证 Java 新环境", new(javaTrace.SelectedPath, ["-version"], vm.Paths.Root, fresh, 15, PreserveHostEnvironment: true));
            var javac = await vm.ExecuteAsync("验证 javac 新环境", new(javacTrace.SelectedPath, ["-version"], vm.Paths.Root, fresh, 15, PreserveHostEnvironment: true));
            await MessageAsync("Java 全局配置已更新", "JAVA_HOME：" + fresh.GetValueOrDefault("JAVA_HOME") + "\n\n" + java + "\n" + javac + "\n\n请重新打开终端和需要使用该环境的开发工具。");
        }
        else
        {
            var version = await vm.RunAsync("查询修改后的入口版本", (_, ct) => new EnvironmentDoctor(vm.Paths, vm.Runner).ProbeAsync(verified, fresh, vm.Paths.Root, ct));
            await MessageAsync("默认配置已更新", "新环境命中：" + verified.SelectedPath + "\n\n" + version + "\n\n请重新打开终端。也可从项目页打开使用指定环境的终端。");
        }
        // Bring this process in sync only after successful validation; existing shells remain unchanged.
        foreach (var pair in fresh) Environment.SetEnvironmentVariable(pair.Key, pair.Value);
        await vm.ScanAsync();
    }
    private async Task RemoveVersionAsync(ToolInstallation installation)
    {
        var references = vm.Projects.Where(p => p.Bindings.Values.Contains(installation.Id)).ToList();
        if (references.Count > 0) throw new InvalidOperationException("请先解除这些项目的绑定：" + string.Join("、", references.Select(p => p.Name)));
        if (installation.IsActive) throw new InvalidOperationException("该版本仍是当前命令入口，请先切换到其他版本并重新扫描。");
        if (!await ConfirmAsync("移除 " + installation.Title, "位置：" + installation.HomePath + "\n来源：" + installation.Source + "\nDevHub 直接安装的 SDK 将移入待清理区，可在缓存与空间中永久清理；uv / rustup 版本通过原管理器移除。", "移除版本")) return;
        if (installation.ToolId == "python" && installation.Source == "uv")
            await vm.ExecuteAsync("通过 uv 移除 Python", vm.Management.PythonMaintenance(installation, "remove"));
        else if (installation.ToolId == "rust" && installation.SelectionVersion is not null)
            await vm.ExecuteAsync("移除 Rust 工具链", vm.Management.RustCommand("remove", installation.SelectionVersion));
        else await vm.RunAsync("移除 " + installation.Name, async (_, ct) => { await vm.Management.RemoveManagedAsync(installation, vm.Projects, ct); return true; });
        await vm.ScanAsync();
    }
    private async Task RunPreviewedCommandAsync(string title, CommandSpec command, bool agreement = false)
    {
        if (!await ConfirmAsync(title, "将执行：\n" + command.Display + "\n\n工作目录：" + (command.WorkingDirectory ?? vm.Paths.Root), "执行", agreement)) return;
        var output = await vm.ExecuteAsync(title, command);
        await MessageAsync(title + " · 已完成", string.IsNullOrWhiteSpace(output) ? "操作完成。" : output.Length > 15000 ? output[^15000..] : output);
    }

    private async Task ManageViaPackageManagerAsync(ToolDefinition definition, ToolInstallation? installation)
    {
        if (installation?.Owner is not null) throw new InvalidOperationException("该入口属于 " + installation.Owner + "。请安装独立版本后管理。");
        if (definition.Id == "python" && installation is not null)
        {
            var version = string.Join('.', ProjectService.CleanVersion(installation.Version).Split('.').Take(2));
            if (version.Length > 0) definition = definition with { WingetId = "Python.Python." + version };
        }
        var managers = new List<string>();
        if (definition.WingetId is not null) managers.Add("winget"); if (definition.ScoopId is not null) managers.Add("scoop"); if (definition.ChocoId is not null) managers.Add("choco");
        var source = new ComboBox { Header = "安装来源（更新 / 卸载请选择原来源）", ItemsSource = managers, SelectedIndex = Math.Max(0, managers.IndexOf(vm.Settings.PreferredPackageManager)), MinWidth = 420 };
        var canMaintain = installation is not null && !installation.Managed && installation.Source != "uv";
        var action = new ComboBox { Header = "操作", ItemsSource = canMaintain ? new[] { "安装", "更新", "卸载" } : ["安装"], SelectedIndex = canMaintain ? 1 : 0, MinWidth = 420 };
        var description = Text("");
        void Update()
        {
            try { description.Text = vm.Packages.ManagerCommand(definition, source.SelectedItem?.ToString() ?? "winget", new[] { "install", "update", "remove" }[action.SelectedIndex]).Display; }
            catch (Exception e) { description.Text = e.Message; }
        }
        source.SelectionChanged += (_, _) => Update(); action.SelectionChanged += (_, _) => Update(); Update();
        var dialog = new ContentDialog { Title = "包管理器 · " + definition.Name, Content = Stack(source, action, description, Muted("操作会调用所选来源。更新与卸载前会检查该来源是否登记此工具。")), PrimaryButtonText = "查看执行预览", CloseButtonText = "取消" };
        if (await ShowAsync(dialog) != ContentDialogResult.Primary) return;
        var chosenAction = new[] { "install", "update", "remove" }[action.SelectedIndex]; var manager = source.SelectedItem!.ToString()!;
        if (chosenAction != "install")
        {
            if (installation is not null && vm.Projects.Any(p => p.Bindings.Values.Contains(installation.Id)) && chosenAction == "remove") throw new InvalidOperationException("请先解除项目绑定。");
            if (installation is null) throw new InvalidOperationException("请先扫描并选择具体安装。");
            if (manager == "scoop" && !installation.ExecutablePath.Contains("\\scoop\\apps\\" + definition.ScoopId?.Split('/').Last() + "\\", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("所选入口未确认属于这个 Scoop 包。");
            if (manager != "scoop" && WindowsInstallService.FindUninstaller(installation) is null) throw new InvalidOperationException("无法将所选安装位置与系统安装记录对应，不能按猜测的包名更新或卸载。可安装独立版本或通过原来源管理。");
            if (chosenAction == "remove" && installation.IsActive) throw new InvalidOperationException("此版本仍是当前入口，请先切换默认版本。");
            await vm.RunAsync("确认安装来源", async (_, ct) => { await vm.Packages.VerifyManagerOwnershipAsync(definition, manager, ct); return true; });
        }
        await RunPreviewedCommandAsync(action.SelectedItem + " " + definition.Name, vm.Packages.ManagerCommand(definition, manager, chosenAction), chosenAction != "remove");
        await vm.ScanAsync();
    }

    private void RenderVersions()
    {
        Heading("版本管理", "从官方来源安装独立版本，或导入已下载的 ZIP。安装完成后再选择是否切换。");
        var ids = new[] { "java", "python", "flutter", "dart", "android", "node", "go", "rust", "dotnet" };
        var definitions = ids.Select(id => vm.Definitions.First(d => d.Id == id)).ToList();
        var tool = new ComboBox { Header = "开发环境", ItemsSource = definitions, DisplayMemberPath = "Name", Width = 190 };
        var vendor = new ComboBox { Header = "发行版 / 渠道", Width = 200 };
        var version = new TextBox { Header = "版本（留空查看可用版本）", PlaceholderText = "例如 21 或 3.13", Width = 260 };
        var status = Muted("版本列表由你查询时从官方获取。");
        var releases = PageList(); releases.MinHeight = 70;
        var installed = PageList();
        var detail = Muted("");
        var query = Button("查询可用版本", async () =>
        {
            var id = ((ToolDefinition)tool.SelectedItem).Id;
            if (id is "python" or "rust")
            {
                var exe = DiscoveryService.ResolveCommand(id == "python" ? ["uv.exe"] : new[] { "rustup.exe" }) ?? throw new InvalidOperationException("请先在工具页安装 " + (id == "python" ? "uv" : "rustup"));
                var output = await vm.ExecuteAsync("查询 " + id + " 版本", new(exe, id == "python" ? ["python", "list", "--all-versions", "--only-downloads"] : ["toolchain", "list"], vm.Paths.Root));
                await MessageAsync("可用 / 已安装版本", output); return;
            }
            var result = await vm.RunAsync("查询官方版本列表", (_, ct) => vm.Downloads.GetReleasesAsync(id, vendor.SelectedItem?.ToString() ?? "", version.Text, ct));
            releases.ItemsSource = result; status.Text = result.Count == 0 ? "未找到匹配的 Windows x64 版本，可调整筛选或打开官网。" : $"找到 {result.Count} 个版本。选中后可查看来源并安装。";
        }, true);
        var install = Button("安装所选版本", async () =>
        {
            var id = ((ToolDefinition)tool.SelectedItem).Id;
            if (id is "python" or "rust")
            {
                var cmd = id == "python" ? vm.Management.PythonInstall(version.Text.Trim()) : vm.Management.RustCommand("install", string.IsNullOrWhiteSpace(version.Text) ? "stable" : version.Text.Trim());
                await RunPreviewedCommandAsync("安装 " + id + " " + version.Text, cmd, true); await vm.ScanAsync(); return;
            }
            if (releases.SelectedItem is not DownloadRelease release) throw new InvalidOperationException("请先查询并选择一个版本。");
            if (release.Manual) { ProcessRunner.OpenWebsite(release.Url); await MessageAsync("官网下载", "下载 ZIP 后，使用“导入 ZIP 安装包”导入。管理器不会代填网站账户信息。"); return; }
            if (!await ConfirmAsync("安装 " + release, $"来源：{release.Url}\n目标：{Path.Combine(vm.Paths.Sdks, release.ToolId)}\n\n将校验下载文件并解压到独立目录，完成后可选择切换。", "下载并安装", true)) return;
            var destination = await vm.RunAsync("安装 " + release, (task, ct) => vm.Downloads.InstallAsync(release, vm.DownloadProgress(task), ct));
            await vm.ScanAsync(); await MessageAsync("安装完成", destination);
        });
        var import = Button("导入 ZIP 安装包", async () =>
        {
            var id = ((ToolDefinition)tool.SelectedItem).Id;
            if (id is "python" or "rust") throw new InvalidOperationException("Python 使用 uv 安装，Rust 使用 rustup 安装；已有版本可以添加扫描目录。");
            var archive = await PickZipAsync(); if (archive is null) return;
            if (!await ConfirmAsync("导入本地安装包", "安装包：" + archive + "\n类型：" + id + "\n本地包由你确认来源，解压时会检查路径和工具入口。", "导入")) return;
            var destination = await vm.RunAsync("导入 " + id, (_, ct) => vm.Downloads.ImportZipAsync(archive, id, string.IsNullOrWhiteSpace(version.Text) ? DateTime.Now.ToString("yyyyMMddHHmmss") : version.Text, ct));
            await vm.ScanAsync(); await MessageAsync("导入完成", destination);
        });
        void Configure()
        {
            var id = ((ToolDefinition)tool.SelectedItem).Id;
            vendor.ItemsSource = id switch { "java" => new[] { "Temurin", "Microsoft", "Zulu", "Oracle" }, "flutter" => ["stable", "beta", "全部"], "dotnet" => ["SDK", "Runtime"], "python" => ["uv · Astral Python"], "rust" => ["rustup"], _ => new[] { "官方" } };
            vendor.SelectedIndex = 0; version.Text = id switch { "java" => "21", "python" => "3.13", "rust" => "stable", "dotnet" => "10.0", _ => "" };
            releases.ItemsSource = null; detail.Text = "";
            installed.ItemsSource = vm.Installations.Where(i => i.ToolId == id).ToList();
            status.Text = id == "rust" ? "输入 stable、beta、nightly 或具体版本后安装；已有工具链由 rustup 管理。" : id == "python" ? "输入 Python 版本后安装；使用已有 uv，下载来源为 Astral Python 构建。" : "查询官方版本列表后选择安装。";
        }
        tool.SelectionChanged += (_, _) => Configure();
        releases.SelectionChanged += (_, _) => { if (releases.SelectedItem is DownloadRelease selected) detail.Text = selected.Url + "\n" + (selected.Sha256 is not null ? "SHA-256：" + selected.Sha256 : selected.Sha512 is not null ? "SHA-512：" + selected.Sha512 : "请通过官网手动下载并导入。"); };
        installed.DoubleTapped += async (_, _) => await Guard(() => ShowToolAsync((ToolDefinition)tool.SelectedItem));
        PageContent.Children.Add(Card(Stack(Row(tool, vendor, version), Row(query, install, import), status, releases, detail)));
        PageContent.Children.Add(Text("已发现的版本与入口", 19, true)); PageContent.Children.Add(installed);
        var index = requestedVersionTool is null ? 0 : Array.IndexOf(ids, requestedVersionTool); requestedVersionTool = null; tool.SelectedIndex = Math.Max(0, index);
    }

    private async Task PythonPackagesAsync()
    {
        var interpreters = vm.Installations.Where(i => i.ToolId == "python" && i.Version != "应用执行别名").ToList();
        foreach (var project in vm.Projects.Where(p => p.VirtualEnvironment is not null))
        {
            var exe = Path.Combine(project.VirtualEnvironment!, "Scripts", "python.exe");
            if (File.Exists(exe)) interpreters.Insert(0, new() { Id = project.Id, ToolId = "python", Name = project.Name + " · venv", ExecutablePath = exe, HomePath = project.VirtualEnvironment!, Version = "项目虚拟环境" });
        }
        var python = new ComboBox { Header = "明确选择 Python 环境", ItemsSource = interpreters, SelectedIndex = interpreters.Count > 0 ? 0 : -1, MinWidth = 440, MaxWidth = 620 };
        var action = new ComboBox { Header = "操作", ItemsSource = new[] { "查看已安装包", "安装包", "升级包", "卸载包" }, SelectedIndex = 0, MinWidth = 440 };
        var package = new TextBox { Header = "包名（安装可附版本，如 requests==2.32.3）", PlaceholderText = "查看包列表时可留空" };
        var target = Muted(""); python.SelectionChanged += (_, _) => target.Text = (python.SelectedItem as ToolInstallation)?.ExecutablePath ?? ""; target.Text = interpreters.FirstOrDefault()?.ExecutablePath ?? "";
        var dialog = new ContentDialog { Title = "Python / pip 包管理", Content = Stack(python, target, action, package, Muted("使用所选解释器的 python -m pip；建议为项目创建虚拟环境。")), PrimaryButtonText = "继续", CloseButtonText = "取消" };
        if (await ShowAsync(dialog) != ContentDialogResult.Primary) return;
        if (python.SelectedItem is not ToolInstallation selected) throw new InvalidOperationException("请先安装或扫描一个 Python 环境。");
        if (action.SelectedIndex == 0)
        {
            var packages = await vm.RunAsync("查询 pip 包", (_, ct) => vm.Packages.ListPythonAsync(selected.ExecutablePath, ct));
            await MessageAsync("已安装包 · " + selected.HomePath, string.Join('\n', packages));
        }
        else await RunPreviewedCommandAsync(action.SelectedItem + " · " + package.Text, vm.Packages.PythonPackageCommand(selected.ExecutablePath, new[] { "", "install", "upgrade", "uninstall" }[action.SelectedIndex], package.Text.Trim(), vm.Paths.Root));
    }
    private async Task NodePackagesAsync(ToolInstallation node)
    {
        var manager = new ComboBox { Header = "包管理器", ItemsSource = new[] { "npm", "pnpm", "yarn" }, SelectedIndex = 0, MinWidth = 420 };
        var project = new ComboBox { Header = "操作目标", MinWidth = 420 }; project.Items.Add("DevHub 中的此 Node.js 全局包"); foreach (var p in vm.Projects) project.Items.Add(p); project.SelectedIndex = 0;
        var action = new ComboBox { Header = "操作", ItemsSource = new[] { "查看 npm 包清单", "安装包", "升级包", "卸载包" }, SelectedIndex = 0, MinWidth = 420 };
        var name = new TextBox { Header = "包名（例如 pnpm、yarn 或 @types/node）" };
        var dialog = new ContentDialog { Title = "Node.js 包管理", Content = Stack(Muted(node.ExecutablePath), manager, project, action, name), PrimaryButtonText = "继续", CloseButtonText = "取消" };
        if (await ShowAsync(dialog) != ContentDialogResult.Primary) return;
        var directory = (project.SelectedItem as ProjectProfile)?.Directory;
        if (action.SelectedIndex == 0) await MessageAsync("npm 包清单", await vm.RunAsync("查询 npm 包", (_, ct) => vm.Packages.ListNodeAsync(node, directory, ct)));
        else await RunPreviewedCommandAsync("Node.js 包操作", vm.Packages.NodePackageCommand(node, manager.SelectedItem!.ToString()!, new[] { "", "install", "upgrade", "uninstall" }[action.SelectedIndex], name.Text.Trim(), directory));
    }
    private async Task AndroidPackagesAsync(ToolInstallation android)
    {
        var action = new ComboBox { Header = "操作", ItemsSource = new[] { "查看已安装和可用组件", "安装组件", "卸载组件", "更新 SDK 组件", "查看并交互接受许可证", "查看模拟器设备", "创建模拟器设备", "启动模拟器设备", "删除模拟器设备" }, SelectedIndex = 0, MinWidth = 440 };
        var package = new TextBox { Header = "组件标识 / 模拟器系统镜像包", PlaceholderText = "platforms;android-35 或 system-images;android-35;google_apis;x86_64" };
        var name = new TextBox { Header = "模拟器名称（设备操作时填写）", PlaceholderText = "DevHub_Pixel" };
        var dialog = new ContentDialog { Title = "Android SDK 与模拟器", Content = Stack(Muted(android.HomePath), action, package, name), PrimaryButtonText = "继续", CloseButtonText = "取消" };
        if (await ShowAsync(dialog) != ContentDialogResult.Primary) return;
        var env = vm.Paths.CacheEnvironment(); var java = vm.Installations.FirstOrDefault(i => i.ToolId == "java" && i.IsJdk && i.IsActive) ?? vm.Installations.FirstOrDefault(i => i.ToolId == "java" && i.IsJdk);
        if (java is not null) { env["JAVA_HOME"] = java.HomePath; env["PATH"] = Path.GetDirectoryName(java.ExecutablePath) + ";" + Environment.GetEnvironmentVariable("PATH"); }
        if (action.SelectedIndex == 4) { vm.Packages.OpenAndroidLicenses(android, java); return; }
        CommandSpec command;
        if (action.SelectedIndex < 4)
            command = vm.Packages.AndroidCommand(android, new[] { "list", "install", "remove", "update" }[action.SelectedIndex], package.Text.Trim()) with { Environment = env };
        else
        {
            if (!System.Text.RegularExpressions.Regex.IsMatch(name.Text, @"^[A-Za-z0-9_-]+$") && action.SelectedIndex != 5) throw new ArgumentException("模拟器名称仅使用字母、数字、下划线和短横线。");
            var avdmanager = Path.Combine(Path.GetDirectoryName(android.ExecutablePath)!, "avdmanager.bat");
            command = action.SelectedIndex switch
            {
                5 => new(avdmanager, ["list", "avd"], vm.Paths.Root, env),
                6 => new(avdmanager, ["create", "avd", "--name", name.Text, "--package", package.Text.Trim(), "--device", "pixel"], vm.Paths.Root, env, 180),
                7 => new(Path.Combine(android.HomePath, "emulator", "emulator.exe"), ["-avd", name.Text], vm.Paths.Root, env, Interactive: true),
                _ => new(avdmanager, ["delete", "avd", "--name", name.Text], vm.Paths.Root, env)
            };
        }
        if (action.SelectedIndex is 0 or 5) await MessageAsync("Android SDK 清单", await vm.ExecuteAsync("查询 Android SDK", command));
        else await RunPreviewedCommandAsync(action.SelectedItem!.ToString()!, command);
    }
}
