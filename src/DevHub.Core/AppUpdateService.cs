using System.IO.Compression;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DevHub.Core;

public sealed record ReleaseFile(string Path, string Sha256, long Length);
public sealed record ReleaseManifest(int SchemaVersion, string Version, string Runtime, int MinimumWindowsBuild, List<ReleaseFile> Files);
public sealed record UpdateDescriptor(int SchemaVersion, string Version, string Runtime, string AssetName, string Sha256, long Size);
public sealed record AvailableUpdate(UpdateDescriptor Descriptor, string Tag, string Notes, string PageUrl);
public sealed record PreparedUpdate(string StageDirectory, ReleaseManifest Manifest);

public static class AppVersion
{
    public static string Current => StartupLifecycle.Version;
    public static bool IsStable(string value) => Regex.IsMatch(value.TrimStart('v'), @"^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(\+[0-9A-Za-z.-]+)?$");
    public static int Compare(string a, string b)
    {
        static (Version Number, string[] Pre) Parse(string text)
        {
            var parts = text.TrimStart('v').Split('+')[0].Split('-', 2);
            if (!Regex.IsMatch(parts[0], @"^\d+\.\d+\.\d+$") || !Version.TryParse(parts[0], out var number)) throw new InvalidDataException("版本号无效。");
            return (number, parts.Length > 1 ? parts[1].Split('.') : []);
        }
        var left = Parse(a); var right = Parse(b); var comparison = left.Number.CompareTo(right.Number); if (comparison != 0) return comparison;
        if (left.Pre.Length == 0 || right.Pre.Length == 0) return left.Pre.Length == right.Pre.Length ? 0 : left.Pre.Length == 0 ? 1 : -1;
        for (var i = 0; i < Math.Min(left.Pre.Length, right.Pre.Length); i++)
        {
            var ln = long.TryParse(left.Pre[i], out var l); var rn = long.TryParse(right.Pre[i], out var r);
            comparison = ln && rn ? l.CompareTo(r) : ln != rn ? ln ? -1 : 1 : string.CompareOrdinal(left.Pre[i], right.Pre[i]);
            if (comparison != 0) return comparison;
        }
        return left.Pre.Length.CompareTo(right.Pre.Length);
    }
}

public sealed class AppUpdateService : IDisposable
{
    public const string Repository = "Cydiacoft/DevHub";
    public static readonly string[] RequiredFiles = ["DevHub.App.exe", "DevHub.App.dll", "DevHub.App.pri", "DevHub.App.runtimeconfig.json", "DevHub.Core.dll", "Microsoft.UI.Xaml.dll", "coreclr.dll", "hostfxr.dll", "hostpolicy.dll", "Assets/DevHub.ico", "helper/DevHub.Helper.exe", "helper/DevHub.Helper.runtimeconfig.json", "helper/coreclr.dll", "updater/DevHub.Updater.exe", "updater/DevHub.Updater.runtimeconfig.json", "updater/DevHub.Core.dll", "updater/coreclr.dll", "updater/hostfxr.dll", "updater/hostpolicy.dll"];
    private readonly HubPaths paths;
    private readonly HttpClient http;
    public AppUpdateService(HubPaths paths, HttpMessageHandler? handler = null)
    {
        this.paths = paths; http = handler is null ? new HttpClient() : new HttpClient(handler);
        http.Timeout = TimeSpan.FromSeconds(15); http.DefaultRequestHeaders.UserAgent.ParseAdd("DevHub/" + AppVersion.Current);
    }
    public async Task<AvailableUpdate?> CheckAsync(string currentVersion, CancellationToken ct = default)
    {
        using var response = await http.GetAsync("https://api.github.com/repos/" + Repository + "/releases/latest", ct);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests) throw new InvalidOperationException("GitHub 检查更新暂时受限，请稍后重试。");
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct)); var release = json.RootElement;
        if (release.GetProperty("draft").GetBoolean() || release.GetProperty("prerelease").GetBoolean()) return null;
        var tag = release.GetProperty("tag_name").GetString()!; var version = tag.TrimStart('v');
        if (!AppVersion.IsStable(version) || AppVersion.Compare(version, currentVersion) <= 0) return null;
        var fileName = $"DevHub-{version}-win-x64.update.json";
        var expectedUrl = DownloadUrl(tag, fileName);
        var asset = release.GetProperty("assets").EnumerateArray().FirstOrDefault(a => a.GetProperty("name").GetString() == fileName);
        if (asset.ValueKind == JsonValueKind.Undefined) throw new InvalidDataException("新版本没有提供可验证的更新清单，可前往发布页手动下载。");
        if (asset.GetProperty("browser_download_url").GetString() != expectedUrl) throw new InvalidDataException("更新清单来源不匹配。");
        using var descriptorResponse = await http.GetAsync(expectedUrl, ct); descriptorResponse.EnsureSuccessStatusCode();
        var descriptorText = await descriptorResponse.Content.ReadAsStringAsync(ct);
        if (descriptorText.Length > 65536) throw new InvalidDataException("更新清单过大。");
        var descriptor = JsonSerializer.Deserialize<UpdateDescriptor>(descriptorText, JsonDefaults.Options) ?? throw new InvalidDataException("更新清单为空。");
        ValidateDescriptor(descriptor);
        if (descriptor.Version != version) throw new InvalidDataException("更新清单版本不一致。");
        var zip = release.GetProperty("assets").EnumerateArray().FirstOrDefault(a => a.GetProperty("name").GetString() == descriptor.AssetName);
        if (zip.ValueKind == JsonValueKind.Undefined || zip.GetProperty("browser_download_url").GetString() != DownloadUrl(tag, descriptor.AssetName) || zip.GetProperty("size").GetInt64() != descriptor.Size)
            throw new InvalidDataException("更新包与发布清单不一致。");
        return new(descriptor, tag, release.TryGetProperty("body", out var notes) ? notes.GetString() ?? "" : "", "https://github.com/" + Repository + "/releases/tag/" + tag);
    }
    public async Task<PreparedUpdate> DownloadAsync(AvailableUpdate update, IProgress<(double Progress, string Detail)>? progress = null, CancellationToken ct = default)
    {
        ValidateDescriptor(update.Descriptor);
        if (update.Tag != "v" + update.Descriptor.Version && update.Tag != update.Descriptor.Version) throw new InvalidDataException("版本标签不一致。");
        var directory = Path.Combine(paths.Cache, "updates", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        var partial = Path.Combine(directory, "download.partial"); var stage = Path.Combine(directory, "stage");
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromMinutes(20));
            using var response = await http.GetAsync(DownloadUrl(update.Tag, update.Descriptor.AssetName), HttpCompletionOption.ResponseHeadersRead, timeout.Token); response.EnsureSuccessStatusCode();
            await using (var source = await response.Content.ReadAsStreamAsync(timeout.Token))
            await using (var target = File.Create(partial))
            {
                var buffer = new byte[81920]; long total = 0; int read;
                while ((read = await source.ReadAsync(buffer, timeout.Token)) > 0)
                {
                    total += read; if (total > update.Descriptor.Size) throw new InvalidDataException("更新包大小超过清单声明。");
                    await target.WriteAsync(buffer.AsMemory(0, read), timeout.Token);
                    progress?.Report((total * 80.0 / update.Descriptor.Size, $"下载更新 · {total / 1048576d:F1} MB"));
                }
                if (total != update.Descriptor.Size) throw new InvalidDataException("更新包下载不完整。");
            }
            await using (var stream = File.OpenRead(partial))
                if (!Convert.ToHexString(await SHA256.HashDataAsync(stream, ct)).Equals(update.Descriptor.Sha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("更新包 SHA-256 校验失败。");
            var manifest = await StageArchiveAsync(partial, stage, update.Descriptor.Version, ct);
            progress?.Report((100, "更新包已校验，等待重启替换"));
            return new(stage, manifest);
        }
        catch { if (Directory.Exists(stage)) ManagementService.DeleteTreeWithoutLinks(stage, directory); throw; }
        finally { if (File.Exists(partial)) File.Delete(partial); }
    }
    public static async Task<ReleaseManifest> StageArchiveAsync(string archive, string stage, string version, CancellationToken ct = default)
    {
        if (Directory.Exists(stage)) throw new InvalidOperationException("更新暂存目录已存在。");
        Directory.CreateDirectory(stage);
        try
        {
            using var zip = ZipFile.OpenRead(archive); long expanded = 0; var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (zip.Entries.Count > 20000) throw new InvalidDataException("更新包文件过多。");
            foreach (var entry in zip.Entries)
            {
                ct.ThrowIfCancellationRequested();
                if (entry.FullName.EndsWith('/')) { SafeRelativePath(entry.FullName.TrimEnd('/')); continue; }
                SafeRelativePath(entry.FullName);
                if (((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000 || !seen.Add(entry.FullName.Replace('\\', '/'))) throw new InvalidDataException("更新包含链接或重复文件。");
                expanded += entry.Length; if (expanded > 2L * 1024 * 1024 * 1024) throw new InvalidDataException("更新包展开后过大。");
                var target = Path.Combine(stage, entry.FullName); HubPaths.RequireSafeChild(target, stage); Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                await using var input = entry.Open(); await using var output = File.Create(target); await input.CopyToAsync(output, ct);
            }
            var manifest = await ReadAndVerifyManifestAsync(stage, ct);
            if (manifest.Version != version) throw new InvalidDataException("更新包内部版本不一致。");
            return manifest;
        }
        catch { ManagementService.DeleteTreeWithoutLinks(stage, Path.GetDirectoryName(stage)!); throw; }
    }
    public static async Task<ReleaseManifest> ReadAndVerifyManifestAsync(string directory, CancellationToken ct = default)
    {
        var manifestPath = Path.Combine(directory, "release-manifest.json");
        var manifest = JsonSerializer.Deserialize<ReleaseManifest>(await File.ReadAllTextAsync(manifestPath, ct), JsonDefaults.Options) ?? throw new InvalidDataException("发布文件清单为空。");
        if (manifest.SchemaVersion != 1 || manifest.Runtime != "win-x64" || !AppVersion.IsStable(manifest.Version) || manifest.MinimumWindowsBuild < 19041 || manifest.Files.Count is 0 or > 20000) throw new InvalidDataException("发布文件清单无效。");
        if (OperatingSystem.IsWindows() && Environment.OSVersion.Version.Build < manifest.MinimumWindowsBuild) throw new InvalidOperationException("当前 Windows 版本不满足新版要求。");
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in manifest.Files)
        {
            ct.ThrowIfCancellationRequested(); SafeRelativePath(file.Path);
            if (!seen.Add(file.Path.Replace('\\', '/')) || file.Length < 0 || !Regex.IsMatch(file.Sha256, "^[A-Fa-f0-9]{64}$")) throw new InvalidDataException("发布文件条目无效。");
            var full = Path.Combine(directory, file.Path); HubPaths.RequireSafeChild(full, directory);
            if (!File.Exists(full) || new FileInfo(full).Length != file.Length || (File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("发布文件缺失或大小不符：" + file.Path);
            await using var stream = File.OpenRead(full);
            if (!Convert.ToHexString(await SHA256.HashDataAsync(stream, ct)).Equals(file.Sha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("发布文件校验失败：" + file.Path);
        }
        foreach (var required in RequiredFiles)
            if (!seen.Contains(required)) throw new InvalidDataException("发布文件清单缺少必要组件：" + required);
        var listed = Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).Select(p => Path.GetRelativePath(directory, p).Replace('\\', '/')).Where(p => p != "release-manifest.json");
        if (listed.Any(p => !seen.Contains(p))) throw new InvalidDataException("发布包存在清单外文件。");
        return manifest;
    }
    public static void SafeRelativePath(string path)
    {
        var normalized = path.Replace('\\', '/'); var parts = normalized.Split('/');
        if (string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path) || path.IndexOfAny([':', '*', '?', '"', '<', '>', '|']) >= 0 || parts.Any(p => p is "" or "." or ".." || p.EndsWith('.') || p.EndsWith(' ') || p.Any(c => c < 32) || Regex.IsMatch(p, @"^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(\.|$)", RegexOptions.IgnoreCase)) || parts.Any(p => p.Equals(".devhub", StringComparison.OrdinalIgnoreCase)) || normalized.Equals("DevHub.root", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("更新文件路径不安全：" + path);
    }
    private static void ValidateDescriptor(UpdateDescriptor descriptor)
    {
        if (descriptor.SchemaVersion != 1 || !AppVersion.IsStable(descriptor.Version) || descriptor.Runtime != "win-x64" || descriptor.AssetName != $"DevHub-{descriptor.Version}-win-x64.zip" || descriptor.Size is <= 0 or > 1073741824 || !Regex.IsMatch(descriptor.Sha256, "^[a-fA-F0-9]{64}$")) throw new InvalidDataException("更新清单内容无效。");
    }
    private static string DownloadUrl(string tag, string file) => "https://github.com/" + Repository + "/releases/download/" + Uri.EscapeDataString(tag) + "/" + Uri.EscapeDataString(file);
    public void Dispose() => http.Dispose();
}
