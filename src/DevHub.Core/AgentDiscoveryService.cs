using System.Diagnostics;
using System.Text.Json;
using Tomlyn;
using Tomlyn.Model;

namespace DevHub.Core;

public sealed record AgentClient(string Id, string Name, string Kind, List<ToolInstallation> Installations,
    int ProcessCount, int ConfigCount)
{
    public string Status => Installations.Count > 0 ? "已发现安装" : ConfigCount > 0 ? "仅发现配置" : "未发现";
}
public sealed record AiConfigFile(string ClientId, string ClientName, string Scope, string Path, string Status);
public sealed record AgentDefinitionFile(string ClientId, string ClientName, string Scope, string Name, string Path, string Kind);
public sealed record McpConfiguration(string ClientId, string ClientName, string Scope, string Name, string ConfigPath,
    bool Enabled, string Transport, string Launcher, string? ResolvedExecutable, string Endpoint,
    string[] EnvironmentNames, string DependencyStatus);
public sealed record AgentSnapshot(DateTimeOffset ScannedAt, List<AgentClient> Clients, List<AiConfigFile> Configurations,
    List<AgentDefinitionFile> Definitions, List<McpConfiguration> McpServers, int ProjectCount);

// This inventory never starts an Agent or MCP server, and never stores secret-bearing values or prompts.
public sealed class AgentDiscoveryService
{
    private readonly string home, local, roaming;
    private readonly IReadOnlyList<string> searchDirectories;
    private static readonly JsonDocumentOptions JsonOptions = new() { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip, MaxDepth = 64 };
    private static readonly (string Id, string Name, string Kind, string Process)[] ClientCatalog =
    [
        ("codex", "Codex CLI", "命令行 Agent", "codex"), ("claude-code", "Claude Code", "命令行 Agent", "claude"),
        ("gemini-cli", "Gemini CLI", "命令行 Agent", "gemini"), ("opencode", "OpenCode", "命令行 Agent", "opencode"),
        ("aider", "Aider", "命令行 Agent", "aider"), ("goose", "Goose", "命令行 Agent", "goose"),
        ("copilot-cli", "GitHub Copilot CLI", "命令行 Agent", "copilot"), ("cursor", "Cursor", "AI 编辑器", "Cursor"),
        ("vscode", "Visual Studio Code", "编辑器 / AI 扩展宿主", "Code"), ("claude-desktop", "Claude Desktop", "AI 客户端", "Claude")
    ];

    public AgentDiscoveryService(string? home = null, string? local = null, string? roaming = null, IReadOnlyList<string>? searchDirectories = null)
    {
        this.home = home ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        this.local = local ?? UserDirectories.LocalAppData; this.roaming = roaming ?? UserDirectories.RoamingAppData;
        this.searchDirectories = searchDirectories ?? DiscoveryService.EnvironmentPaths();
    }

    public async Task<AgentSnapshot> ScanAsync(IEnumerable<ToolInstallation> installations, IEnumerable<string> projectDirectories,
        Action<string>? progress = null, CancellationToken ct = default, bool inspectProcesses = true)
    {
        var configs = new List<AiConfigFile>(); var definitions = new List<AgentDefinitionFile>(); var servers = new List<McpConfiguration>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var projects = projectDirectories.Where(Directory.Exists).Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var inventory = installations.Where(i => ClientCatalog.Any(c => c.Id == i.ToolId) && File.Exists(i.ExecutablePath)).ToList();
        foreach (var definition in DiscoveryService.BuiltInCatalog().Tools.Where(d => d.Category == "AI 工具"))
            foreach (var directory in searchDirectories)
                foreach (var entry in definition.Executables)
                {
                    ct.ThrowIfCancellationRequested();
                    var executable = Path.Combine(directory, entry);
                    if (!File.Exists(executable) || inventory.Any(i => DiscoveryService.SamePath(i.ExecutablePath, executable))) continue;
                    if (File.Exists(Path.Combine(directory, "resources", "app.asar")) || File.Exists(Path.Combine(directory, "resources", "app", "package.json"))) continue;
                    inventory.Add(new() { Id = DiscoveryService.StableId(definition.Id + executable), ToolId = definition.Id, Name = definition.Name, Category = "AI 工具", ExecutablePath = executable, HomePath = directory, RealPath = executable, Version = "版本待工具扫描", Source = "PATH 入口" });
                }
        AddDesktop("cursor", Path.Combine(local, "Programs", "cursor", "Cursor.exe"));
        AddDesktop("vscode", Path.Combine(local, "Programs", "Microsoft VS Code", "Code.exe"));
        AddDesktop("vscode", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Microsoft VS Code", "Code.exe"));
        AddDesktop("cursor", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Cursor", "Cursor.exe"));
        AddDesktop("claude-desktop", Path.Combine(local, "AnthropicClaude", "claude.exe"));
        AddDesktop("claude-desktop", Path.Combine(local, "Programs", "Claude", "Claude.exe"));
        foreach (var client in ClientCatalog)
        {
            ct.ThrowIfCancellationRequested(); progress?.Invoke("检查 AI 配置 · " + client.Name);
            foreach (var source in ConfigSources(client.Id)) await ReadConfig(client.Id, client.Name, "全局", source.Path, source.Format, home);
            foreach (var project in projects)
            {
                var scope = "项目 · " + project;
                foreach (var source in ProjectSources(client.Id, project)) await ReadConfig(client.Id, client.Name, scope, source.Path, source.Format, project);
            }
        }
        // Shared conventions are listed once rather than attributed to every client that might read them.
        foreach (var root in new[] { home }.Concat(projects))
        {
            ct.ThrowIfCancellationRequested(); var scope = root == home ? "全局" : "项目 · " + root;
            foreach (var (id, name, folder) in new[] { ("claude-code", "Claude Code", ".claude/agents"), ("opencode", "OpenCode", root == home ? ".config/opencode/agents" : ".opencode/agents"), ("copilot-cli", "GitHub Copilot", ".github/agents") })
                AddDefinitions(id, name, scope, Path.Combine(root, folder), "*.md", "子 Agent");
            foreach (var folder in new[] { ".agents/skills", ".claude/skills", ".codex/skills", ".cursor/skills", ".gemini/skills" })
                AddDefinitions("shared", "Skills", scope, Path.Combine(root, folder), "SKILL.md", "Skill");
            foreach (var rule in new[] { "AGENTS.md", "CLAUDE.md", "GEMINI.md", ".github/copilot-instructions.md", ".cursorrules" })
                AddDefinition("shared", "项目规则", scope, Path.Combine(root, rule), "规则文件");
            AddDefinitions("cursor", "Cursor", scope, Path.Combine(root, ".cursor", "rules"), "*.mdc", "规则文件");
        }
        var processes = inspectProcesses ? CountProcesses() : new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var clients = ClientCatalog.Select(c => new AgentClient(c.Id, c.Name, c.Kind,
            inventory.Where(i => i.ToolId == c.Id).DistinctBy(i => i.ExecutablePath, StringComparer.OrdinalIgnoreCase).ToList(),
            processes.GetValueOrDefault(c.Process), configs.Count(f => f.ClientId == c.Id) + definitions.Count(d => d.ClientId == c.Id))).ToList();
        progress?.Invoke($"发现 {clients.Count(c => c.Installations.Count > 0)} 种 AI 工具、{servers.Count} 条 MCP 配置、{definitions.Count} 项 Agent / Skill / 规则。");
        return new(DateTimeOffset.Now, clients, configs, definitions, servers, projects.Count);

        void AddDesktop(string id, string executable)
        {
            if (!File.Exists(executable)) return;
            var client = ClientCatalog.First(c => c.Id == id); var version = "未读取";
            try { version = FileVersionInfo.GetVersionInfo(executable).ProductVersion ?? version; } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            inventory.Add(new() { Id = DiscoveryService.StableId(id + executable), ToolId = id, Name = client.Name, Category = "AI 工具", ExecutablePath = executable, HomePath = Path.GetDirectoryName(executable)!, RealPath = executable, Version = version, Source = "常见安装目录" });
        }
        async Task ReadConfig(string id, string client, string scope, string file, string format, string baseDirectory)
        {
            if (!File.Exists(file) || !seen.Add(id + "|" + file)) return;
            ct.ThrowIfCancellationRequested();
            try
            {
                if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0) { configs.Add(new(id, client, scope, file, "链接文件，未读取")); return; }
                if (new FileInfo(file).Length > 4 * 1024 * 1024) { configs.Add(new(id, client, scope, file, "超过 4 MB，仅登记位置")); return; }
                if (format == "location") { configs.Add(new(id, client, scope, file, "已发现配置位置")); return; }
                var text = await File.ReadAllTextAsync(file, ct);
                using var doc = format == "toml" ? JsonDocument.Parse(JsonSerializer.Serialize(TomlSerializer.Deserialize<TomlTable>(text)), JsonOptions) : JsonDocument.Parse(text, JsonOptions);
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object) { configs.Add(new(id, client, scope, file, "配置根节点不是对象")); return; }
                configs.Add(new(id, client, scope, file, "已解析 · 配置记录，不代表已生效"));
                var key = format == "toml" ? "mcp_servers" : id == "opencode" ? "mcp" : id == "vscode" ? "servers" : "mcpServers";
                ReadServers(root, key, scope, baseDirectory);
                if (id == "claude-code" && root.TryGetProperty("projects", out var projectConfigs) && projectConfigs.ValueKind == JsonValueKind.Object)
                    foreach (var project in projectConfigs.EnumerateObject())
                        ReadServers(project.Value, "mcpServers", "项目 · " + project.Name, project.Name);
                if (root.TryGetProperty(id == "codex" ? "agents" : "agent", out var agents) && agents.ValueKind == JsonValueKind.Object)
                    foreach (var agent in agents.EnumerateObject().Where(a => a.Value.ValueKind == JsonValueKind.Object))
                        definitions.Add(new(id, client, scope, agent.Name, file, "子 Agent · 配置内定义"));

                void ReadServers(JsonElement parent, string property, string serverScope, string workingDirectory)
                {
                    if (parent.ValueKind != JsonValueKind.Object || !parent.TryGetProperty(property, out var map) || map.ValueKind != JsonValueKind.Object) return;
                    foreach (var item in map.EnumerateObject())
                    {
                        var server = item.Value;
                        if (server.ValueKind != JsonValueKind.Object) { servers.Add(new(id, client, serverScope, item.Name, file, true, "未知", "", null, "", [], "配置项不是对象")); continue; }
                        var enabled = !Bool(server, "disabled", false) && Bool(server, "enabled", true);
                        var command = String(server, "command");
                        if (server.TryGetProperty("command", out var commandArray) && commandArray.ValueKind == JsonValueKind.Array)
                            command = commandArray.EnumerateArray().FirstOrDefault().ValueKind == JsonValueKind.String ? commandArray[0].GetString() ?? "" : "";
                        var url = String(server, "url"); if (url.Length == 0) url = String(server, "httpUrl");
                        var transport = String(server, "type"); if (transport.Length == 0) transport = url.Length > 0 ? "HTTP / SSE" : "stdio";
                        var remote = url.Length > 0 || transport is "remote" or "http" or "sse" or "streamable-http" or "ws";
                        var resolved = remote ? null : ResolveLauncher(command, workingDirectory);
                        var dependency = !enabled ? "配置中已停用" : remote ? "远程配置 · 未测试连接 / 授权" : resolved is not null ? "本机找到候选启动入口 · 未测试客户端环境和连接" : "启动入口未找到或需客户端展开变量";
                        if (!remote && String(server, "cwd") is { Length: > 0 } cwd && Path.IsPathFullyQualified(cwd) && !Directory.Exists(cwd)) dependency = "配置中的工作目录不存在";
                        // Retain only environment variable names. Arguments, headers, URLs with secrets and values never leave this parser.
                        var environmentNames = new HashSet<string>(StringComparer.Ordinal);
                        foreach (var envKey in new[] { "env", "environment" })
                            if (server.TryGetProperty(envKey, out var env) && env.ValueKind == JsonValueKind.Object)
                                foreach (var variable in env.EnumerateObject()) environmentNames.Add(variable.Name);
                        if (server.TryGetProperty("env_vars", out var forwarded) && forwarded.ValueKind == JsonValueKind.Array)
                            foreach (var variable in forwarded.EnumerateArray()) if (variable.ValueKind == JsonValueKind.String) environmentNames.Add(variable.GetString()!);
                        var launcher = remote ? "" : resolved is not null ? Path.GetFileName(resolved) : "待解析启动命令";
                        servers.Add(new(id, client, serverScope, item.Name, file, enabled, transport, launcher, resolved, SafeOrigin(url), environmentNames.Order().ToArray(), dependency));
                    }
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or TomlException or ArgumentException or InvalidOperationException)
            {
                // Parser messages may contain a secret from the source line; expose only the error category.
                configs.Add(new(id, client, scope, file, e is IOException or UnauthorizedAccessException ? "无法读取配置" : "配置格式无法解析"));
            }
        }
        void AddDefinition(string id, string name, string scope, string file, string kind)
        {
            if (!File.Exists(file) || !seen.Add("definition|" + file)) return;
            var title = Path.GetFileName(file).Equals("SKILL.md", StringComparison.OrdinalIgnoreCase) ? Path.GetFileName(Path.GetDirectoryName(file))! : Path.GetFileNameWithoutExtension(file);
            definitions.Add(new(id, name, scope, title, file, kind));
        }
        void AddDefinitions(string id, string name, string scope, string root, string pattern, string kind)
        {
            if (!Directory.Exists(root)) return;
            var queue = new Queue<(string Path, int Depth)>(); queue.Enqueue((root, 0)); var count = 0;
            while (queue.TryDequeue(out var item) && count++ < 1500)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    if ((File.GetAttributes(item.Path) & FileAttributes.ReparsePoint) != 0) continue;
                    foreach (var file in Directory.EnumerateFiles(item.Path, pattern).Take(500)) AddDefinition(id, name, scope, file, kind);
                    if (item.Depth >= 4) continue;
                    foreach (var directory in Directory.EnumerateDirectories(item.Path).Take(500)) queue.Enqueue((directory, item.Depth + 1));
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            }
        }
    }

    private IEnumerable<(string Path, string Format)> ConfigSources(string id)
    {
        return id switch
        {
            "codex" => [(Path.Combine(home, ".codex", "config.toml"), "toml")],
            "claude-code" => [(Path.Combine(home, ".claude.json"), "json"), (Path.Combine(home, ".claude", "settings.json"), "json")],
            "gemini-cli" => [(Path.Combine(home, ".gemini", "settings.json"), "json")],
            "cursor" => [(Path.Combine(home, ".cursor", "mcp.json"), "json")],
            "vscode" => [(Path.Combine(roaming, "Code", "User", "mcp.json"), "json")],
            "opencode" => [(Path.Combine(home, ".config", "opencode", "opencode.json"), "json"), (Path.Combine(home, ".config", "opencode", "opencode.jsonc"), "json")],
            "claude-desktop" => [(Path.Combine(roaming, "Claude", "claude_desktop_config.json"), "json")],
            "aider" => [(Path.Combine(home, ".aider.conf.yml"), "location")],
            "goose" => [(Path.Combine(home, ".config", "goose", "config.yaml"), "location")],
            _ => []
        };
    }
    private static IEnumerable<(string Path, string Format)> ProjectSources(string id, string root) => id switch
    {
        "codex" => [(Path.Combine(root, ".codex", "config.toml"), "toml")],
        "claude-code" => [(Path.Combine(root, ".mcp.json"), "json"), (Path.Combine(root, ".claude", "settings.json"), "json"), (Path.Combine(root, ".claude", "settings.local.json"), "json")],
        "gemini-cli" => [(Path.Combine(root, ".gemini", "settings.json"), "json")],
        "cursor" => [(Path.Combine(root, ".cursor", "mcp.json"), "json")],
        "vscode" => [(Path.Combine(root, ".vscode", "mcp.json"), "json")],
        "opencode" => [(Path.Combine(root, "opencode.json"), "json"), (Path.Combine(root, "opencode.jsonc"), "json")],
        "aider" => [(Path.Combine(root, ".aider.conf.yml"), "location")],
        _ => []
    };
    private string? ResolveLauncher(string command, string workingDirectory)
    {
        if (string.IsNullOrWhiteSpace(command) || command.IndexOfAny(['$', '%', '\r', '\n', '"']) >= 0) return null;
        try
        {
            if (Path.IsPathFullyQualified(command)) return File.Exists(command) ? command : null;
            if (command.Contains('/') || command.Contains('\\')) return null; // Relative paths depend on the actual client's working directory.
            foreach (var directory in searchDirectories)
                foreach (var extension in Path.HasExtension(command) ? new[] { "" } : new[] { ".exe", ".cmd", ".bat", ".ps1", "" })
                {
                    var path = Path.Combine(directory, command + extension); if (File.Exists(path)) return path;
                }
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or IOException) { }
        return null;
    }
    private static string SafeOrigin(string url) => Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" or "ws" or "wss"
        ? uri.Scheme + "://" + uri.IdnHost + (uri.IsDefaultPort ? "" : ":" + uri.Port) + "/…" : url.Length == 0 ? "" : "地址由客户端解析";
    private static string String(JsonElement element, string key) => element.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
    private static bool Bool(JsonElement element, string key, bool fallback) => element.TryGetProperty(key, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.GetBoolean() : fallback;
    private static Dictionary<string, int> CountProcesses()
    {
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var process in Process.GetProcesses())
        {
            using (process)
                try { var name = process.ProcessName; counts[name] = counts.GetValueOrDefault(name) + 1; }
                catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        }
        return counts;
    }
}
