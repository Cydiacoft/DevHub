using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Diagnostics;
using DevHub.Core;

var paths = HubPaths.Discover();
if (args.Length == 2 && args[0] == "--update-smoke")
{
    var source = Path.GetFullPath(args[1]); var sourceManifest = await AppUpdateService.ReadAndVerifyManifestAsync(source);
    var cases = new List<object>();
    foreach (var scenario in new[] { "success", "startup-failure", "interrupted" })
    {
        var failStartup = scenario == "startup-failure"; var interrupted = scenario == "interrupted";
        var root = new HubPaths(Path.Combine(paths.State, "build", "update-smoke", Guid.NewGuid().ToString("N")));
        var target = Path.Combine(root.Root, "program"); var stage = Path.Combine(root.Cache, "updates", "staged");
        foreach (var directory in new[] { target, stage })
        {
            Directory.CreateDirectory(directory);
            foreach (var file in sourceManifest.Files)
            {
                var destination = Path.Combine(directory, file.Path); Directory.CreateDirectory(Path.GetDirectoryName(destination)!); File.Copy(Path.Combine(source, file.Path), destination);
            }
        }
        var obsolete = Path.Combine(target, "old-only.txt"); await File.WriteAllTextAsync(obsolete, "old-managed-data"); var oldBytes = await File.ReadAllBytesAsync(obsolete);
        var oldManifest = sourceManifest with { Version = "1.0.0", Files = [.. sourceManifest.Files, new("old-only.txt", Convert.ToHexString(SHA256.HashData(oldBytes)), oldBytes.Length)] };
        var version = failStartup ? "99.0.0" : sourceManifest.Version;
        await File.WriteAllTextAsync(Path.Combine(target, "release-manifest.json"), JsonSerializer.Serialize(oldManifest, JsonDefaults.Options));
        await File.WriteAllTextAsync(Path.Combine(stage, "release-manifest.json"), JsonSerializer.Serialize(sourceManifest with { Version = version }, JsonDefaults.Options));
        var protectedData = Path.Combine(root.Sdks, "must-survive.txt"); await File.WriteAllTextAsync(protectedData, "keep-sdk");
        var request = new UpdateRequest(root.Root, target, stage, version, 0, Guid.NewGuid().ToString("N"));
        var requestFile = await UpdateTransaction.PrepareRunnerAsync(request);
        if (interrupted) { await UpdateTransaction.ApplyFilesAsync(request); File.Delete(Path.Combine(target, "DevHub.App.exe")); }
        var info = new ProcessStartInfo(Path.Combine(UpdateTransaction.WorkDirectory(request), "runner", "DevHub.Updater.exe")) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
        info.ArgumentList.Add(interrupted ? "--recover" : "--apply"); info.ArgumentList.Add(requestFile); info.ArgumentList.Add("--test-mode"); info.Environment.Remove("DEVHUB_WAIT_PID");
        using var process = Process.Start(info)!;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        try { await process.WaitForExitAsync(timeout.Token); } catch { process.Kill(true); throw; }
        var journal = JsonSerializer.Deserialize<UpdateJournal>(await File.ReadAllTextAsync(Path.Combine(UpdateTransaction.WorkDirectory(request), "journal.json")), JsonDefaults.Options)!;
        if (journal.Status != (failStartup || interrupted ? "rolled-back" : "completed") || await File.ReadAllTextAsync(protectedData) != "keep-sdk" || File.Exists(obsolete) != (failStartup || interrupted) || !File.Exists(Path.Combine(target, "DevHub.App.exe"))) throw new InvalidOperationException("更新程序独立进程检查失败：" + journal.Status);
        cases.Add(new { Scenario = scenario, journal.Status, ExitCode = process.ExitCode, Root = root.Root });
        Console.WriteLine($"Updater process: {scenario} = {journal.Status}");
    }
    await File.WriteAllTextAsync(Path.Combine(paths.State, "build", "update-smoke-result.json"), JsonSerializer.Serialize(cases, JsonDefaults.Options)); return 0;
}
if (args.Contains("--doctor"))
{
    if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("新终端环境诊断需要 Windows。");
    var store = new JsonStore(paths); var inventory = await store.LoadAsync("inventory", () => new List<ToolInstallation>());
    var runner = new ProcessRunner(paths); var doctor = new EnvironmentDoctor(paths, runner);
    var evidence = new List<EnvironmentEvidence>();
    foreach (var context in new[] { (Name: "当前进程", Environment: EnvironmentDoctor.CurrentEnvironment()), (Name: "新终端", Environment: new EnvironmentService(paths, new WindowsEnvironmentStore()).FreshEnvironment()) })
    {
        var report = await doctor.InspectAsync(context.Name, context.Environment, DiscoveryService.BuiltInCatalog().Tools, inventory);
        evidence.Add(report); Console.WriteLine($"{report.Name}: {report.Commands.Count} commands, {report.Issues.Count} issues");
    }
    await File.WriteAllTextAsync(Path.Combine(paths.Logs, "environment-report.json"), JsonSerializer.Serialize(evidence, JsonDefaults.Options)); return 0;
}
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
    var request = new EnvironmentService(fixtures, fake).PlanSwitch(jdk, "Machine");
    var value = request.Changes.First(c => c.After.Name == "Path").After.Value!;
    True(value.StartsWith(Path.GetDirectoryName(jdk.ExecutablePath)!)); True(value.Contains(@"C:\Tools")); True(value.Contains(@"C:\Java\old\bin"));
});
Test("Java switch reconciles existing user JAVA_HOME", () =>
{
    var fake = new FakeEnvironmentStore(); fake.Write(new("User", "JAVA_HOME", @"C:\Old"));
    var req = new EnvironmentService(fixtures, fake).PlanSwitch(jdk, "Machine");
    Equal(jdk.HomePath, req.Changes.Single(c => c.After.Name == "JAVA_HOME" && c.After.Scope == "User").After.Value);
});
Test("environment transaction persists and backs up", () =>
{
    var fake = new FakeEnvironmentStore(); var service = new EnvironmentService(fixtures, fake); var req = service.PlanSwitch(jdk, "Machine");
    EnvironmentService.ApplyTransaction(req, fake, fixtures); Equal(jdk.HomePath, fake.Read("Machine", "JAVA_HOME").Value); True(Directory.GetFiles(fixtures.Backups, "environment-*.json").Length > 0);
});
Test("environment transaction refuses stale preview", () =>
{
    var fake = new FakeEnvironmentStore(); var req = new EnvironmentService(fixtures, fake).PlanSwitch(jdk, "Machine"); fake.Write(new("Machine", "Path", "changed"));
    Throws<InvalidOperationException>(() => EnvironmentService.ApplyTransaction(req, fake, fixtures)); Equal("changed", fake.Read("Machine", "Path").Value);
});
Test("environment transaction rolls back failed write", () =>
{
    var fake = new FakeEnvironmentStore(); fake.Write(new("Machine", "Path", "original")); var req = new EnvironmentService(fixtures, fake).PlanSwitch(jdk, "Machine");
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
    var toolDirectory = Path.Combine(testRoot, "fake-uv-bin"); Directory.CreateDirectory(toolDirectory); await File.WriteAllTextAsync(Path.Combine(toolDirectory, "uv.exe"), "fixture, never executed");
    var previousPath = Environment.GetEnvironmentVariable("PATH");
    try
    {
        Environment.SetEnvironmentVariable("PATH", toolDirectory);
        var runner = new FakeRunner(); await service.ClearAsync(entry, runner);
        Equal(Path.Combine(toolDirectory, "uv.exe"), runner.Last!.FileName);
        True(runner.Last.Arguments.Contains("prune")); True(runner.Last.Arguments.Contains("--cache-dir")); True(runner.Last.Arguments.Contains(dir)); True(File.Exists(file));
    }
    finally { Environment.SetEnvironmentVariable("PATH", previousPath); }
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
Test("default environment repair changes only the current user", () =>
{
    var fake = new FakeEnvironmentStore(); fake.Write(new("Machine", "Path", "machine-keep")); fake.Write(new("Machine", "JAVA_HOME", "machine-jdk"));
    var request = new EnvironmentService(fixtures, fake).PlanSwitch(jdk); Equal("User", request.Scope); True(request.Changes.All(c => c.Before.Scope == "User"));
    EnvironmentService.ApplyTransaction(request, fake, fixtures); Equal("machine-jdk", fake.Read("Machine", "JAVA_HOME").Value); Equal(jdk.HomePath, fake.Read("User", "JAVA_HOME").Value);
});
Test("repair preview exposes system PATH shadowing and never writes", () =>
{
    var fake = new FakeEnvironmentStore(); var other = FixtureTool("java", "shadowing-java"); fake.Write(new("Machine", "Path", Path.GetDirectoryName(other.ExecutablePath)));
    var service = new EnvironmentService(fixtures, fake); var request = service.PlanSwitch(jdk); var preview = service.PreviewEnvironment(request);
    Equal(other.ExecutablePath, EnvironmentDoctor.Resolve("java", preview).First().Path); Equal<string?>(null, fake.Read("User", "JAVA_HOME").Value);
});
Test("command evidence preserves PATH order aliases and duplicate directories", () =>
{
    var first = FixtureTool("node", "first path"); var second = FixtureTool("node", "second 中文 path");
    var env = new Dictionary<string, string> { ["PATH"] = Path.GetDirectoryName(first.ExecutablePath) + ";" + Path.GetDirectoryName(second.ExecutablePath) + ";" + Path.GetDirectoryName(first.ExecutablePath), ["PATHEXT"] = ".EXE;.CMD" };
    var result = EnvironmentDoctor.Resolve("node", env); Equal(2, result.Count); Equal(first.ExecutablePath, result[0].Path); Equal(second.ExecutablePath, result[1].Path);
});
AsyncTest("recovering config preserves corrupt input before using defaults", async () =>
{
    var warnings = new List<string>(); var store = new JsonStore(fixtures); var input = Path.Combine(fixtures.Data, "broken-startup.json");
    await File.WriteAllTextAsync(input, "{broken-startup-private"); var settings = await store.LoadRecoveringAsync("broken-startup", () => new HubSettings(), warnings);
    True(settings.CheckForUpdates); Equal(1, warnings.Count); await store.SaveAsync("broken-startup", settings);
    var backup = Directory.GetFiles(fixtures.Backups, "corrupt-broken-startup-*.json").Single(); Equal("{broken-startup-private", await File.ReadAllTextAsync(backup));
});
Test("startup readiness reports are atomic and constrained to a token", () =>
{
    var id = Guid.NewGuid().ToString("N"); StartupLifecycle.Report(fixtures, id, "ready");
    var report = JsonSerializer.Deserialize<StartupReport>(File.ReadAllText(StartupLifecycle.ReportPath(fixtures, id)), JsonDefaults.Options)!;
    Equal(Environment.ProcessId, report.ProcessId); Equal("ready", report.Status); Throws<ArgumentException>(() => StartupLifecycle.ReportPath(fixtures, "../outside"));
});
Test("structured project declarations do not use substring matching", () =>
{
    var directory = Path.Combine(testRoot, "declarations"); Directory.CreateDirectory(directory); File.WriteAllText(Path.Combine(directory, ".python-version"), "13.10.1");
    var install = python with { Version = "3.10.1" }; var profile = new ProjectProfile { Directory = directory, Bindings = new() { ["python"] = install.Id } };
    True(ProjectDeclarations.Inspect(profile, [install]).Any(i => i.Severity == "警告"));
    File.WriteAllText(Path.Combine(directory, ".python-version"), "3.10"); True(!ProjectDeclarations.Inspect(profile, [install]).Any(i => i.Severity == "警告"));
});
Test("malformed SDK declaration is reported without crashing diagnostics", () =>
{
    var directory = Path.Combine(testRoot, "bad-declarations"); Directory.CreateDirectory(directory); File.WriteAllText(Path.Combine(directory, "global.json"), "{}");
    var install = jdk with { ToolId = "dotnet" }; var profile = new ProjectProfile { Directory = directory, Bindings = new() { ["dotnet"] = install.Id } };
    True(ProjectDeclarations.Inspect(profile, [install]).Any(i => i.Title.Contains("无法解析")));
});
Test("app semantic versions compare numeric components and prereleases", () =>
{
    True(AppVersion.Compare("1.10.0", "1.9.9") > 0); True(AppVersion.Compare("1.1.0", "1.1.0-rc.2") > 0); True(AppVersion.Compare("1.1.0-rc.10", "1.1.0-rc.2") > 0);
    Equal(0, AppVersion.Compare("v1.1.0+build", "1.1.0")); True(!AppVersion.IsStable("1.1.0-beta"));
});
AsyncTest("update check handles no releases and rate limiting", async () =>
{
    using var noRelease = new AppUpdateService(fixtures, new RoutingHttp(_ => new HttpResponseMessage(HttpStatusCode.NotFound)));
    Equal<AvailableUpdate?>(null, await noRelease.CheckAsync("1.1.0"));
    using var limited = new AppUpdateService(fixtures, new RoutingHttp(_ => new HttpResponseMessage(HttpStatusCode.Forbidden)));
    await ThrowsAsync<InvalidOperationException>(() => limited.CheckAsync("1.1.0"));
});
AsyncTest("update check validates repository version asset names sizes and digests", async () =>
{
    var descriptor = new UpdateDescriptor(1, "1.2.0", "win-x64", "DevHub-1.2.0-win-x64.zip", new string('a', 64), 128);
    var release = JsonSerializer.Serialize(new { draft = false, prerelease = false, tag_name = "v1.2.0", body = "notes", assets = new[] {
        new { name = "DevHub-1.2.0-win-x64.update.json", browser_download_url = "https://github.com/Cydiacoft/DevHub/releases/download/v1.2.0/DevHub-1.2.0-win-x64.update.json", size = 100 },
        new { name = descriptor.AssetName, browser_download_url = "https://github.com/Cydiacoft/DevHub/releases/download/v1.2.0/" + descriptor.AssetName, size = 128 } } });
    using var service = new AppUpdateService(fixtures, new RoutingHttp(request => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(request.RequestUri!.AbsolutePath.EndsWith("latest") ? release : JsonSerializer.Serialize(descriptor, JsonDefaults.Options)) }));
    Equal("1.2.0", (await service.CheckAsync("1.1.0"))!.Descriptor.Version);
    Equal<AvailableUpdate?>(null, await service.CheckAsync("1.2.0"));
});
Test("update paths reject caches SDKs traversal devices streams and aliases", () =>
{
    foreach (var path in new[] { "../SDK", "C:/file", ".devhub/data/settings.json", "a/.devhub/sdks/x", "thing:stream", "a/CON.txt", "a/../b", "a./x", "a /x", "a|b", "DevHub.root" }) Throws<InvalidDataException>(() => AppUpdateService.SafeRelativePath(path));
});
AsyncTest("update ZIP rejects traversal symlinks duplicate names and bad hashes", async () =>
{
    var archive = Path.Combine(testRoot, "bad-update.zip"); await File.WriteAllBytesAsync(archive, ZipBytes("../escape"));
    await ThrowsAsync<InvalidDataException>(() => AppUpdateService.StageArchiveAsync(archive, Path.Combine(testRoot, "bad-update-stage"), "1.2.0"));
    True(!Directory.Exists(Path.Combine(testRoot, "bad-update-stage")));
    await File.WriteAllBytesAsync(archive, ZipBytes("DevHub.App.exe", "devhub.app.exe"));
    await ThrowsAsync<InvalidDataException>(() => AppUpdateService.StageArchiveAsync(archive, Path.Combine(testRoot, "duplicate-update-stage"), "1.2.0"));
});
AsyncTest("update download cancellation and checksum failure never change installed files", async () =>
{
    var descriptor = new UpdateDescriptor(1, "1.2.0", "win-x64", "DevHub-1.2.0-win-x64.zip", new string('a', 64), 3);
    using var service = new AppUpdateService(fixtures, new RoutingHttp(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) }));
    await ThrowsAsync<InvalidDataException>(() => service.DownloadAsync(new(descriptor, "v1.2.0", "", "")));
    using var canceled = new CancellationTokenSource(); canceled.Cancel();
    await ThrowsAsync<OperationCanceledException>(() => service.DownloadAsync(new(descriptor, "v1.2.0", "", ""), ct: canceled.Token));
});
AsyncTest("verified update is staged and rejects modifications or unlisted files", async () =>
{
    var release = Path.Combine(testRoot, "verified-release"); await MakeRelease(release, "1.2.0", "new");
    Equal("1.2.0", (await AppUpdateService.ReadAndVerifyManifestAsync(release)).Version);
    var archive = Path.Combine(testRoot, "good-update.zip"); ZipFile.CreateFromDirectory(release, archive);
    var staged = Path.Combine(testRoot, "good-update-stage"); await AppUpdateService.StageArchiveAsync(archive, staged, "1.2.0");
    await File.WriteAllTextAsync(Path.Combine(staged, "unlisted.txt"), "unlisted"); await ThrowsAsync<InvalidDataException>(() => AppUpdateService.ReadAndVerifyManifestAsync(staged));
    await File.WriteAllTextAsync(Path.Combine(release, "DevHub.App.dll"), "modified"); await ThrowsAsync<InvalidDataException>(() => AppUpdateService.ReadAndVerifyManifestAsync(release));
});
AsyncTest("portable update changes only managed program files and can restore after interruption", async () =>
{
    var directory = Path.Combine(testRoot, "portable 中文 app"); await MakeRelease(directory, "1.1.0", "old", "obsolete.dll");
    var portable = new HubPaths(directory); var protectedFile = Path.Combine(portable.Sdks, "keep-sdk.txt"); await File.WriteAllTextAsync(protectedFile, "sdk");
    var config = Path.Combine(portable.Data, "settings.json"); await File.WriteAllTextAsync(config, "settings");
    var unrelated = Path.Combine(directory, "user-notes.txt"); await File.WriteAllTextAsync(unrelated, "keep");
    var stage = Path.Combine(portable.Cache, "updates", "staged"); await MakeRelease(stage, "1.2.0", "new");
    var request = new UpdateRequest(directory, directory, stage, "1.2.0", 0, Guid.NewGuid().ToString("N"));
    var journal = await UpdateTransaction.ApplyFilesAsync(request);
    Equal("new", await File.ReadAllTextAsync(Path.Combine(directory, "DevHub.App.dll"))); True(!File.Exists(Path.Combine(directory, "obsolete.dll")));
    Equal("sdk", await File.ReadAllTextAsync(protectedFile)); Equal("settings", await File.ReadAllTextAsync(config)); Equal("keep", await File.ReadAllTextAsync(unrelated));
    await UpdateTransaction.RollbackAsync(journal, "simulated interruption");
    Equal("old", await File.ReadAllTextAsync(Path.Combine(directory, "DevHub.App.dll"))); True(File.Exists(Path.Combine(directory, "obsolete.dll")));
    Equal("sdk", await File.ReadAllTextAsync(protectedFile)); Equal("settings", await File.ReadAllTextAsync(config));
});
AsyncTest("update refuses to overwrite untracked user files and occupied files", async () =>
{
    var directory = Path.Combine(testRoot, "update-file-conflict"); await MakeRelease(directory, "1.1.0", "old"); var root = new HubPaths(directory);
    await File.WriteAllTextAsync(Path.Combine(directory, "extra.dll"), "user file");
    var stage = Path.Combine(root.Cache, "updates", "staged"); await MakeRelease(stage, "1.2.0", "new", "extra.dll");
    var request = new UpdateRequest(directory, directory, stage, "1.2.0", 0, Guid.NewGuid().ToString("N"));
    await ThrowsAsync<InvalidOperationException>(() => UpdateTransaction.ApplyFilesAsync(request)); Equal("old", await File.ReadAllTextAsync(Path.Combine(directory, "DevHub.App.dll")));
    await MakeRelease(stage, "1.2.0", "new"); File.Delete(Path.Combine(stage, "extra.dll")); // regenerate manifest without the conflicting path
    using var locked = new FileStream(Path.Combine(directory, "DevHub.App.dll"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    await ThrowsAsync<IOException>(() => UpdateTransaction.ApplyFilesAsync(request));
});
if (OperatingSystem.IsWindows())
{
    AsyncTest("native no-profile command resolution does not execute discovered commands or export secret variables", async () =>
    {
        var directory = Path.Combine(testRoot, "doctor-entry 中文"); Directory.CreateDirectory(directory); var command = Path.Combine(directory, "doctor-test.cmd");
        await File.WriteAllTextAsync(command, "@echo off\r\necho WRONG>must-not-run\r\n");
        var environment = EnvironmentDoctor.CurrentEnvironment(); environment["PATH"] = directory; environment["PRIVATE_API_TOKEN"] = "do-not-export-this-value";
        var catalog = new List<ToolDefinition> { new() { Id = "doctor-test", Name = "Doctor test", Executables = ["doctor-test.cmd"] } };
        var evidence = await new EnvironmentDoctor(fixtures, new ProcessRunner(fixtures)).InspectAsync("test", environment, catalog, []);
        Equal(command, evidence.Commands.First(t => t.ToolId == "doctor-test").SelectedPath); True(!File.Exists(Path.Combine(testRoot, "must-not-run"))); True(!JsonSerializer.Serialize(evidence).Contains("do-not-export-this-value"));
    });
}
AsyncTest("updater rejects downgrades and same-version replacements before writes", async () =>
{
    var directory = Path.Combine(testRoot, "downgrade"); await MakeRelease(directory, "1.2.0", "current"); var root = new HubPaths(directory);
    var stage = Path.Combine(root.Cache, "updates", "staged"); await MakeRelease(stage, "1.1.0", "older");
    var request = new UpdateRequest(directory, directory, stage, "1.1.0", 0, Guid.NewGuid().ToString("N"));
    await ThrowsAsync<InvalidOperationException>(() => UpdateTransaction.ApplyFilesAsync(request)); Equal("current", await File.ReadAllTextAsync(Path.Combine(directory, "DevHub.App.dll")));
});
AsyncTest("updater rejects ZIP symlinks without leaving an installation", async () =>
{
    var archive = Path.Combine(testRoot, "symlink-update.zip");
    using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create)) { var link = zip.CreateEntry("linked"); link.ExternalAttributes = unchecked((int)0xA1FF0000); using var writer = new StreamWriter(link.Open()); writer.Write("../outside"); }
    var stage = Path.Combine(testRoot, "symlink-update-stage"); await ThrowsAsync<InvalidDataException>(() => AppUpdateService.StageArchiveAsync(archive, stage, "1.2.0")); True(!Directory.Exists(stage));
});
Test("locked project declarations do not prevent the overview from opening", () =>
{
    var directory = Path.Combine(testRoot, "locked declarations"); Directory.CreateDirectory(directory);
    var file = Path.Combine(directory, "gradle.properties"); File.WriteAllText(file, "org.gradle.java.home=C:/test");
    using var locked = new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    True(ProjectDeclarations.Inspect(new ProjectProfile { Directory = directory }, []).Any(i => i.Title.Contains("无法读取")));
});
if (OperatingSystem.IsWindows() && DiscoveryService.ResolveCommand(["python.exe"]) is { } metadataPython && !metadataPython.Contains("\\WindowsApps\\", StringComparison.OrdinalIgnoreCase))
{
    AsyncTest("Python metadata probes do not import modules from the project or PYTHONPATH", async () =>
    {
        var directory = Path.Combine(testRoot, "python-project-imports"); Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, "json.py"), "raise RuntimeError('project json module executed')");
        var environment = EnvironmentDoctor.CurrentEnvironment(); environment["PYTHONPATH"] = directory;
        var trace = new CommandTrace("python", "Python", "python", [new(metadataPython, metadataPython, 1, "test")], null, "test");
        var result = await new EnvironmentDoctor(fixtures, new ProcessRunner(fixtures)).ProbeAsync(trace, environment, directory);
        using var json = JsonDocument.Parse(result); True(json.RootElement.GetProperty("executable").GetString()!.Length > 0);
        True(!Directory.Exists(Path.Combine(directory, "__pycache__"))); Equal(directory, environment["PYTHONPATH"]);
    });
}
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
static async Task MakeRelease(string directory, string version, string content, params string[] extra)
{
    Directory.CreateDirectory(directory); var files = new List<ReleaseFile>();
    foreach (var path in AppUpdateService.RequiredFiles.Concat(extra))
    {
        var full = Path.Combine(directory, path); Directory.CreateDirectory(Path.GetDirectoryName(full)!); await File.WriteAllTextAsync(full, content);
        var bytes = await File.ReadAllBytesAsync(full); files.Add(new(path, Convert.ToHexString(SHA256.HashData(bytes)), bytes.Length));
    }
    await File.WriteAllTextAsync(Path.Combine(directory, "release-manifest.json"), JsonSerializer.Serialize(new ReleaseManifest(1, version, "win-x64", 19041, files), JsonDefaults.Options));
}
sealed class RoutingHttp(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) { cancellationToken.ThrowIfCancellationRequested(); return Task.FromResult(respond(request)); }
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
