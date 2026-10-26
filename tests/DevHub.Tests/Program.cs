using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DevHub.Core;

var paths = HubPaths.Discover();
if (args.Contains("--agents"))
{
    var store = new JsonStore(paths); var settings = await store.LoadAsync("settings", () => new HubSettings());
    var projects = await store.LoadAsync("projects", () => new List<ProjectProfile>());
    var inventory = await new DiscoveryService(paths, new ProcessRunner(paths)).ScanAsync(settings, Console.WriteLine);
    var directories = CacheService.DiscoverProjects(settings.CacheProjectRoots.Concat(projects.Select(p => p.Directory)).Append(Directory.GetParent(paths.Root)?.FullName ?? paths.Root));
    var snapshot = await new AgentDiscoveryService().ScanAsync(inventory, directories.Append(paths.Root), Console.WriteLine);
    await File.WriteAllTextAsync(Path.Combine(paths.Logs, "agents-report.json"), JsonSerializer.Serialize(snapshot, JsonDefaults.Options));
    foreach (var client in snapshot.Clients) Console.WriteLine($"{client.Name}: {client.Status}, {client.Installations.Count} installations, {client.ConfigCount} configuration records");
    return 0;
}
if (args.Contains("--providers"))
{
    using var provider = new DownloadService(paths); using var gate = new SemaphoreSlim(3);
    var checks = new[] { ("java", "Temurin", "21"), ("java", "Microsoft", "21"), ("java", "Zulu", "21"), ("java", "Oracle", "21"), ("node", "official", "24"), ("go", "official", ""), ("flutter", "stable", ""), ("dart", "official", ""), ("dotnet", "SDK", "10.0"), ("android", "official", "") };
    var outcomes = await Task.WhenAll(checks.Select(async check =>
    {
        await gate.WaitAsync();
        try
        {
            var releases = await provider.GetReleasesAsync(check.Item1, check.Item2, check.Item3);
            Console.WriteLine($"{check.Item1}/{check.Item2}: {releases.Count} — {releases.FirstOrDefault()}");
            return new { Tool = check.Item1, Vendor = check.Item2, Count = releases.Count, Error = (string?)null };
        }
        catch (Exception e) { Console.WriteLine($"ERROR {check.Item1}/{check.Item2}: {e.Message}"); return new { Tool = check.Item1, Vendor = check.Item2, Count = 0, Error = (string?)e.Message }; }
        finally { gate.Release(); }
    }));
    await File.WriteAllTextAsync(Path.Combine(paths.Logs, "provider-check.json"), JsonSerializer.Serialize(outcomes, JsonDefaults.Options));
    return outcomes.All(o => o.Count > 0) ? 0 : 1;
}
if (args.Contains("--caches"))
{
    var store = new JsonStore(paths); var settings = await store.LoadAsync("settings", () => new HubSettings()); var projects = await store.LoadAsync("projects", () => new List<ProjectProfile>());
    var inventory = await store.LoadAsync("inventory", () => new List<ToolInstallation>()); var cache = new CacheService(paths);
    var entries = await cache.InspectComputerAsync(settings, projects, inventory, Console.WriteLine);
    foreach (var entry in entries) Console.WriteLine($"{entry.Category} | {entry.Owner} | {entry.Name} | {entry.Size} | {(entry.CanClean ? "cleanable" : "protected")}");
    await File.WriteAllTextAsync(Path.Combine(paths.Logs, "cache-report.json"), JsonSerializer.Serialize(new { Projects = cache.LastDiscoveredProjects, Entries = entries }, JsonDefaults.Options));
    return 0;
}
if (args.Contains("--scan"))
{
    var store = new JsonStore(paths); var settings = await store.LoadAsync("settings", () => new HubSettings());
    var scanner = new DiscoveryService(paths, new ProcessRunner(paths));
    var inventory = await scanner.ScanAsync(settings, Console.WriteLine);
    var diagnostics = DiagnosticService.Analyze(inventory, []);
    var report = Path.Combine(paths.Logs, "scan-report.json");
    await File.WriteAllTextAsync(report, JsonSerializer.Serialize(new { captured = DateTimeOffset.Now, inventory, diagnostics }, JsonDefaults.Options));
    foreach (var group in inventory.Where(i => !i.ToolId.StartsWith("unknown:")).GroupBy(i => i.ToolId))
        Console.WriteLine($"{group.Key}: {group.Count()} — {string.Join(" / ", group.Select(i => i.Version))}");
    Console.WriteLine($"Report: {report}\nInstallations: {inventory.Count}; diagnostics: {diagnostics.Count}");
    return 0;
}

var testRoot = Path.Combine(paths.State, "build", "tests", DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N"));
var fixtures = new HubPaths(testRoot);
await File.WriteAllTextAsync(Path.Combine(testRoot, "DevHub.root"), "test");
var tests = new List<(string Name, Func<Task> Run)>();
void Test(string name, Action action) => tests.Add((name, () => { action(); return Task.CompletedTask; }));
void AsyncTest(string name, Func<Task> action) => tests.Add((name, action));
void Equal<T>(T expected, T actual) { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Expected {expected}, got {actual}"); }
void True(bool value, string message = "Assertion failed") { if (!value) throw new Exception(message); }
void Throws<T>(Action action) where T : Exception { try { action(); } catch (T) { return; } throw new Exception("Expected " + typeof(T).Name); }
async Task ThrowsAsync<T>(Func<Task> action) where T : Exception { try { await action(); } catch (T) { return; } throw new Exception("Expected " + typeof(T).Name); }
ToolInstallation FixtureTool(string id, string folder)
{
    var home = Path.Combine(testRoot, folder); var bin = Path.Combine(home, "bin"); Directory.CreateDirectory(bin);
    var exe = Path.Combine(bin, id == "java" ? "java.exe" : id + ".exe"); File.WriteAllText(exe, "fixture");
    if (id == "java") File.WriteAllText(Path.Combine(bin, "javac.exe"), "fixture");
    return new() { Id = id + "-" + folder, ToolId = id, Name = id, HomePath = home, ExecutablePath = exe, RealPath = exe, IsJdk = id == "java", Version = "17.0.15" };
}
var jdk = FixtureTool("java", "含 空格的 JDK"); var python = FixtureTool("python", "Python");
Test("catalog schema and unique identities", () => { var c = DiscoveryService.BuiltInCatalog(); Equal(1, c.SchemaVersion); Equal(c.Tools.Count, c.Tools.Select(t => t.Id).Distinct().Count()); True(c.Tools.Count >= 35); });
Test("cache environment remains under project", () =>
{
    foreach (var pair in fixtures.CacheEnvironment().Where(p => Path.IsPathRooted(p.Value))) True(HubPaths.IsWithin(pair.Value, testRoot) || pair.Value == testRoot, pair.Key);
});
Test("path boundary rejects sibling prefix", () => Throws<InvalidOperationException>(() => HubPaths.RequireSafeChild(testRoot + "-outside/file", testRoot)));
Test("path boundary rejects root deletion", () => Throws<InvalidOperationException>(() => HubPaths.RequireSafeChild(testRoot, testRoot)));
Test("stable IDs ignore Windows case", () => Equal(DiscoveryService.StableId("Java|D:\\JDK"), DiscoveryService.StableId("java|d:\\jdk")));
Test("Java switch keeps unrelated PATH entries", () =>
{
    var fake = new FakeEnvironmentStore(); fake.Write(new("Machine", "Path", @"C:\Tools;C:\Java\old\bin"));
    var request = new EnvironmentService(fixtures, fake).PlanSwitch(jdk);
    var value = request.Changes.First(c => c.After.Name == "Path").After.Value!;
    True(value.StartsWith(Path.GetDirectoryName(jdk.ExecutablePath)!)); True(value.Contains(@"C:\Tools")); True(value.Contains(@"C:\Java\old\bin"));
});
Test("Java switch reconciles existing user JAVA_HOME", () =>
{
    var fake = new FakeEnvironmentStore(); fake.Write(new("User", "JAVA_HOME", @"C:\Old"));
    var req = new EnvironmentService(fixtures, fake).PlanSwitch(jdk);
    Equal(jdk.HomePath, req.Changes.Single(c => c.After.Name == "JAVA_HOME" && c.After.Scope == "User").After.Value);
});
Test("environment transaction persists and backs up", () =>
{
    var fake = new FakeEnvironmentStore(); var service = new EnvironmentService(fixtures, fake); var req = service.PlanSwitch(jdk);
    EnvironmentService.ApplyTransaction(req, fake, fixtures); Equal(jdk.HomePath, fake.Read("Machine", "JAVA_HOME").Value); True(Directory.GetFiles(fixtures.Backups, "environment-*.json").Length > 0);
});
Test("environment transaction refuses stale preview", () =>
{
    var fake = new FakeEnvironmentStore(); var req = new EnvironmentService(fixtures, fake).PlanSwitch(jdk); fake.Write(new("Machine", "Path", "changed"));
    Throws<InvalidOperationException>(() => EnvironmentService.ApplyTransaction(req, fake, fixtures)); Equal("changed", fake.Read("Machine", "Path").Value);
});
Test("environment transaction rolls back failed write", () =>
{
    var fake = new FakeEnvironmentStore(); fake.Write(new("Machine", "Path", "original")); var req = new EnvironmentService(fixtures, fake).PlanSwitch(jdk);
    fake.FailOnceOnName = "JAVA_HOME"; Throws<IOException>(() => EnvironmentService.ApplyTransaction(req, fake, fixtures)); Equal("original", fake.Read("Machine", "Path").Value); Equal<string?>(null, fake.Read("Machine", "JAVA_HOME").Value);
});
Test("environment helper rejects arbitrary variables", () =>
{
    var fake = new FakeEnvironmentStore(); var before = new EnvironmentValue("Machine", "COMSPEC", null); var req = new EnvironmentRequest(testRoot, "test", [new(before, before with { Value = "evil" })], "test");
    Throws<InvalidOperationException>(() => EnvironmentService.ApplyTransaction(req, fake, fixtures));
});
Test("JRE and embedded runtime cannot be global default", () =>
{
    var service = new EnvironmentService(fixtures, new FakeEnvironmentStore()); Throws<InvalidOperationException>(() => service.PlanSwitch(jdk with { IsJdk = false })); Throws<InvalidOperationException>(() => service.PlanSwitch(jdk with { Owner = "IDE" }));
});
Test("fresh environment expands selected JAVA_HOME", () =>
{
    var fake = new FakeEnvironmentStore(); fake.Write(new("Machine", "Path", "%JAVA_HOME%\\bin;C:\\Tools")); fake.Write(new("Machine", "JAVA_HOME", jdk.HomePath));
    var env = new EnvironmentService(fixtures, fake).FreshEnvironment(); True(env["PATH"].StartsWith(jdk.HomePath));
});
Test("project environments stay isolated", () =>
{
    var other = FixtureTool("java", "JDK21") with { Version = "21.0.7" }; var service = new ProjectService(fixtures, new FakeRunner());
    var a = new ProjectProfile { Directory = testRoot, Bindings = new() { ["java"] = jdk.Id } }; var b = new ProjectProfile { Directory = testRoot, Bindings = new() { ["java"] = other.Id } };
    var envA = service.BuildEnvironment(a, [jdk, other]); var envB = service.BuildEnvironment(b, [jdk, other]); Equal(jdk.HomePath, envA["JAVA_HOME"]); Equal(other.HomePath, envB["JAVA_HOME"]); True(envA["PATH"] != envB["PATH"]);
});
Test("missing project binding fails explicitly", () => Throws<InvalidOperationException>(() => new ProjectService(fixtures, new FakeRunner()).BuildEnvironment(new() { Directory = testRoot, Bindings = new() { ["java"] = "missing" } }, [jdk])));
AsyncTest("pip commands target chosen interpreter", async () =>
{
    var fake = new FakeRunner { Result = new(0, "[{\"name\":\"requests\",\"version\":\"2.32.3\"}]", "") }; var service = new PackageService(fixtures, fake);
    var list = await service.ListPythonAsync(python.ExecutablePath); Equal("requests", list.Single().Name); Equal(python.ExecutablePath, fake.Last!.FileName); Equal("-m", fake.Last.Arguments[0]); Equal("pip", fake.Last.Arguments[1]);
});
Test("package names cannot add command options", () => { Throws<ArgumentException>(() => PackageService.ValidatePackage("--target=C:\\")); Throws<ArgumentException>(() => PackageService.ValidatePackage("requests & whoami")); PackageService.ValidatePackage("requests>=2.0"); });
Test("command batch parameters reject quote injection", () => Throws<ArgumentException>(() => ProcessRunner.StartInfo(new("tool.cmd", ["x\" & whoami"]))));
Test("PowerShell literals escape single quotes", () => Equal("'a''b'", ProcessRunner.PowerShellLiteral("a'b")));
AsyncTest("JSON store round trips atomically", async () => { var store = new JsonStore(fixtures); await store.SaveAsync("test", new HubSettings { Theme = "Dark" }); Equal("Dark", (await store.LoadAsync("test", () => new HubSettings())).Theme); True(!Directory.GetFiles(fixtures.Data, "*.tmp").Any()); });
AsyncTest("corrupt configuration is preserved", async () => { var file = Path.Combine(fixtures.Data, "broken.json"); await File.WriteAllTextAsync(file, "invalid"); await ThrowsAsync<InvalidDataException>(() => new JsonStore(fixtures).LoadAsync("broken", () => new HubSettings())); Equal("invalid", await File.ReadAllTextAsync(file)); });
Test("ZIP traversal cannot escape staging", () =>
{
    var zip = Path.Combine(testRoot, "traversal.zip"); using (var a = ZipFile.Open(zip, ZipArchiveMode.Create)) { using var w = new StreamWriter(a.CreateEntry("../escaped.txt").Open()); w.Write("evil"); }
    var staging = Path.Combine(testRoot, "zip-test"); Directory.CreateDirectory(staging); Throws<InvalidOperationException>(() => DownloadService.ExtractSafely(zip, staging)); True(!File.Exists(Path.Combine(testRoot, "escaped.txt")));
});
Test("ZIP symbolic links are rejected", () =>
{
    var zip = Path.Combine(testRoot, "link.zip"); using (var a = ZipFile.Open(zip, ZipArchiveMode.Create)) { var entry = a.CreateEntry("link"); entry.ExternalAttributes = 0xA000 << 16; }
    Throws<InvalidDataException>(() => DownloadService.ExtractSafely(zip, Path.Combine(testRoot, "zip-test")));
});
AsyncTest("checksum failure rejects package", async () => { var file = Path.Combine(testRoot, "hash.txt"); await File.WriteAllTextAsync(file, "data"); await ThrowsAsync<InvalidDataException>(() => DownloadService.VerifyAsync(file, new string('0', 64), null)); });
AsyncTest("verified download installs atomically", async () =>
{
    var bytes = ZipBytes("jdk/bin/java.exe", "jdk/bin/javac.exe"); var hash = Convert.ToHexString(SHA256.HashData(bytes));
    using var service = new DownloadService(fixtures, new FixtureHttp(bytes)); var target = await service.InstallAsync(new() { ToolId = "java", Vendor = "Fixture", Version = "17", Url = "https://example.test/jdk.zip", Sha256 = hash });
    True(File.Exists(Path.Combine(target, "bin", "java.exe"))); True(File.Exists(Path.Combine(target, ".devhub-install.json"))); True(!Directory.GetDirectories(Path.Combine(fixtures.Sdks, "java"), ".install-*").Any());
});
AsyncTest("Android installation uses cmdline-tools latest layout", async () =>
{
    var bytes = ZipBytes("cmdline-tools/bin/sdkmanager.bat", "cmdline-tools/source.properties");
    using var service = new DownloadService(fixtures, new FixtureHttp(bytes));
    var target = await service.InstallAsync(new() { ToolId = "android", Vendor = "Android", Version = "23.0", Url = "https://example.test/android.zip", Sha256 = Convert.ToHexString(SHA256.HashData(bytes)) });
    True(File.Exists(Path.Combine(target, "cmdline-tools", "latest", "bin", "sdkmanager.bat"))); Equal(target, DiscoveryService.FindAndroidRoot(Path.Combine(target, "cmdline-tools", "latest", "bin")));
});
AsyncTest("failed download leaves no installation or partial", async () =>
{
    using var service = new DownloadService(fixtures, new FixtureHttp(Encoding.UTF8.GetBytes("corrupt")));
    await ThrowsAsync<InvalidDataException>(() => service.InstallAsync(new() { ToolId = "node", Vendor = "Fixture", Version = "broken", Url = "https://example.test/bad.zip", Sha256 = new string('0', 64) }));
    True(!Directory.GetFiles(Path.Combine(fixtures.Cache, "downloads"), "*.partial").Any());
});
AsyncTest("invalid archive never becomes installed SDK", async () =>
{
    var file = Path.Combine(testRoot, "invalid.zip"); await File.WriteAllBytesAsync(file, ZipBytes("unrelated.txt"));
    using var service = new DownloadService(fixtures); await ThrowsAsync<InvalidDataException>(() => service.ImportZipAsync(file, "node", "bad")); True(!Directory.GetDirectories(Path.Combine(fixtures.Sdks, "node"), ".install-*").Any());
});
Test("cache cleanup preserves SDK and configuration", () =>
{
    var directory = Path.Combine(fixtures.Cache, "test-cache"); Directory.CreateDirectory(directory); File.WriteAllText(Path.Combine(directory, "data"), "cache");
    var sentinel = Path.Combine(fixtures.Sdks, "keep.txt"); File.WriteAllText(sentinel, "keep"); var service = new CacheService(fixtures); service.Clear(service.Inspect().Single(e => e.Directory == directory));
    True(File.Exists(sentinel)); True(File.Exists(Path.Combine(fixtures.Data, "test.json"))); Equal(0L, CacheService.Measure(directory));
});
Test("cache cleanup rejects SDK paths", () => Throws<InvalidOperationException>(() => new CacheService(fixtures).Clear(new("fake", fixtures.Sdks, 0, ""))));
AsyncTest("uv cache cleanup uses native prune with an explicit directory", async () =>
{
    var dir = Path.Combine(fixtures.Cache, "uv"); Directory.CreateDirectory(dir); var file = Path.Combine(dir, "fixture.dat"); await File.WriteAllTextAsync(file, "keep");
    var service = new CacheService(fixtures); var entry = service.Inspect().Single(e => e.Directory == dir);
    Throws<InvalidOperationException>(() => service.Clear(entry));
    var runner = new FakeRunner(); await service.ClearAsync(entry, runner);
    True(runner.Last!.Arguments.Contains("prune")); True(runner.Last.Arguments.Contains("--cache-dir")); True(runner.Last.Arguments.Contains(dir)); True(File.Exists(file));
});
Test("cache cleanup reports released space and skips locked files", () =>
{
    var dir = Path.Combine(fixtures.Cache, "locked-cache"); Directory.CreateDirectory(dir);
    var free = Path.Combine(dir, "free.bin"); var locked = Path.Combine(dir, "locked.bin"); File.WriteAllBytes(free, new byte[128]); File.WriteAllBytes(locked, new byte[64]);
    using var handle = File.Open(locked, FileMode.Open, FileAccess.Read, FileShare.None);
    var service = new CacheService(fixtures); var entry = service.Inspect().Single(e => e.Directory == dir); var result = service.Clear(entry);
    Equal(1, result.DeletedFiles); Equal(128L, result.ReleasedBytes); Equal(1, result.RetainedFiles); True(File.Exists(locked));
});
Test("temporary cleanup keeps recent and locked files", () =>
{
    var directory = Path.Combine(testRoot, "aged-temp"); Directory.CreateDirectory(directory);
    var old = Path.Combine(directory, "old.tmp"); var recent = Path.Combine(directory, "recent.tmp"); var locked = Path.Combine(directory, "locked.tmp");
    File.WriteAllText(old, "old"); File.WriteAllText(recent, "recent"); File.WriteAllText(locked, "locked");
    File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddDays(-15)); File.SetLastWriteTimeUtc(locked, DateTime.UtcNow.AddDays(-15));
    using var handle = File.Open(locked, FileMode.Open, FileAccess.Read, FileShare.None);
    CacheService.DeleteOldTemporaryFiles(directory, DateTime.UtcNow.AddDays(-7));
    True(!File.Exists(old)); True(File.Exists(recent)); True(File.Exists(locked)); True(Directory.Exists(directory));
});
Test("project discovery skips dependency trees", () =>
{
    var root = Path.Combine(testRoot, "project-cache-fixtures"); var a = Path.Combine(root, "web"); var b = Path.Combine(root, "rust");
    Directory.CreateDirectory(Path.Combine(a, "node_modules", "nested")); Directory.CreateDirectory(b);
    File.WriteAllText(Path.Combine(a, "package.json"), "{}"); File.WriteAllText(Path.Combine(a, "node_modules", "nested", "package.json"), "{}"); File.WriteAllText(Path.Combine(b, "Cargo.toml"), "[package]");
    var found = CacheService.DiscoverProjects([root]); Equal(2, found.Count); True(found.Contains(a)); True(found.Contains(b));
});
Test("version cache fingerprint invalidates changed metadata", () =>
{
    var definition = new ToolDefinition { Id = "java", VersionArgs = ["-version"] };
    var original = DiscoveryService.Fingerprint(jdk, definition); var release = Path.Combine(jdk.HomePath, "release"); File.WriteAllText(release, "JAVA_VERSION=\"17.0.16\"");
    True(original != DiscoveryService.Fingerprint(jdk, definition));
    True(DiscoveryService.Fingerprint(jdk, definition) != DiscoveryService.Fingerprint(jdk, definition with { VersionArgs = ["--version"] }));
});
AsyncTest("Everything failure falls back without aborting scan", async () =>
{
    var fakeCli = Path.Combine(testRoot, "es.exe"); await File.WriteAllTextAsync(fakeCli, "fixture");
    var fake = new FakeRunner { Result = new(8, "", "IPC unavailable") };
    var results = await new EverythingService(fixtures, fake).FindToolPathsAsync(new() { EverythingCliPath = fakeCli }, DiscoveryService.BuiltInCatalog().Tools, null, default);
    Equal(0, results.Count); True(fake.Last!.Arguments.Contains("-export-txt"));
});
AsyncTest("removal protects referenced SDK", async () =>
{
    var service = new ManagementService(fixtures); await ThrowsAsync<InvalidOperationException>(() => service.RemoveManagedAsync(jdk with { Managed = true }, [new() { Bindings = new() { ["java"] = jdk.Id } }]));
});
AsyncTest("SDK removal retires complete files before permanent cleanup", async () =>
{
    var target = Path.Combine(fixtures.Sdks, "java", "Fixture-17");
    var installation = jdk with { Id = "retire-test", HomePath = target, ExecutablePath = Path.Combine(target, "bin", "java.exe"), Managed = true, IsActive = false };
    await new ManagementService(fixtures).RemoveManagedAsync(installation, []);
    True(!Directory.Exists(target)); var removed = Directory.GetDirectories(Path.Combine(fixtures.Data, "removed-sdks")).Single(); True(File.Exists(Path.Combine(removed, "bin", "java.exe")));
});
Test("project .NET selection rejects conflicting global.json", () =>
{
    var directory = Path.Combine(testRoot, "dotnet-project"); Directory.CreateDirectory(directory); File.WriteAllText(Path.Combine(directory, "global.json"), "{\"sdk\":{\"version\":\"8.0.100\"}}");
    var sdk = FixtureTool("dotnet", "dotnet-sdk") with { Version = "10.0.200" };
    var profile = new ProjectProfile { Directory = directory, Bindings = new() { ["dotnet"] = sdk.Id } };
    Throws<InvalidOperationException>(() => new ProjectService(fixtures, new FakeRunner()).BuildEnvironment(profile, [sdk]));
});
AsyncTest("version file replacement requires explicit choice", async () =>
{
    var projectRoot = Path.Combine(testRoot, "版本项目"); Directory.CreateDirectory(projectRoot); var file = Path.Combine(projectRoot, ".python-version"); await File.WriteAllTextAsync(file, "3.11\n");
    var service = new ProjectService(fixtures, new FakeRunner()); var p = new ProjectProfile { Directory = projectRoot };
    await ThrowsAsync<InvalidOperationException>(() => service.WriteVersionFileAsync(p, "python", python with { Version = "3.13.2" }, false)); Equal("3.11\n", await File.ReadAllTextAsync(file));
    await service.WriteVersionFileAsync(p, "python", python with { Version = "3.13.2" }, true); Equal("3.13.2\n", await File.ReadAllTextAsync(file));
});
Test("IDEA registration preserves unrelated XML and backs up", () =>
{
    var dir = Path.Combine(testRoot, "idea-project"); var config = Path.Combine(testRoot, "idea-config"); Directory.CreateDirectory(Path.Combine(dir, ".idea")); Directory.CreateDirectory(Path.Combine(config, "options"));
    File.WriteAllText(Path.Combine(dir, ".idea", "misc.xml"), "<project version=\"4\"><component name=\"KeepMe\" /></project>");
    var service = new ProjectService(fixtures, new FakeRunner()); service.ConfigureIdea(new() { Directory = dir, IdeaConfigDirectory = config }, jdk, true);
    True(File.ReadAllText(Path.Combine(dir, ".idea", "misc.xml")).Contains("KeepMe")); True(File.ReadAllText(Path.Combine(config, "options", "jdk.table.xml")).Contains("<application>"));
    var backup = Directory.GetDirectories(fixtures.Backups, "idea-*").Single(); service.RestoreFiles(backup); True(!File.Exists(Path.Combine(config, "options", "jdk.table.xml")));
});
if (OperatingSystem.IsWindows())
{
    AsyncTest("process runner captures output and scoped cache environment", async () =>
    {
        var runner = new ProcessRunner(fixtures); var script = "[Console]::WriteLine($env:PIP_CACHE_DIR); [Console]::Error.WriteLine('stderr-fixture')";
        var result = await runner.RunAsync(new(ProcessRunner.PowerShellExe, ["-NoProfile", "-NonInteractive", "-EncodedCommand", ProcessRunner.EncodedScript(script)], testRoot));
        True(result.Success, result.Combined); True(result.Output.Contains(Path.Combine(fixtures.Cache, "pip"))); True(result.Error.Contains("stderr-fixture"));
    });
    AsyncTest("process cancellation terminates child process", async () =>
    {
        using var ct = new CancellationTokenSource(300); var runner = new ProcessRunner(fixtures);
        var result = await runner.RunAsync(new(ProcessRunner.PowerShellExe, ["-NoProfile", "-NonInteractive", "-EncodedCommand", ProcessRunner.EncodedScript("Start-Sleep -Seconds 30")], TimeoutSeconds: 60), cancellation: ct.Token);
        True(result.Canceled); True(!result.Success);
    });
    AsyncTest("batch metacharacters remain quoted arguments", async () =>
    {
        var bat = Path.Combine(testRoot, "test 参数.cmd"); await File.WriteAllTextAsync(bat, "@echo off\r\necho %1\r\n"); var runner = new ProcessRunner(fixtures);
        var result = await runner.RunAsync(new(bat, ["value&echo_INJECTED>test.txt"], testRoot));
        True(result.Success, result.Combined); True(result.Output.Contains("value&echo_INJECTED>test.txt")); True(!File.Exists(Path.Combine(testRoot, "test.txt")));
    });
}
AsyncTest("Agent configuration alone is not treated as an installation", async () =>
{
    var root = Path.Combine(testRoot, "agent-config-only"); Directory.CreateDirectory(Path.Combine(root, ".cursor"));
    await File.WriteAllTextAsync(Path.Combine(root, ".cursor", "mcp.json"), "{\"mcpServers\":{\"test\":{\"command\":\"nonexistent-agent-entry\"}}}");
    var scan = await new AgentDiscoveryService(root, root, root, []).ScanAsync([], [], inspectProcesses: false);
    Equal("仅发现配置", scan.Clients.Single(c => c.Id == "cursor").Status);
    Equal(0, scan.Clients.Single(c => c.Id == "cursor").Installations.Count);
    True(scan.McpServers.Single().DependencyStatus.Contains("未找到"));
});
AsyncTest("MCP JSONC scan resolves launchers without executing and excludes secret values", async () =>
{
    var root = Path.Combine(testRoot, "agent-secrets"); Directory.CreateDirectory(Path.Combine(root, ".cursor"));
    var launch = Path.Combine(root, "test-mcp.cmd"); await File.WriteAllTextAsync(launch, "@echo off\r\necho started>SHOULD_NOT_EXIST\r\n");
    await File.WriteAllTextAsync(Path.Combine(root, ".cursor", "mcp.json"), """
    { // JSONC
      "mcpServers": {
        "local": { "command": "test-mcp", "args": ["private-argument-value"], "env": {"API_TOKEN":"private-env-value"} },
        "remote": { "url": "https://private-user:private-password@example.com/secret-path?key=private-query-value", "headers": {"Authorization":"private-header-value"} },
      },
    }
    """);
    var scan = await new AgentDiscoveryService(root, root, root, [root]).ScanAsync([], [], inspectProcesses: false);
    Equal(launch, scan.McpServers.Single(s => s.Name == "local").ResolvedExecutable);
    True(!File.Exists(Path.Combine(root, "SHOULD_NOT_EXIST")));
    var exported = JsonSerializer.Serialize(scan);
    foreach (var secret in new[] { "private-argument-value", "private-env-value", "private-user", "private-password", "secret-path", "private-query-value", "private-header-value" }) True(!exported.Contains(secret), secret);
    Equal("https://example.com/…", scan.McpServers.Single(s => s.Name == "remote").Endpoint);
    Equal("API_TOKEN", scan.McpServers.Single(s => s.Name == "local").EnvironmentNames.Single());
});
AsyncTest("TOML quoted server names multiline arguments and inline environment parse correctly", async () =>
{
    var root = Path.Combine(testRoot, "agent-toml"); Directory.CreateDirectory(Path.Combine(root, ".codex"));
    await File.WriteAllTextAsync(Path.Combine(root, ".codex", "config.toml"), """
    [mcp_servers."with.dot"]
    command = "not-installed"
    args = [
      "hidden-toml-argument",
    ]
    enabled = false
    env = { TOKEN = "hidden-toml-env" }
    [agents.reviewer]
    config_file = "agents/reviewer.toml"
    """);
    var scan = await new AgentDiscoveryService(root, root, root, []).ScanAsync([], [], inspectProcesses: false);
    Equal("with.dot", scan.McpServers.Single().Name); True(!scan.McpServers.Single().Enabled);
    True(scan.Definitions.Any(d => d.Name == "reviewer"));
    var exported = JsonSerializer.Serialize(scan); True(!exported.Contains("hidden-toml-argument")); True(!exported.Contains("hidden-toml-env"));
});
AsyncTest("MCP sources retain global project and Claude local scopes independently", async () =>
{
    var root = Path.Combine(testRoot, "agent-scopes"); var project = Path.Combine(root, "中文 空格项目"); Directory.CreateDirectory(project);
    var localConfig = JsonSerializer.Serialize(new { mcpServers = new { same = new { command = "none" } }, projects = new Dictionary<string, object> { [project] = new { mcpServers = new { same = new { command = "none" } } } } });
    await File.WriteAllTextAsync(Path.Combine(root, ".claude.json"), localConfig);
    await File.WriteAllTextAsync(Path.Combine(project, ".mcp.json"), "{\"mcpServers\":{\"same\":{\"command\":\"none\"}}}");
    var scan = await new AgentDiscoveryService(root, root, root, []).ScanAsync([], [project, project], inspectProcesses: false);
    Equal(3, scan.McpServers.Count); Equal(1, scan.ProjectCount);
    True(scan.McpServers.Any(m => m.Scope == "全局")); Equal(2, scan.McpServers.Count(m => m.Scope.StartsWith("项目")));
});
AsyncTest("Invalid AI config produces a redacted failure while other clients still scan", async () =>
{
    var root = Path.Combine(testRoot, "agent-bad-config"); Directory.CreateDirectory(Path.Combine(root, ".cursor")); Directory.CreateDirectory(Path.Combine(root, ".gemini"));
    await File.WriteAllTextAsync(Path.Combine(root, ".cursor", "mcp.json"), "{\"key\": invalid-secret-value }");
    await File.WriteAllTextAsync(Path.Combine(root, ".gemini", "settings.json"), "{\"mcpServers\":{\"works\":{\"command\":\"none\"}}}");
    var scan = await new AgentDiscoveryService(root, root, root, []).ScanAsync([], [], inspectProcesses: false);
    True(scan.Configurations.Any(c => c.Status.Contains("无法解析"))); Equal("works", scan.McpServers.Single().Name);
    True(!JsonSerializer.Serialize(scan).Contains("invalid-secret-value"));
});
AsyncTest("Agent definitions skills and rules are indexed without reading instruction bodies", async () =>
{
    var root = Path.Combine(testRoot, "agent-definitions"); var agents = Path.Combine(root, ".claude", "agents", "review"); var skill = Path.Combine(root, ".agents", "skills", "test-skill");
    Directory.CreateDirectory(agents); Directory.CreateDirectory(skill);
    await File.WriteAllTextAsync(Path.Combine(agents, "reviewer.md"), "DO_NOT_READ_PRIVATE_PROMPT");
    await File.WriteAllTextAsync(Path.Combine(skill, "SKILL.md"), "DO_NOT_READ_PRIVATE_PROMPT");
    await File.WriteAllTextAsync(Path.Combine(root, "AGENTS.md"), "DO_NOT_READ_PRIVATE_PROMPT");
    var scan = await new AgentDiscoveryService(root, root, root, []).ScanAsync([], [], inspectProcesses: false);
    Equal(3, scan.Definitions.Count); True(scan.Definitions.Any(d => d.Name == "test-skill"));
    True(!JsonSerializer.Serialize(scan).Contains("DO_NOT_READ_PRIVATE_PROMPT"));
});
AsyncTest("Agent scan respects cancellation", async () =>
{
    using var canceled = new CancellationTokenSource(); canceled.Cancel();
    await ThrowsAsync<OperationCanceledException>(() => new AgentDiscoveryService(testRoot, testRoot, testRoot, []).ScanAsync([], [], ct: canceled.Token, inspectProcesses: false));
});
AsyncTest("Agent scan discovers a new PATH entry without executing it or treating stale paths as installed", async () =>
{
    var root = Path.Combine(testRoot, "agent-new-cli"); Directory.CreateDirectory(root);
    var command = Path.Combine(root, "claude.cmd"); await File.WriteAllTextAsync(command, "@echo off\r\necho MUST_NOT_EXECUTE>agent-created\r\n");
    var stale = new ToolInstallation { ToolId = "codex", ExecutablePath = Path.Combine(root, "missing.exe") };
    var scan = await new AgentDiscoveryService(root, root, root, [root]).ScanAsync([stale], [], inspectProcesses: false);
    Equal(command, scan.Clients.Single(c => c.Id == "claude-code").Installations.Single().ExecutablePath);
    Equal("未发现", scan.Clients.Single(c => c.Id == "codex").Status);
    True(!File.Exists(Path.Combine(root, "agent-created")));
});
AsyncTest("OpenCode array commands and VS Code servers use their own schemas", async () =>
{
    var root = Path.Combine(testRoot, "agent-schemas"); var project = Path.Combine(root, "project"); Directory.CreateDirectory(Path.Combine(project, ".vscode"));
    await File.WriteAllTextAsync(Path.Combine(project, "opencode.jsonc"), "{\"mcp\":{\"sample\":{\"type\":\"local\",\"command\":[\"uvx\",\"private-value\"],\"enabled\":false}}}");
    await File.WriteAllTextAsync(Path.Combine(project, ".vscode", "mcp.json"), "{\"servers\":{\"remote\":{\"type\":\"http\",\"url\":\"https://example.com/mcp\"}}}");
    var scan = await new AgentDiscoveryService(root, root, root, []).ScanAsync([], [project], inspectProcesses: false);
    Equal(2, scan.McpServers.Count); True(!scan.McpServers.Single(s => s.ClientId == "opencode").Enabled);
    Equal("http", scan.McpServers.Single(s => s.ClientId == "vscode").Transport); True(!JsonSerializer.Serialize(scan).Contains("private-value"));
});
var passed = 0;
foreach (var test in tests)
{
    try { await test.Run(); Console.WriteLine("PASS " + test.Name); passed++; }
    catch (Exception e) { Console.Error.WriteLine("FAIL " + test.Name + "\n" + e); }
}
Console.WriteLine($"\n{passed}/{tests.Count} passed. Fixtures: {testRoot}");
return passed == tests.Count ? 0 : 1;

static byte[] ZipBytes(params string[] entries)
{
    using var stream = new MemoryStream(); using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, true)) foreach (var name in entries) { using var writer = new StreamWriter(zip.CreateEntry(name).Open()); writer.Write("fixture"); }
    return stream.ToArray();
}
sealed class FakeEnvironmentStore : IEnvironmentStore
{
    private readonly Dictionary<string, EnvironmentValue> values = [];
    public string? FailOnceOnName { get; set; }
    public EnvironmentValue Read(string scope, string name) => values.GetValueOrDefault(scope + "|" + name) ?? new(scope, name, null);
    public void Write(EnvironmentValue value)
    {
        if (FailOnceOnName == value.Name) { FailOnceOnName = null; throw new IOException("fixture write failure"); }
        values[value.Scope + "|" + value.Name] = value;
    }
    public void Broadcast() { }
}
sealed class FakeRunner : ICommandRunner
{
    public CommandSpec? Last { get; private set; }
    public CommandResult Result { get; set; } = new(0, "", "");
    public Task<CommandResult> RunAsync(CommandSpec spec, Action<string>? output = null, CancellationToken cancellation = default) { Last = spec; return Task.FromResult(Result); }
}
sealed class FixtureHttp(byte[] bytes) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes), RequestMessage = request });
}
