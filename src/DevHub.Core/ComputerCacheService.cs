namespace DevHub.Core;

public sealed partial class CacheService
{
    public IReadOnlyList<string> LastDiscoveredProjects { get; private set; } = [];
    public List<string> DefaultProjectRoots()
    {
        var user = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return new[] { Path.GetDirectoryName(cachePaths.Root), Path.Combine(user, "Projects"), Path.Combine(user, "source", "repos"), Path.Combine(user, "Documents"), Path.Combine(user, "Desktop") }
            .Where(p => !string.IsNullOrWhiteSpace(p) && Directory.Exists(p)).Cast<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    public async Task<List<CacheEntry>> InspectComputerAsync(HubSettings settings, IReadOnlyList<ProjectProfile> projects, IReadOnlyList<ToolInstallation> installations,
        Action<string>? progress = null, CancellationToken ct = default)
    {
        var user = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var local = UserDirectories.LocalAppData;
        var roaming = UserDirectories.RoamingAppData;
        var candidates = new Dictionary<string, CacheEntry>(StringComparer.OrdinalIgnoreCase);
        void Add(string? path, string name, string owner, string category = "开发工具缓存", bool clean = true, string? explanation = null, int minimumAgeDays = 0, string? cleanupTool = null)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            path = Environment.ExpandEnvironmentVariables(path);
            if (!Path.IsPathRooted(path) || !Directory.Exists(path)) return;
            path = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
            if (path.Length < 5 || HubPaths.IsWithin(path, cachePaths.State) && category != "DevHub 自身") return;
            var detail = explanation ?? (clean ? "可重新生成或下载；清理前请关闭正在使用它的构建任务。" : "包含工具、项目依赖或本地内容，提供占用查看与目录入口。 ");
            candidates.TryAdd(path, new(name, path, 0, detail, category, owner, clean, clean ? null : detail, minimumAgeDays, CleanupTool: cleanupTool));
        }
        void Known(string path, string name, string owner, bool clean = true, string? explanation = null) => Add(path, name, owner, clean: clean, explanation: explanation);
        void EnvironmentCache(string variable, string name, string owner) => Add(Environment.GetEnvironmentVariable(variable), name, owner);

        Add(Path.Combine(local, "Temp"), "用户临时文件", "Windows 临时文件", "系统与应用占用", explanation: "仅清理超过 7 天且未被占用的文件；保留近期文件、目录链接及正在使用的文件。", minimumAgeDays: 7);
        Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Temp"), "系统临时文件", "Windows 临时文件", "系统与应用占用", false, "系统目录可能正在被安装器使用，请通过 Windows 存储设置清理。");
        Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "SoftwareDistribution", "Download"), "Windows 更新下载", "Windows 更新", "系统与应用占用", false, "请通过 Windows 存储设置处理更新缓存，不直接删除系统更新目录。");
        Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Installer"), "Windows 安装器缓存", "软件维护数据", "系统与应用占用", false, "软件修复、更新和卸载可能需要这些安装包，不能按普通临时文件直接删除。");
        Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Package Cache"), "软件安装与修复包", "软件维护数据", "系统与应用占用", false, "包含运行库和安装器维护数据，建议通过原安装器或系统存储设置管理。");
        var systemDrive = Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows))!;
        foreach (var folder in new[] { "$WINDOWS.~BT", "$Windows.~WS", "Windows.old" })
            Add(Path.Combine(systemDrive, folder), folder + " · Windows 安装 / 升级文件", "Windows 升级", "系统与应用占用", false, "旧版本或升级文件应由 Windows 存储设置识别和清理，可能关系到更新或回退。");
        foreach (var folder in new[] { "Temp", "AMD", "NVIDIA", "Autodesk" })
            Add(Path.Combine(systemDrive, folder), folder + " · 需检查的临时 / 安装目录", "磁盘根目录", "系统与应用占用", false, "目录名称不能证明里面都是缓存，请打开查看用途后再决定如何处理。");
        Add(Path.Combine(local, "D3DSCache"), "DirectX 着色器缓存", "图形缓存", "系统与应用占用");
        Add(Path.Combine(local, "NVIDIA", "DXCache"), "NVIDIA DirectX 缓存", "图形缓存", "系统与应用占用");
        Add(Path.Combine(local, "NVIDIA", "GLCache"), "NVIDIA OpenGL 缓存", "图形缓存", "系统与应用占用");
        Add(Path.Combine(user, ".cache", "codex-runtimes"), "应用内置开发运行环境", "应用运行环境", "系统与应用占用", false, "属于应用自身运行所需的工具，需由所属应用维护。");
        foreach (var browser in new[] { Path.Combine(local, "Microsoft", "Edge", "User Data"), Path.Combine(local, "Google", "Chrome", "User Data") }.Where(Directory.Exists))
            foreach (var profile in Directory.EnumerateDirectories(browser).Where(d => Path.GetFileName(d) == "Default" || Path.GetFileName(d).StartsWith("Profile ")))
                foreach (var cacheFolder in new[] { "Cache", "Code Cache", "GPUCache" })
                    Add(Path.Combine(profile, cacheFolder), Path.GetFileName(profile) + " · " + cacheFolder, browser.Contains("Edge") ? "Microsoft Edge" : "Google Chrome", "系统与应用占用", false, "浏览器运行时可能占用这些文件，建议使用浏览器内置清理功能。");

        EnvironmentCache("PIP_CACHE_DIR", "pip 下载与构建缓存", "Python / pip");
        Known(Path.Combine(local, "pip", "Cache"), "pip 下载与构建缓存", "Python / pip");
        Add(Environment.GetEnvironmentVariable("UV_CACHE_DIR"), "uv 下载与构建缓存", "Python / uv", explanation: "通过 uv cache prune 清理未使用的缓存，保留工具所需的缓存结构。可回收量由 uv 决定。", cleanupTool: "uv-prune");
        Add(Path.Combine(local, "uv", "cache"), "uv 下载与构建缓存", "Python / uv", explanation: "通过 uv cache prune 清理未使用的缓存，保留工具所需的缓存结构。可回收量由 uv 决定。", cleanupTool: "uv-prune");
        EnvironmentCache("npm_config_cache", "npm 包缓存", "Node.js / npm");
        Known(Path.Combine(local, "npm-cache"), "npm 包缓存", "Node.js / npm");
        Known(Path.Combine(roaming, "npm-cache"), "npm 旧版包缓存", "Node.js / npm");
        Known(Path.Combine(local, "pnpm", "store"), "pnpm 共享包存储", "Node.js / pnpm", false, "共享包存储可能被项目通过硬链接使用；请使用 pnpm store prune 清理未使用的包。");
        Known(Path.Combine(local, "pnpm-cache"), "pnpm 元数据缓存", "Node.js / pnpm");
        EnvironmentCache("YARN_CACHE_FOLDER", "Yarn 包缓存", "Node.js / Yarn");
        Known(Path.Combine(local, "Yarn", "Cache"), "Yarn 包缓存", "Node.js / Yarn");
        Known(Path.Combine(local, "node-gyp", "Cache"), "Node.js 原生编译头文件缓存", "Node.js / node-gyp");
        Known(Path.Combine(user, ".nuget", "packages"), "NuGet 全局包缓存", ".NET / NuGet");
        EnvironmentCache("NUGET_PACKAGES", "NuGet 全局包缓存", ".NET / NuGet");
        Known(Path.Combine(local, "NuGet", "v3-cache"), "NuGet 下载元数据", ".NET / NuGet");
        EnvironmentCache("NUGET_HTTP_CACHE_PATH", "NuGet 下载元数据", ".NET / NuGet");
        Known(Path.Combine(local, "NuGet", "plugins-cache"), "NuGet 插件缓存", ".NET / NuGet");
        var gradle = Environment.GetEnvironmentVariable("GRADLE_USER_HOME") ?? Path.Combine(user, ".gradle");
        Known(Path.Combine(gradle, "caches"), "Gradle 构建与依赖缓存", "Java / Gradle");
        Known(Path.Combine(gradle, "wrapper", "dists"), "Gradle Wrapper 下载", "Java / Gradle");
        Known(Path.Combine(user, ".m2", "repository"), "Maven 本地依赖仓库", "Java / Maven", false, "可能包含本地安装、尚未发布的构建产物，不能按普通缓存整体删除。请检查后通过 Maven 管理。");
        var goPath = Environment.GetEnvironmentVariable("GOPATH") ?? Path.Combine(user, "go");
        EnvironmentCache("GOCACHE", "Go 编译缓存", "Go"); Known(Path.Combine(local, "go-build"), "Go 编译缓存", "Go");
        EnvironmentCache("GOMODCACHE", "Go 模块缓存", "Go"); Known(Path.Combine(goPath, "pkg", "mod"), "Go 模块缓存", "Go");
        var cargo = Environment.GetEnvironmentVariable("CARGO_HOME") ?? Path.Combine(user, ".cargo");
        Known(Path.Combine(cargo, "registry", "cache"), "Cargo 包下载", "Rust / Cargo");
        Known(Path.Combine(cargo, "registry", "src"), "Cargo 包解压缓存", "Rust / Cargo");
        Known(Path.Combine(cargo, "git", "db"), "Cargo Git 下载缓存", "Rust / Cargo");
        var pub = Environment.GetEnvironmentVariable("PUB_CACHE") ?? Path.Combine(local, "Pub", "Cache");
        Known(Path.Combine(pub, "hosted"), "Dart / Flutter 包缓存", "Flutter / Dart");
        Known(Path.Combine(pub, "git", "cache"), "Dart Git 包下载缓存", "Flutter / Dart");
        Known(Path.Combine(user, ".pub-cache", "hosted"), "Dart 旧版包缓存", "Flutter / Dart");
        Known(Path.Combine(user, ".android", "cache"), "Android 下载缓存", "Android");
        var androidRoots = installations.Where(i => i.ToolId == "android").Select(i => i.HomePath).Append(settings.AndroidSdkRoot).Append(Environment.GetEnvironmentVariable("ANDROID_HOME")).Where(p => !string.IsNullOrWhiteSpace(p)).Cast<string>();
        foreach (var root in androidRoots)
        {
            Known(Path.Combine(root, ".temp"), "Android SDK 安装临时文件", "Android");
            Known(Path.Combine(root, ".downloadIntermediates"), "Android SDK 下载临时文件", "Android");
        }
        foreach (var flutter in installations.Where(i => i.ToolId == "flutter"))
            Known(Path.Combine(flutter.HomePath, "bin", "cache"), "Flutter SDK 运行组件", "Flutter", false, "包含 Dart SDK 和 Flutter 引擎，属于 SDK 的运行组件。请通过 Flutter 管理，不能作为普通缓存清空。");
        Known(Path.Combine(user, "scoop", "cache"), "Scoop 安装包缓存", "Scoop");
        Known(Path.Combine(local, "huggingface", "hub"), "Hugging Face 模型存储", "AI / Hugging Face", false, "包含模型文件及快照链接；请使用 Hugging Face 的模型缓存管理功能。");
        Known(Path.Combine(user, ".cache", "huggingface", "hub"), "Hugging Face 模型存储", "AI / Hugging Face", false, "包含模型文件及快照链接；请使用 Hugging Face 的模型缓存管理功能。");
        Known(Path.Combine(user, ".cache", "torch"), "PyTorch 模型与扩展缓存", "AI / PyTorch", false, "可能包含下载模型和自定义编译扩展，请检查后按需清理。");

        var queries = new[]
        {
            (Id: "python", Args: new[] { "-m", "pip", "cache", "dir" }, Name: "pip 当前配置缓存", Owner: "Python / pip"),
            (Id: "uv", Args: new[] { "cache", "dir" }, Name: "uv 当前配置缓存", Owner: "Python / uv"),
            (Id: "npm", Args: new[] { "config", "get", "cache" }, Name: "npm 当前配置缓存", Owner: "Node.js / npm"),
            (Id: "pnpm", Args: new[] { "store", "path" }, Name: "pnpm 当前共享包存储", Owner: "Node.js / pnpm"),
            (Id: "go", Args: new[] { "env", "GOCACHE" }, Name: "Go 当前编译缓存", Owner: "Go"),
            (Id: "go", Args: new[] { "env", "GOMODCACHE" }, Name: "Go 当前模块缓存", Owner: "Go")
        };
        var locationQueries = await Task.WhenAll(queries.Select(async query =>
        {
            var tool = installations.FirstOrDefault(i => i.ToolId == query.Id && i.IsActive && i.Owner != "应用运行环境");
            if (tool is null || tool.Version == "应用执行别名") return (Query: query, Location: (string?)null);
            try
            {
                progress?.Invoke("读取 " + query.Owner + " 的缓存位置…");
                var result = await new ProcessRunner(cachePaths).RunAsync(new(tool.ExecutablePath, query.Args, cachePaths.Root,
                    new Dictionary<string, string> { ["GOTOOLCHAIN"] = "local" }, 8, PreserveHostEnvironment: true), cancellation: ct);
                var location = result.Success ? result.Output.Split('\n', StringSplitOptions.TrimEntries).LastOrDefault(Directory.Exists) : null;
                return (Query: query, Location: location);
            }
            catch (Exception e) when (e is IOException or System.ComponentModel.Win32Exception) { return (Query: query, Location: (string?)null); }
        }));
        foreach (var result in locationQueries)
            Add(result.Location, result.Query.Name, result.Query.Owner, clean: result.Query.Id != "pnpm", explanation: result.Query.Id == "pnpm" ? "共享包存储请使用 pnpm store prune 清理未使用包。" : result.Query.Id == "uv" ? "通过 uv cache prune 清理未使用的缓存。" : null, cleanupTool: result.Query.Id == "uv" ? "uv-prune" : null);

        progress?.Invoke("正在发现项目目录与构建缓存…");
        var roots = DefaultProjectRoots().Concat(settings.CacheProjectRoots).Concat(projects.Select(p => p.Directory));
        var discovered = await Task.Run(() => DiscoverProjects(roots, ct, progress), ct);
        LastDiscoveredProjects = discovered;
        foreach (var project in discovered)
        {
            if (DiscoveryService.SamePath(project, cachePaths.Root)) continue;
            var owner = Path.GetFileName(project);
            void ProjectCache(string relative, string name, bool clean = true, string? note = null) => Add(Path.Combine(project, relative.Replace('/', Path.DirectorySeparatorChar)), name, owner, "项目缓存与依赖", clean, note);
            foreach (var folder in new[] { ".pytest_cache", ".mypy_cache", ".ruff_cache", "__pycache__", "node_modules/.cache", ".next/cache", ".angular/cache", ".parcel-cache", ".vite", ".turbo" })
                ProjectCache(folder, folder + " 缓存");
            ProjectCache("node_modules", "Node.js 项目依赖", false, "项目已安装依赖，部分命令和构建直接使用它们。请在项目中使用包管理器重装或清理。");
            ProjectCache(".venv", "Python 虚拟环境", false, "这是项目解释器与已安装的包，属于项目环境。请通过项目环境管理。");
            if (File.Exists(Path.Combine(project, "pom.xml"))) ProjectCache("target", "Maven 构建输出");
            if (File.Exists(Path.Combine(project, "Cargo.toml"))) ProjectCache("target", "Rust 构建输出");
            if (File.Exists(Path.Combine(project, "build.gradle")) || File.Exists(Path.Combine(project, "build.gradle.kts")))
            { ProjectCache(".gradle", "项目 Gradle 缓存"); ProjectCache("build", "Gradle 构建输出"); }
            if (File.Exists(Path.Combine(project, "pubspec.yaml")))
            { ProjectCache(".dart_tool", "Dart 项目工具缓存"); ProjectCache("build", "Flutter 构建输出"); ProjectCache("android/.gradle", "Android 项目 Gradle 缓存"); ProjectCache("android/app/build", "Android 构建输出"); }
            if (HasFile(project, "*.csproj") || HasFile(project, "*.fsproj"))
            { ProjectCache("bin", ".NET 编译输出"); ProjectCache("obj", ".NET 构建中间文件"); }
        }
        foreach (var directory in Directory.GetDirectories(cachePaths.Cache))
            Add(directory, CacheName(Path.GetFileName(directory)), "DevHub", "DevHub 自身", cleanupTool: Path.GetFileName(directory) == "uv" ? "uv-prune" : null);
        foreach (var directory in new[] { Path.Combine(cachePaths.Data, "gradle", "caches"), Path.Combine(cachePaths.Data, "cargo", "registry", "cache") })
            Add(directory, Path.GetFileName(Path.GetDirectoryName(directory)) + " 缓存", "DevHub", "DevHub 自身");
        Add(Path.Combine(cachePaths.Data, "removed-sdks"), "已移除 SDK 的待清理文件", "DevHub", "DevHub 自身", explanation: "此前已解除绑定并移除的 SDK。清理此项将永久删除这些文件。");
        // Dependency directories are shown once, including their nested caches in the measured total.
        var values = candidates.Values.Where(e => !candidates.Values.Any(p => p.Category == e.Category && !p.CanClean && !DiscoveryService.SamePath(p.Directory, e.Directory) && HubPaths.IsWithin(e.Directory, p.Directory))).ToList();
        using var gate = new SemaphoreSlim(Math.Clamp(settings.ProbeConcurrency, 1, 4));
        int done = 0;
        var measured = await Task.WhenAll(values.Select(async entry =>
        {
            await gate.WaitAsync(ct);
            try
            {
                progress?.Invoke($"统计 {Interlocked.Increment(ref done)}/{values.Count} · {entry.Owner} · {entry.Name}");
                var bytes = await Task.Run(() => Measure(entry.Directory, ct), ct);
                var reclaimable = entry.CleanupTool is not null ? 0 : entry.MinimumAgeDays > 0 ? await Task.Run(() => MeasureOldFiles(entry.Directory, DateTime.UtcNow.AddDays(-entry.MinimumAgeDays), ct), ct) : entry.CanClean ? bytes : 0;
                return entry with { Bytes = bytes, ReclaimableBytes = reclaimable };
            }
            finally { gate.Release(); }
        }));
        approvedPaths.Clear(); foreach (var entry in measured) approvedPaths[entry.Directory] = entry;
        progress?.Invoke($"已检查 {discovered.Count} 个项目，发现 {measured.Length} 项缓存与依赖占用。");
        return measured.OrderByDescending(e => e.Bytes).ToList();
    }

    public static List<string> DiscoverProjects(IEnumerable<string> roots, CancellationToken ct = default, Action<string>? progress = null)
    {
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase); var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<(string Directory, int Depth)>();
        foreach (var root in roots.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase)) queue.Enqueue((Path.GetFullPath(root), 0));
        var skip = new HashSet<string>(new[] { ".git", ".devhub", "node_modules", ".venv", "venv", "bin", "obj", "target", "build", "dist", ".gradle", ".dart_tool", ".next", "vendor", "__pycache__", ".cache", ".nuget", "sdks", "sdk", "system-images" }, StringComparer.OrdinalIgnoreCase);
        while (queue.TryDequeue(out var item) && seen.Count < 30000)
        {
            ct.ThrowIfCancellationRequested();
            if (!seen.Add(item.Directory)) continue;
            try
            {
                if ((File.GetAttributes(item.Directory) & FileAttributes.ReparsePoint) != 0) continue;
                if (seen.Count % 250 == 0) progress?.Invoke($"发现项目 · 已检查 {seen.Count} 个目录");
                if (IsProject(item.Directory)) found.Add(item.Directory);
                if (item.Depth >= 4) continue;
                foreach (var dir in Directory.EnumerateDirectories(item.Directory).Take(500))
                    if (!skip.Contains(Path.GetFileName(dir))) queue.Enqueue((dir, item.Depth + 1));
            }
            catch (Exception e) when (e is UnauthorizedAccessException or IOException) { }
        }
        return found.OrderBy(p => p).ToList();
    }
    private static bool IsProject(string path) => new[] { "package.json", "pom.xml", "build.gradle", "build.gradle.kts", "pubspec.yaml", "pyproject.toml", "requirements.txt", "Cargo.toml", "go.mod", "CMakeLists.txt" }.Any(f => File.Exists(Path.Combine(path, f))) || Directory.Exists(Path.Combine(path, ".git")) || File.Exists(Path.Combine(path, ".git")) || HasFile(path, "*.csproj") || HasFile(path, "*.sln") || HasFile(path, "*.slnx");
    private static bool HasFile(string path, string glob)
    {
        try { return Directory.EnumerateFiles(path, glob, SearchOption.TopDirectoryOnly).Any(); }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException) { return false; }
    }
}
