using System.Text.Json;

namespace DevHub.Core;

public sealed class HubPaths
{
    public string Root { get; }
    public string State => Path.Combine(Root, ".devhub");
    public string Cache => Path.Combine(State, "cache");
    public string Sdks => Path.Combine(State, "sdks");
    public string Data => Path.Combine(State, "data");
    public string Logs => Path.Combine(State, "logs");
    public string Backups => Path.Combine(State, "backups");
    public HubPaths(string root)
    {
        Root = Path.GetFullPath(root);
        foreach (var dir in new[] { Cache, Sdks, Data, Logs, Backups }) Directory.CreateDirectory(dir);
    }
    public static HubPaths Discover()
    {
        var configured = Environment.GetEnvironmentVariable("DEVHUB_ROOT");
        if (!string.IsNullOrWhiteSpace(configured)) return new(configured);
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "DevHub.root"))) return new(d.FullName);
        return new(AppContext.BaseDirectory);
    }
    public Dictionary<string, string> CacheEnvironment()
    {
        var env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["DEVHUB_ROOT"] = Root,
            ["PIP_CACHE_DIR"] = Path.Combine(Cache, "pip"),
            ["UV_CACHE_DIR"] = Path.Combine(Cache, "uv"),
            ["UV_PYTHON_INSTALL_DIR"] = Path.Combine(Sdks, "python"),
            ["UV_PYTHON_BIN_DIR"] = Path.Combine(Data, "python-bin"),
            ["UV_PYTHON_DOWNLOADS"] = "never",
            ["npm_config_cache"] = Path.Combine(Cache, "npm"),
            ["PNPM_HOME"] = Path.Combine(Data, "pnpm"),
            ["npm_config_store_dir"] = Path.Combine(Cache, "pnpm-store"),
            ["YARN_CACHE_FOLDER"] = Path.Combine(Cache, "yarn"),
            ["PUB_CACHE"] = Path.Combine(Cache, "pub"),
            ["NUGET_PACKAGES"] = Path.Combine(Cache, "nuget", "packages"),
            ["NUGET_HTTP_CACHE_PATH"] = Path.Combine(Cache, "nuget", "http"),
            ["NUGET_SCRATCH"] = Path.Combine(Cache, "nuget", "scratch"),
            ["NUGET_PLUGINS_CACHE_PATH"] = Path.Combine(Cache, "nuget", "plugins"),
            ["DOTNET_CLI_HOME"] = Path.Combine(Data, "dotnet-home"),
            ["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1",
            ["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "1",
            ["GRADLE_USER_HOME"] = Path.Combine(Data, "gradle"),
            ["GOCACHE"] = Path.Combine(Cache, "go-build"),
            ["GOMODCACHE"] = Path.Combine(Cache, "go-mod"),
            ["GOPATH"] = Path.Combine(Data, "go"),
            ["CARGO_HOME"] = Path.Combine(Data, "cargo"),
            ["ANDROID_USER_HOME"] = Path.Combine(Data, "android"),
            ["ANDROID_AVD_HOME"] = Path.Combine(Data, "android-avd"),
            ["TEMP"] = Path.Combine(Cache, "tmp"),
            ["TMP"] = Path.Combine(Cache, "tmp")
        };
        foreach (var value in env.Where(p => p.Key is not "DEVHUB_ROOT" and not "UV_PYTHON_DOWNLOADS" and not "DOTNET_CLI_TELEMETRY_OPTOUT" and not "DOTNET_SKIP_FIRST_TIME_EXPERIENCE").Select(p => p.Value))
            Directory.CreateDirectory(value);
        return env;
    }
    public static bool IsWithin(string path, string parent) =>
        Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar).StartsWith(
            Path.GetFullPath(parent).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase);

    public static void RequireSafeChild(string path, string parent)
    {
        if (!IsWithin(path, parent)) throw new InvalidOperationException("操作路径超出允许目录。");
        var current = new DirectoryInfo(Path.GetFullPath(path));
        var stop = Path.GetFullPath(parent).TrimEnd(Path.DirectorySeparatorChar);
        for (; current is not null; current = current.Parent)
        {
            if (current.Exists && (current.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("该路径含链接目录，不能直接清理。");
            if (current.FullName.TrimEnd(Path.DirectorySeparatorChar).Equals(stop, StringComparison.OrdinalIgnoreCase)) break;
        }
    }
}

public sealed class JsonStore(HubPaths paths)
{
    public async Task<T> LoadRecoveringAsync<T>(string name, Func<T> fallback, ICollection<string> warnings)
    {
        try { return await LoadAsync(name, fallback); }
        catch (InvalidDataException)
        {
            var file = Path.Combine(paths.Data, name + ".json");
            var backup = Path.Combine(paths.Backups, $"corrupt-{name}-{DateTime.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid():N}.json");
            File.Copy(file, backup);
            warnings.Add($"{name} 配置损坏，已使用默认值。原文件备份：{backup}");
            return fallback();
        }
    }
    public async Task<T> LoadAsync<T>(string name, Func<T> fallback)
    {
        var file = Path.Combine(paths.Data, name + ".json");
        if (!File.Exists(file)) return fallback();
        try { return JsonSerializer.Deserialize<T>(await File.ReadAllTextAsync(file), JsonDefaults.Options) ?? fallback(); }
        catch (JsonException e) { throw new InvalidDataException($"配置文件损坏，已保留原文件：{file}", e); }
    }
    public async Task SaveAsync<T>(string name, T value)
    {
        var file = Path.Combine(paths.Data, name + ".json");
        var temp = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
        await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(value, JsonDefaults.Options));
        File.Move(temp, file, true);
    }
}
