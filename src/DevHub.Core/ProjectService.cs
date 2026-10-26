using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace DevHub.Core;

public sealed class ProjectService(HubPaths paths, ICommandRunner runner)
{
    public Dictionary<string, string> BuildEnvironment(ProjectProfile profile, IReadOnlyList<ToolInstallation> installations, IReadOnlyDictionary<string, string>? baseEnvironment = null)
    {
        if (!Directory.Exists(profile.Directory)) throw new DirectoryNotFoundException("项目目录不存在：" + profile.Directory);
        var env = paths.CacheEnvironment();
        if (baseEnvironment is not null) foreach (var pair in baseEnvironment) env[pair.Key] = pair.Value;
        var bins = new List<string>();
        foreach (var binding in profile.Bindings)
        {
            var installation = installations.FirstOrDefault(t => t.Id == binding.Value) ?? throw new InvalidOperationException($"项目绑定的 {binding.Key} 未找到，请重新选择版本。");
            if (!File.Exists(installation.ExecutablePath)) throw new InvalidOperationException("项目工具入口已经失效：" + installation.ExecutablePath);
            bins.Add(Path.GetDirectoryName(installation.ExecutablePath)!);
            switch (binding.Key)
            {
                case "java": env["JAVA_HOME"] = installation.HomePath; env["JDK_HOME"] = installation.HomePath; break;
                case "python":
                    env["PYTHONHOME"] = ""; env["PYTHONPATH"] = "";
                    if (Directory.Exists(Path.Combine(installation.HomePath, "Scripts"))) bins.Add(Path.Combine(installation.HomePath, "Scripts"));
                    break;
                case "flutter": env["FLUTTER_ROOT"] = installation.HomePath; break;
                case "go": env["GOROOT"] = installation.HomePath; env["GOTOOLCHAIN"] = "local"; break;
                case "rust": env["RUSTUP_TOOLCHAIN"] = installation.SelectionVersion ?? installation.Version.Split(' ')[0]; break;
                case "dotnet":
                    env["DOTNET_ROOT"] = installation.HomePath;
                    var global = Path.Combine(profile.Directory, "global.json");
                    if (!File.Exists(global)) throw new InvalidOperationException("请在项目页保存 .NET 版本绑定，写入 global.json 后再打开终端。");
                    using (var json = JsonDocument.Parse(File.ReadAllText(global), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }))
                        if (!json.RootElement.TryGetProperty("sdk", out var sdk) || !sdk.TryGetProperty("version", out var sdkVersion) || sdkVersion.GetString() != CleanVersion(installation.Version)) throw new InvalidOperationException("项目 global.json 与 .NET 绑定不一致，请在项目页确认版本声明。");
                    break;
                case "android":
                    env["ANDROID_HOME"] = installation.HomePath; env["ANDROID_SDK_ROOT"] = installation.HomePath;
                    bins.Add(Path.Combine(installation.HomePath, "platform-tools")); bins.Add(Path.Combine(installation.HomePath, "emulator")); break;
            }
        }
        if (!string.IsNullOrWhiteSpace(profile.VirtualEnvironment))
        {
            var scripts = Path.Combine(profile.VirtualEnvironment, "Scripts");
            if (!File.Exists(Path.Combine(scripts, "python.exe"))) throw new InvalidOperationException("项目虚拟环境已失效，请重新配置。");
            bins.Insert(0, scripts); env["VIRTUAL_ENV"] = profile.VirtualEnvironment; env["PYTHONHOME"] = ""; env["PYTHONPATH"] = "";
        }
        env["PATH"] = string.Join(';', bins.Distinct(StringComparer.OrdinalIgnoreCase)) + ";" + (env.GetValueOrDefault("PATH") ?? Environment.GetEnvironmentVariable("PATH"));
        var mavenCache = Path.Combine(paths.Cache, "maven", "repository"); Directory.CreateDirectory(mavenCache);
        env["MAVEN_OPTS"] = (Environment.GetEnvironmentVariable("MAVEN_OPTS") ?? "") + " -Dmaven.repo.local=\"" + mavenCache + "\"";
        return env;
    }

    public void OpenTerminal(ProjectProfile profile, IReadOnlyList<ToolInstallation> installations, IReadOnlyDictionary<string, string>? fresh = null)
    {
        var env = BuildEnvironment(profile, installations, fresh);
        var script = "Set-Location -LiteralPath " + ProcessRunner.PowerShellLiteral(profile.Directory) + "; Write-Host " + ProcessRunner.PowerShellLiteral("DevHub · " + profile.Name + " · 项目环境已载入") + " -ForegroundColor Cyan";
        var info = new ProcessStartInfo(ProcessRunner.PowerShellExe) { UseShellExecute = false, CreateNoWindow = false, WorkingDirectory = profile.Directory };
        info.ArgumentList.Add("-NoProfile"); info.ArgumentList.Add("-NoExit"); info.ArgumentList.Add("-EncodedCommand"); info.ArgumentList.Add(ProcessRunner.EncodedScript(script));
        foreach (var pair in env) info.Environment[pair.Key] = pair.Value;
        Process.Start(info);
    }

    public async Task<string> CreateVenvAsync(ProjectProfile profile, ToolInstallation python, Action<string>? output = null, CancellationToken ct = default)
    {
        var target = Path.Combine(profile.Directory, ".venv");
        if (Directory.Exists(target)) throw new InvalidOperationException("项目已有 .venv，请选择已有环境，不会覆盖。");
        var result = await runner.RunAsync(new(python.ExecutablePath, ["-m", "venv", target], profile.Directory, TimeoutSeconds: 180), output, ct);
        result.EnsureSuccess();
        if (!File.Exists(Path.Combine(target, "Scripts", "python.exe"))) throw new InvalidOperationException("虚拟环境创建后缺少 Python 入口。");
        profile.VirtualEnvironment = target;
        return target;
    }

    public List<string> InspectDeclarations(ProjectProfile profile)
    {
        var lines = new List<string>();
        foreach (var file in new[] { ".python-version", ".node-version", ".nvmrc", ".tool-versions", ".sdkmanrc", "rust-toolchain.toml", "rust-toolchain", "global.json", ".fvmrc", "pubspec.yaml", "go.mod", "pyproject.toml", "package.json", ".idea/misc.xml", ".idea/gradle.xml" })
        {
            var path = Path.Combine(profile.Directory, file.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(path)) continue;
            var content = string.Join('\n', File.ReadLines(path).Take(35));
            lines.Add($"{file}\n{content}");
        }
        if (File.Exists(Path.Combine(profile.Directory, "mvnw.cmd"))) lines.Add("已发现 Maven Wrapper：项目终端中使用 .\\mvnw.cmd");
        if (File.Exists(Path.Combine(profile.Directory, "gradlew.bat"))) lines.Add("已发现 Gradle Wrapper：项目终端中使用 .\\gradlew.bat");
        return lines;
    }

    public string VersionFileContent(string toolId, ToolInstallation installation) => toolId switch
    {
        "dotnet" => JsonSerializer.Serialize(new { sdk = new { version = CleanVersion(installation.Version), rollForward = "disable", allowPrerelease = false } }, JsonDefaults.Options),
        "python" or "node" => CleanVersion(installation.Version) + "\n",
        "rust" => "[toolchain]\nchannel = \"" + (installation.SelectionVersion ?? CleanVersion(installation.Version)) + "\"\n",
        "flutter" => JsonSerializer.Serialize(new { flutter = CleanVersion(installation.Version) }, JsonDefaults.Options),
        _ => throw new InvalidOperationException("该工具的项目绑定保存在 DevHub 中，无需创建版本声明文件。")
    };
    public static string VersionFileName(string toolId) => toolId switch { "dotnet" => "global.json", "python" => ".python-version", "node" => ".node-version", "rust" => "rust-toolchain.toml", "flutter" => ".fvmrc", _ => throw new ArgumentException("不支持的版本文件。") };

    public async Task WriteVersionFileAsync(ProjectProfile project, string toolId, ToolInstallation installation, bool allowReplace)
    {
        var target = Path.Combine(project.Directory, VersionFileName(toolId));
        if (File.Exists(target) && !allowReplace) throw new InvalidOperationException("已有版本声明，需明确选择替换。");
        var content = VersionFileContent(toolId, installation);
        if (File.Exists(target)) BackupFiles([target], "project-version");
        await File.WriteAllTextAsync(target, content);
    }

    public string ConfigureIdea(ProjectProfile project, ToolInstallation java, bool replace)
    {
        if (java.ToolId != "java" || !java.IsJdk) throw new InvalidOperationException("请选择一个完整 JDK。");
        if (Process.GetProcessesByName("idea64").Length > 0 || Process.GetProcessesByName("idea").Length > 0) throw new InvalidOperationException("请关闭 IntelliJ IDEA 后再应用，避免配置被覆盖。");
        var config = project.IdeaConfigDirectory;
        if (string.IsNullOrWhiteSpace(config) || !Directory.Exists(config)) throw new InvalidOperationException("请选择 IntelliJ IDEA 配置目录（包含 options 的目录）。");
        var miscPath = Path.Combine(project.Directory, ".idea", "misc.xml");
        var gradlePath = Path.Combine(project.Directory, ".idea", "gradle.xml");
        var tablePath = Path.Combine(config, "options", "jdk.table.xml");
        var misc = LoadXml(miscPath); var gradle = LoadXml(gradlePath); var table = LoadXml(tablePath);
        var manager = Component(misc, "ProjectRootManager");
        var sdkName = "DevHub-" + CleanVersion(java.Version) + "-" + java.Id[..6];
        var oldSdk = manager.Attribute("project-jdk-name")?.Value;
        if (!replace && oldSdk is not null && oldSdk != sdkName) throw new InvalidOperationException("项目已有 IDEA JDK 配置，需要选择替换。");
        manager.SetAttributeValue("version", "2"); manager.SetAttributeValue("project-jdk-name", sdkName); manager.SetAttributeValue("project-jdk-type", "JavaSDK");
        var sdkTable = Component(table, "ProjectJdkTable");
        sdkTable.Elements("jdk").Where(j => j.Element("name")?.Attribute("value")?.Value == sdkName).Remove();
        var home = java.HomePath.Replace('\\', '/');
        var classRoots = new List<XElement>();
        var jmods = Path.Combine(java.HomePath, "jmods");
        if (Directory.Exists(jmods))
            classRoots.AddRange(Directory.GetFiles(jmods, "*.jmod").Select(m => new XElement("root", new XAttribute("url", "jrt://" + home + "!/" + Path.GetFileNameWithoutExtension(m)), new XAttribute("type", "simple"))));
        else
            foreach (var folder in new[] { Path.Combine(java.HomePath, "jre", "lib"), Path.Combine(java.HomePath, "lib") }.Where(Directory.Exists))
                classRoots.AddRange(Directory.GetFiles(folder, "*.jar").Select(j => new XElement("root", new XAttribute("url", "jar://" + j.Replace('\\', '/') + "!/"), new XAttribute("type", "simple"))));
        var source = File.Exists(Path.Combine(java.HomePath, "lib", "src.zip")) ? home + "/lib/src.zip" : home + "/src.zip";
        sdkTable.Add(new XElement("jdk", new XAttribute("version", "2"),
            new XElement("name", new XAttribute("value", sdkName)), new XElement("type", new XAttribute("value", "JavaSDK")),
            new XElement("version", new XAttribute("value", "java version \"" + CleanVersion(java.Version) + "\"")),
            new XElement("homePath", new XAttribute("value", home)),
            new XElement("roots",
                new XElement("annotationsPath", new XElement("root", new XAttribute("type", "composite"))),
                new XElement("classPath", new XElement("root", new XAttribute("type", "composite"), classRoots)),
                new XElement("javadocPath", new XElement("root", new XAttribute("type", "composite"))),
                new XElement("sourcePath", new XElement("root", new XAttribute("type", "composite"), File.Exists(source) ? new XElement("root", new XAttribute("url", "jar://" + source + "!/"), new XAttribute("type", "simple")) : null))),
            new XElement("additional")));
        foreach (var linked in Component(gradle, "GradleSettings").Descendants("GradleProjectSettings"))
        {
            var option = linked.Elements("option").FirstOrDefault(o => o.Attribute("name")?.Value == "gradleJvm");
            if (option is null) linked.Add(new XElement("option", new XAttribute("name", "gradleJvm"), new XAttribute("value", sdkName)));
            else option.SetAttributeValue("value", sdkName);
        }
        var files = new[] { miscPath, tablePath }.Concat(File.Exists(gradlePath) ? [gradlePath] : []).ToArray();
        BackupFiles(files, "idea");
        var docs = new Dictionary<string, XDocument> { [miscPath] = misc, [tablePath] = table };
        if (File.Exists(gradlePath)) docs[gradlePath] = gradle;
        WriteXmlTransaction(docs);
        return "已配置 IDEA 项目 JDK 和现有 Gradle JVM；配置备份已保存。模块或 Maven 中的显式 JDK 覆盖请在 IDEA 中检查。";
    }

    public string BackupFiles(IEnumerable<string> files, string description)
    {
        var folder = Path.Combine(paths.Backups, $"{description}-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}"); Directory.CreateDirectory(folder);
        var manifest = new List<FileBackup>(); var index = 0;
        foreach (var file in files)
        {
            var copy = (++index) + Path.GetExtension(file);
            var existed = File.Exists(file);
            if (existed) File.Copy(file, Path.Combine(folder, copy));
            manifest.Add(new(file, copy, existed));
        }
        File.WriteAllText(Path.Combine(folder, "manifest.json"), JsonSerializer.Serialize(manifest, JsonDefaults.Options));
        return folder;
    }
    public void RestoreFiles(string folder)
    {
        HubPaths.RequireSafeChild(folder, paths.Backups);
        var records = JsonSerializer.Deserialize<List<FileBackup>>(File.ReadAllText(Path.Combine(folder, "manifest.json")), JsonDefaults.Options) ?? throw new InvalidDataException("备份损坏。");
        foreach (var record in records)
        {
            var copy = Path.Combine(folder, record.Copy); HubPaths.RequireSafeChild(copy, folder);
            if (record.Existed && !File.Exists(copy)) throw new InvalidDataException("备份文件缺失。");
        }
        BackupFiles(records.Select(r => r.Path), "before-restore");
        foreach (var record in records)
        {
            if (record.Existed) { Directory.CreateDirectory(Path.GetDirectoryName(record.Path)!); File.Copy(Path.Combine(folder, record.Copy), record.Path, true); }
            else if (File.Exists(record.Path)) File.Delete(record.Path);
        }
    }
    private static void WriteXmlTransaction(Dictionary<string, XDocument> documents)
    {
        var original = documents.Keys.ToDictionary(f => f, f => File.Exists(f) ? File.ReadAllBytes(f) : null);
        try
        {
            foreach (var pair in documents)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(pair.Key)!);
                var temp = pair.Key + ".devhub.tmp"; pair.Value.Save(temp); File.Move(temp, pair.Key, true);
            }
        }
        catch
        {
            foreach (var pair in original)
                if (pair.Value is not null) File.WriteAllBytes(pair.Key, pair.Value); else if (File.Exists(pair.Key)) File.Delete(pair.Key);
            throw;
        }
    }
    private static XDocument LoadXml(string path) => File.Exists(path) ? XDocument.Load(path) : new(new XElement("project", new XAttribute("version", "4")));
    private static XElement Component(XDocument document, string name)
    {
        // IDEA's global SDK file uses <application>; project files use <project>.
        if (name == "ProjectJdkTable" && document.Root?.Name == "project" && !document.Root.Elements().Any())
        {
            document.Root.Name = "application"; document.Root.Attribute("version")?.Remove();
        }
        var component = document.Root!.Elements("component").FirstOrDefault(e => e.Attribute("name")?.Value == name);
        if (component is null) { component = new("component", new XAttribute("name", name)); document.Root.Add(component); }
        return component;
    }
    public static string CleanVersion(string version) => Regex.Match(version, @"\d+\.\d+(?:\.\d+)?(?:[+-][\w.-]+)?").Value;
}
public sealed record FileBackup(string Path, string Copy, bool Existed);
