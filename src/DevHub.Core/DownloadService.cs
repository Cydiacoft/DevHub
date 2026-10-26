using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace DevHub.Core;

public interface IVersionProvider
{
    Task<List<DownloadRelease>> GetReleasesAsync(string toolId, string vendor, string version, CancellationToken cancellation = default);
}

public sealed class DownloadService : IVersionProvider, IDisposable
{
    private readonly HubPaths paths;
    private readonly HttpClient http;
    public DownloadService(HubPaths paths, HttpMessageHandler? handler = null)
    {
        this.paths = paths;
        http = handler is null ? new HttpClient() : new HttpClient(handler);
        http.DefaultRequestHeaders.UserAgent.ParseAdd("DevHub/1.0");
        http.Timeout = TimeSpan.FromMinutes(20);
    }
    public void Dispose() => http.Dispose();

    private async Task<JsonDocument> JsonAsync(string url, CancellationToken cancellation)
    {
        ValidateHttps(url);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation); timeout.CancelAfter(TimeSpan.FromSeconds(40));
        var text = await http.GetStringAsync(url, timeout.Token);
        var dir = Path.Combine(paths.Cache, "metadata"); Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(Path.Combine(dir, DiscoveryService.StableId(url) + ".json"), text, cancellation);
        return JsonDocument.Parse(text);
    }
    private async Task<string?> ChecksumAsync(string url, int length, CancellationToken cancellation)
    {
        try
        {
            ValidateHttps(url);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation); timeout.CancelAfter(TimeSpan.FromSeconds(25));
            var text = await http.GetStringAsync(url, timeout.Token);
            var match = Regex.Match(text, $@"\b[a-fA-F0-9]{{{length}}}\b");
            return match.Success ? match.Value.ToLowerInvariant() : null;
        }
        catch (HttpRequestException) { return null; }
    }

    public async Task<List<DownloadRelease>> GetReleasesAsync(string toolId, string vendor, string version, CancellationToken cancellation = default)
    {
        version = version.Trim();
        if (version.Length > 60 || !Regex.IsMatch(version, @"^[\w.+-]*$")) throw new ArgumentException("请输入有效的版本号或渠道。");
        List<DownloadRelease> releases = toolId switch
        {
            "java" => await JavaAsync(vendor, string.IsNullOrEmpty(version) ? "21" : version, cancellation),
            "node" => await NodeAsync(version, cancellation),
            "go" => await GoAsync(version, cancellation),
            "flutter" => await FlutterAsync(vendor, version, cancellation),
            "dart" => await DartAsync(version, cancellation),
            "dotnet" => await DotnetAsync(vendor, version, cancellation),
            "android" => await AndroidAsync(cancellation),
            _ => throw new InvalidOperationException("该工具使用所属管理器安装，请在工具页面选择安装来源。")
        };
        return releases.Take(60).ToList();
    }

    private async Task<List<DownloadRelease>> JavaAsync(string vendor, string majorText, CancellationToken ct)
    {
        if (!int.TryParse(majorText, out var major) || major is < 8 or > 100) throw new ArgumentException("Java 请输入主版本号，例如 8、17、21、25。");
        if (vendor == "Temurin")
        {
            using var json = await JsonAsync($"https://api.adoptium.net/v3/assets/latest/{major}/hotspot?architecture=x64&image_type=jdk&os=windows&vendor=eclipse", ct);
            return json.RootElement.EnumerateArray().Select(x => new DownloadRelease
            {
                ToolId = "java", Vendor = vendor, Version = x.GetProperty("version").GetProperty("openjdk_version").GetString()!,
                Url = x.GetProperty("binary").GetProperty("package").GetProperty("link").GetString()!,
                Sha256 = x.GetProperty("binary").GetProperty("package").GetProperty("checksum").GetString()
            }).ToList();
        }
        if (vendor == "Zulu")
        {
            using var json = await JsonAsync($"https://api.azul.com/metadata/v1/zulu/packages/?java_version={major}&os=windows&arch=x86_64&archive_type=zip&java_package_type=jdk&availability_types=CA&release_status=ga&latest=true", ct);
            var list = new List<DownloadRelease>();
            foreach (var item in json.RootElement.EnumerateArray().Take(12))
            {
                var url = item.GetProperty("download_url").GetString()!;
                if (url.Contains("-fx-") || url.Contains("musl")) continue;
                var uuid = item.GetProperty("package_uuid").GetString();
                using var detail = await JsonAsync("https://api.azul.com/metadata/v1/zulu/packages/" + uuid, ct);
                var sha = detail.RootElement.TryGetProperty("sha256_hash", out var hash) ? hash.GetString() : await ChecksumAsync(url + ".sha256", 64, ct);
                list.Add(new() { ToolId = "java", Vendor = vendor, Version = string.Join('.', item.GetProperty("java_version").EnumerateArray().Select(n => n.GetInt32())), Url = url, Sha256 = sha, Manual = sha is null });
            }
            return list;
        }
        if (vendor == "Microsoft")
        {
            var page = await http.GetStringAsync("https://learn.microsoft.com/en-us/java/openjdk/download", ct);
            var matches = Regex.Matches(page, @"https://aka\.ms/download-jdk/microsoft-jdk-[^""<>\s]+windows-x64\.zip").Select(m => WebUtility.HtmlDecode(m.Value)).Distinct();
            var list = new List<DownloadRelease>();
            foreach (var url in matches.Where(u => u.Contains($"microsoft-jdk-{major}.", StringComparison.OrdinalIgnoreCase)).Take(10))
            {
                var sha = await ChecksumAsync(url + ".sha256sum.txt", 64, ct);
                list.Add(new() { ToolId = "java", Vendor = vendor, Version = Regex.Match(url, @"microsoft-jdk-(.+)-windows").Groups[1].Value, Url = url, Sha256 = sha, Manual = sha is null });
            }
            return list.Count > 0 ? list : [new() { ToolId = "java", Vendor = vendor, Version = majorText, Url = "https://learn.microsoft.com/en-us/java/openjdk/download", Manual = true }];
        }
        if (vendor == "Oracle")
        {
            var page = await http.GetStringAsync("https://www.oracle.com/java/technologies/downloads/", ct);
            var url = Regex.Matches(page, @"https://download\.oracle\.com/java/[^""<>\s]+windows-x64_bin\.zip").Select(m => m.Value).FirstOrDefault(u => u.Contains($"/java/{major}/"));
            if (url is null) return [new() { ToolId = "java", Vendor = vendor, Version = majorText, Url = "https://www.oracle.com/java/technologies/downloads/archive/", Manual = true }];
            var sha = await ChecksumAsync(url + ".sha256", 64, ct);
            return [new() { ToolId = "java", Vendor = vendor, Version = majorText + "-latest", Url = url, Sha256 = sha, Manual = sha is null }];
        }
        throw new ArgumentException("未知 Java 发行版。");
    }

    private async Task<List<DownloadRelease>> NodeAsync(string version, CancellationToken ct)
    {
        using var json = await JsonAsync("https://nodejs.org/dist/index.json", ct);
        var list = new List<DownloadRelease>();
        foreach (var item in json.RootElement.EnumerateArray())
        {
            var v = item.GetProperty("version").GetString()!;
            if (!v.TrimStart('v').StartsWith(version.TrimStart('v'), StringComparison.OrdinalIgnoreCase) || !item.GetProperty("files").EnumerateArray().Any(f => f.GetString() == "win-x64-zip")) continue;
            var name = $"node-{v}-win-x64.zip";
            var sums = await http.GetStringAsync($"https://nodejs.org/dist/{v}/SHASUMS256.txt", ct);
            var sha = sums.Split('\n').FirstOrDefault(l => l.TrimEnd().EndsWith(" " + name, StringComparison.Ordinal))?.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
            list.Add(new() { ToolId = "node", Vendor = "Node.js", Version = v, Url = $"https://nodejs.org/dist/{v}/{name}", Sha256 = sha, Channel = item.GetProperty("lts").ValueKind == JsonValueKind.String ? "LTS" : "Current" });
            if (list.Count >= 12) break;
        }
        return list;
    }
    private async Task<List<DownloadRelease>> GoAsync(string version, CancellationToken ct)
    {
        using var json = await JsonAsync("https://go.dev/dl/?mode=json&include=all", ct);
        var list = new List<DownloadRelease>();
        foreach (var item in json.RootElement.EnumerateArray())
        {
            var v = item.GetProperty("version").GetString()!;
            if (!item.GetProperty("stable").GetBoolean() || !v.Replace("go", "").StartsWith(version)) continue;
            foreach (var file in item.GetProperty("files").EnumerateArray().Where(f => f.GetProperty("os").GetString() == "windows" && f.GetProperty("arch").GetString() == "amd64" && f.GetProperty("kind").GetString() == "archive"))
                list.Add(new() { ToolId = "go", Vendor = "Go", Version = v, Url = "https://go.dev/dl/" + file.GetProperty("filename").GetString(), Sha256 = file.GetProperty("sha256").GetString() });
            if (list.Count >= 25) break;
        }
        return list;
    }
    private async Task<List<DownloadRelease>> FlutterAsync(string channel, string version, CancellationToken ct)
    {
        using var json = await JsonAsync("https://storage.googleapis.com/flutter_infra_release/releases/releases_windows.json", ct);
        var root = json.RootElement; var baseUrl = root.GetProperty("base_url").GetString()!;
        return root.GetProperty("releases").EnumerateArray().Where(r => (channel == "全部" || r.GetProperty("channel").GetString() == channel) && r.GetProperty("version").GetString()!.StartsWith(version) && (!r.TryGetProperty("dart_sdk_arch", out var arch) || arch.GetString() == "x64"))
            .Take(50).Select(r => new DownloadRelease { ToolId = "flutter", Vendor = "Flutter", Version = r.GetProperty("version").GetString()!, Url = baseUrl + "/" + r.GetProperty("archive").GetString(), Sha256 = r.GetProperty("sha256").GetString(), Channel = r.GetProperty("channel").GetString() }).ToList();
    }
    private async Task<List<DownloadRelease>> DartAsync(string version, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(version))
        {
            using var json = await JsonAsync("https://storage.googleapis.com/dart-archive/channels/stable/release/latest/VERSION", ct);
            version = json.RootElement.GetProperty("version").GetString()!;
        }
        var url = $"https://storage.googleapis.com/dart-archive/channels/stable/release/{version}/sdk/dartsdk-windows-x64-release.zip";
        return [new() { ToolId = "dart", Vendor = "Dart", Version = version, Url = url, Sha256 = await ChecksumAsync(url + ".sha256sum", 64, ct) }];
    }
    private async Task<List<DownloadRelease>> DotnetAsync(string kind, string channel, CancellationToken ct)
    {
        using var index = await JsonAsync("https://builds.dotnet.microsoft.com/dotnet/release-metadata/releases-index.json", ct);
        var chosen = index.RootElement.GetProperty("releases-index").EnumerateArray().FirstOrDefault(c => c.GetProperty("channel-version").GetString()!.StartsWith(channel) && c.GetProperty("support-phase").GetString() != "eol");
        if (chosen.ValueKind == JsonValueKind.Undefined) return [];
        using var releases = await JsonAsync(chosen.GetProperty("releases.json").GetString()!, ct);
        var list = new List<DownloadRelease>();
        var seen = new HashSet<string>();
        foreach (var release in releases.RootElement.GetProperty("releases").EnumerateArray())
        {
            var section = kind == "Runtime" ? "runtime" : "sdk";
            var components = section == "sdk" && release.TryGetProperty("sdks", out var sdks) ? sdks.EnumerateArray().ToArray() : release.TryGetProperty(section, out var sdk) ? [sdk] : [];
            foreach (var component in components)
            {
                var version = component.GetProperty("version").GetString()!;
                if (!seen.Add(version)) continue;
                foreach (var file in component.GetProperty("files").EnumerateArray().Where(f => f.GetProperty("rid").GetString() == "win-x64" && f.GetProperty("url").GetString()!.EndsWith(".zip")))
                    list.Add(new() { ToolId = "dotnet", Vendor = ".NET " + kind, Version = version, Url = file.GetProperty("url").GetString()!, Sha512 = file.GetProperty("hash").GetString() });
            }
            if (list.Count >= 25) break;
        }
        return list;
    }

    private async Task<List<DownloadRelease>> AndroidAsync(CancellationToken ct)
    {
        var document = XDocument.Parse(await http.GetStringAsync("https://dl.google.com/android/repository/repository2-3.xml", ct));
        var package = document.Descendants().FirstOrDefault(e => e.Name.LocalName == "remotePackage" && e.Attribute("path")?.Value == "cmdline-tools;latest")
            ?? throw new InvalidDataException("官方目录中未找到 Android 命令行工具。");
        var revision = package.Elements().First(e => e.Name.LocalName == "revision");
        var version = string.Join('.', revision.Elements().Where(e => e.Name.LocalName is "major" or "minor" or "micro").Select(e => e.Value));
        var list = new List<DownloadRelease>();
        foreach (var archive in package.Descendants().Where(e => e.Name.LocalName == "archive" && e.Elements().Any(c => c.Name.LocalName == "host-os" && c.Value == "windows")))
        {
            var complete = archive.Elements().First(e => e.Name.LocalName == "complete");
            var url = complete.Elements().First(e => e.Name.LocalName == "url").Value;
            var checksum = complete.Elements().First(e => e.Name.LocalName == "checksum");
            var algorithm = checksum.Attribute("type")?.Value ?? "sha1";
            list.Add(new() { ToolId = "android", Vendor = "Android-CommandLineTools", Version = version,
                Url = "https://dl.google.com/android/repository/" + url,
                Sha1 = algorithm == "sha1" ? checksum.Value : null, Sha256 = algorithm == "sha256" ? checksum.Value : null });
        }
        return list;
    }

    public async Task<string> InstallAsync(DownloadRelease release, IProgress<(double Progress, string Detail)>? progress = null, CancellationToken cancellation = default)
    {
        if (release.Manual) { ProcessRunner.OpenWebsite(release.Url); return "已打开官网，请下载 ZIP 后使用导入。"; }
        ValidateHttps(release.Url);
        if (string.IsNullOrWhiteSpace(release.Sha256) && string.IsNullOrWhiteSpace(release.Sha512) && string.IsNullOrWhiteSpace(release.Sha1)) throw new InvalidOperationException("官方下载未提供可验证的校验值，请从官网下载并导入安装包。");
        var downloads = Path.Combine(paths.Cache, "downloads"); Directory.CreateDirectory(downloads);
        var archive = Path.Combine(downloads, DiscoveryService.StableId(release.Url + release.Sha256 + release.Sha512 + release.Sha1) + ".zip");
        var partial = archive + "." + Guid.NewGuid().ToString("N") + ".partial";
        try
        {
            if (!File.Exists(archive))
            {
                using var response = await http.GetAsync(release.Url, HttpCompletionOption.ResponseHeadersRead, cancellation);
                response.EnsureSuccessStatusCode();
                if (response.RequestMessage?.RequestUri?.Scheme != "https") throw new InvalidOperationException("下载跳转到了非 HTTPS 地址。");
                var total = response.Content.Headers.ContentLength;
                await using (var input = await response.Content.ReadAsStreamAsync(cancellation))
                await using (var output = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
                {
                    var buffer = new byte[81920]; long copied = 0; int read;
                    while ((read = await input.ReadAsync(buffer, cancellation)) > 0)
                    {
                        await output.WriteAsync(buffer.AsMemory(0, read), cancellation); copied += read;
                        progress?.Report((total > 0 ? copied * 70d / total.Value : 10, $"下载中 · {copied / 1048576d:F1} MB"));
                    }
                }
                await VerifyAsync(partial, release.Sha256, release.Sha512, cancellation, release.Sha1);
                File.Move(partial, archive, true);
            }
            else await VerifyAsync(archive, release.Sha256, release.Sha512, cancellation, release.Sha1);
            return await ExtractInstallAsync(archive, release, progress, cancellation);
        }
        finally { if (File.Exists(partial)) File.Delete(partial); }
    }

    public async Task<string> ImportZipAsync(string archive, string toolId, string version, CancellationToken ct = default)
    {
        if (!File.Exists(archive) || !archive.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("请选择已下载的 ZIP 安装包。");
        return await ExtractInstallAsync(archive, new() { ToolId = toolId, Vendor = "本地导入", Version = version, Url = "local" }, null, ct);
    }

    private async Task<string> ExtractInstallAsync(string archive, DownloadRelease release, IProgress<(double Progress, string Detail)>? progress, CancellationToken ct)
    {
        var folderName = Regex.Replace(release.Vendor + "-" + release.Version, @"[^\w.+-]", "-");
        var parent = Path.Combine(paths.Sdks, release.ToolId); Directory.CreateDirectory(parent);
        var destination = Path.Combine(parent, folderName);
        HubPaths.RequireSafeChild(destination, paths.Sdks);
        if (Directory.Exists(destination)) throw new InvalidOperationException("该版本目录已经存在，请先检查已有安装。");
        var staging = Path.Combine(parent, ".install-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            progress?.Report((75, "校验通过，正在解压…"));
            await Task.Run(() => ExtractSafely(archive, staging, ct), ct);
            var children = Directory.GetDirectories(staging);
            var root = children.Length == 1 && Directory.GetFiles(staging).Length == 0 ? children[0] : staging;
            if (release.ToolId == "android")
            {
                var layout = Path.Combine(staging, ".android-layout");
                var tools = Path.Combine(layout, "cmdline-tools"); Directory.CreateDirectory(tools);
                if (root == staging) throw new InvalidDataException("Android 安装包应包含 cmdline-tools 根目录。");
                await DirectoryOperations.MoveAsync(root, Path.Combine(tools, "latest"), ct); root = layout;
            }
            var expected = release.ToolId switch
            {
                "java" => @"bin\java.exe", "node" => "node.exe", "go" => @"bin\go.exe", "flutter" => @"bin\flutter.bat",
                "dart" => @"bin\dart.exe", "dotnet" => "dotnet.exe", "python" => "python.exe", "android" => @"cmdline-tools\latest\bin\sdkmanager.bat", _ => throw new InvalidOperationException("此工具不支持 ZIP 导入。")
            };
            if (!File.Exists(Path.Combine(root, expected))) throw new InvalidDataException("安装包不包含预期的工具入口：" + expected);
            ct.ThrowIfCancellationRequested();
            await File.WriteAllTextAsync(Path.Combine(root, ".devhub-install.json"), JsonSerializer.Serialize(release, JsonDefaults.Options), ct);
            await DirectoryOperations.MoveAsync(root, destination, ct);
            progress?.Report((100, "已安装，正在重新扫描…"));
            return destination;
        }
        finally { if (Directory.Exists(staging)) { HubPaths.RequireSafeChild(staging, paths.Sdks); Directory.Delete(staging, true); } }
    }

    public static void ExtractSafely(string archive, string destination, CancellationToken ct = default)
    {
        using var zip = ZipFile.OpenRead(archive);
        foreach (var entry in zip.Entries)
        {
            ct.ThrowIfCancellationRequested();
            var name = entry.FullName.Replace('/', Path.DirectorySeparatorChar);
            if (name.Contains(':') || ((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000) throw new InvalidDataException("安装包包含不允许的链接或路径。");
            var path = Path.GetFullPath(Path.Combine(destination, name));
            if (path.TrimEnd('\\').Equals(Path.GetFullPath(destination).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) continue;
            HubPaths.RequireSafeChild(path, destination);
            if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\')) { Directory.CreateDirectory(path); continue; }
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            entry.ExtractToFile(path, false);
        }
    }
    public static async Task VerifyAsync(string file, string? sha256, string? sha512, CancellationToken ct = default, string? sha1 = null)
    {
        await using var stream = File.OpenRead(file);
        var actual = sha512 is not null ? Convert.ToHexString(await SHA512.HashDataAsync(stream, ct)) : sha256 is not null ? Convert.ToHexString(await SHA256.HashDataAsync(stream, ct)) : Convert.ToHexString(await SHA1.HashDataAsync(stream, ct));
        var expected = sha512 ?? sha256 ?? sha1;
        if (expected is null || !actual.Equals(expected, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("安装包校验失败，文件未安装。");
    }
    private static void ValidateHttps(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https") throw new InvalidOperationException("下载地址必须是 HTTPS。");
    }
}
