using System.Diagnostics;
using System.Text.Json;

namespace DevHub.Core;

public sealed record UpdateRequest(string Root, string TargetDirectory, string StageDirectory, string Version, int ProcessId, string Id);
public sealed record UpdateJournal(UpdateRequest Request, string Status, List<string> Existed, List<string> Touched, string? Error = null);

public static class UpdateTransaction
{
    public static string WorkDirectory(UpdateRequest request) => Path.Combine(new HubPaths(request.Root).Data, "app-updates", request.Id);
    public static string BackupDirectory(UpdateRequest request) => Path.Combine(new HubPaths(request.Root).Backups, "app-updates", request.Id);
    public static void Validate(UpdateRequest request, bool requireTarget = true)
    {
        if (!Guid.TryParseExact(request.Id, "N", out _) || !AppVersion.IsStable(request.Version)) throw new InvalidDataException("更新请求无效。");
        var paths = new HubPaths(request.Root);
        HubPaths.RequireSafeChild(request.StageDirectory, Path.Combine(paths.Cache, "updates"));
        HubPaths.RequireSafeChild(WorkDirectory(request), Path.Combine(paths.Data, "app-updates"));
        if ((requireTarget && (!File.Exists(Path.Combine(request.TargetDirectory, "DevHub.App.exe")) || !File.Exists(Path.Combine(request.TargetDirectory, "DevHub.App.dll")))) || File.Exists(Path.Combine(request.TargetDirectory, "DevHub.slnx"))) throw new InvalidDataException("目标不是独立的 DevHub 程序目录。");
        if (HubPaths.IsWithin(request.StageDirectory, request.TargetDirectory) && !DiscoveryService.SamePath(request.TargetDirectory, request.Root)) throw new InvalidDataException("更新暂存目录不能位于程序目录内。");
        if (new DirectoryInfo(request.TargetDirectory).Attributes.HasFlag(FileAttributes.ReparsePoint)) throw new InvalidDataException("程序目录是链接，无法自动更新。");
    }
    public static async Task<string> PrepareRunnerAsync(UpdateRequest request, CancellationToken ct = default)
    {
        Validate(request);
        var manifest = await AppUpdateService.ReadAndVerifyManifestAsync(request.StageDirectory, ct);
        if (manifest.Version != request.Version) throw new InvalidDataException("暂存版本不匹配。");
        EnsureNoOtherInstances(request.TargetDirectory, Environment.ProcessId);
        var work = WorkDirectory(request); Directory.CreateDirectory(work); var runner = Path.Combine(work, "runner");
        foreach (var file in manifest.Files.Where(f => f.Path.Replace('\\', '/').StartsWith("updater/", StringComparison.OrdinalIgnoreCase)))
        {
            var relative = file.Path.Replace('\\', '/')[8..]; var destination = Path.Combine(runner, relative);
            HubPaths.RequireSafeChild(destination, runner); Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(Path.Combine(request.StageDirectory, file.Path), destination, false);
        }
        var requestPath = Path.Combine(work, "request.json"); await File.WriteAllTextAsync(requestPath, JsonSerializer.Serialize(request, JsonDefaults.Options), ct);
        return requestPath;
    }
    public static async Task<UpdateJournal> ApplyFilesAsync(UpdateRequest request, CancellationToken ct = default)
    {
        Validate(request);
        var manifest = await AppUpdateService.ReadAndVerifyManifestAsync(request.StageDirectory, ct);
        if (manifest.Version != request.Version) throw new InvalidDataException("更新版本不匹配。");
        var oldFile = Path.Combine(request.TargetDirectory, "release-manifest.json");
        if (!File.Exists(oldFile)) throw new InvalidOperationException("当前程序没有发布清单，请先手动安装完整的新版本。");
        var old = JsonSerializer.Deserialize<ReleaseManifest>(await File.ReadAllTextAsync(oldFile, ct), JsonDefaults.Options) ?? throw new InvalidDataException("旧版发布清单损坏。");
        if (old.SchemaVersion != 1 || old.Runtime != "win-x64" || old.Files.Count is 0 or > 20000) throw new InvalidDataException("旧版清单无效。");
        if (AppVersion.Compare(request.Version, old.Version) <= 0) throw new InvalidOperationException("自动更新只接受更高版本。");
        var oldPaths = old.Files.Select(f => f.Path.Replace('\\', '/')).Append("release-manifest.json").ToHashSet(StringComparer.OrdinalIgnoreCase);
        var newPaths = manifest.Files.Select(f => f.Path.Replace('\\', '/')).Append("release-manifest.json").ToHashSet(StringComparer.OrdinalIgnoreCase);
        var touched = oldPaths.Union(newPaths, StringComparer.OrdinalIgnoreCase).ToList();
        var existed = new List<string>(); var backup = BackupDirectory(request); Directory.CreateDirectory(backup);
        if (File.Exists(Path.Combine(WorkDirectory(request), "journal.json"))) throw new InvalidOperationException("已有更新事务，请先恢复。");
        foreach (var relative in touched)
        {
            AppUpdateService.SafeRelativePath(relative);
            var destination = Path.Combine(request.TargetDirectory, relative); HubPaths.RequireSafeChild(destination, request.TargetDirectory);
            if (File.Exists(destination))
            {
                if (!oldPaths.Contains(relative) || (File.GetAttributes(destination) & FileAttributes.ReparsePoint) != 0) throw new InvalidOperationException("更新会覆盖未管理的文件或链接：" + relative);
                // Opening for exclusive read detects occupied files before any target is changed.
                using (var input = new FileStream(destination, FileMode.Open, FileAccess.Read, FileShare.None))
                {
                    var saved = Path.Combine(backup, relative); Directory.CreateDirectory(Path.GetDirectoryName(saved)!);
                    using var output = File.Create(saved); input.CopyTo(output);
                }
                existed.Add(relative);
            }
        }
        var journal = new UpdateJournal(request, "applying", existed, touched); await SaveAsync(journal);
        try
        {
            foreach (var relative in newPaths)
            {
                ct.ThrowIfCancellationRequested(); var target = Path.Combine(request.TargetDirectory, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                var temporary = target + ".devhub-update-" + request.Id;
                File.Copy(Path.Combine(request.StageDirectory, relative), temporary, false); File.Move(temporary, target, true);
            }
            foreach (var obsolete in oldPaths.Except(newPaths, StringComparer.OrdinalIgnoreCase)) File.Delete(Path.Combine(request.TargetDirectory, obsolete));
            journal = journal with { Status = "awaiting-startup" }; await SaveAsync(journal); return journal;
        }
        catch (Exception error) { await RollbackAsync(journal, error.GetType().Name); throw; }
    }
    public static async Task RollbackAsync(UpdateJournal journal, string? error = null)
    {
        var request = journal.Request; Validate(request, false);
        // Only operate on paths from this persisted transaction, never recursively on the app root.
        foreach (var relative in journal.Touched)
        {
            AppUpdateService.SafeRelativePath(relative);
            var target = Path.Combine(request.TargetDirectory, relative); HubPaths.RequireSafeChild(target, request.TargetDirectory);
            if (journal.Existed.Contains(relative, StringComparer.OrdinalIgnoreCase))
            {
                var source = Path.Combine(BackupDirectory(request), relative); HubPaths.RequireSafeChild(source, BackupDirectory(request));
                File.Copy(source, target, true);
            }
            else if (File.Exists(target)) File.Delete(target);
            var temporary = target + ".devhub-update-" + request.Id; if (File.Exists(temporary)) File.Delete(temporary);
        }
        await SaveAsync(journal with { Status = "rolled-back", Error = error });
    }
    public static async Task CompleteAsync(UpdateJournal journal) => await SaveAsync(journal with { Status = "completed" });
    public static async Task SaveAsync(UpdateJournal journal)
    {
        var work = WorkDirectory(journal.Request); Directory.CreateDirectory(work); var file = Path.Combine(work, "journal.json");
        await File.WriteAllTextAsync(file + ".tmp", JsonSerializer.Serialize(journal, JsonDefaults.Options)); File.Move(file + ".tmp", file, true);
    }
    public static void EnsureNoOtherInstances(string targetDirectory, int allowedProcess)
    {
        foreach (var process in Process.GetProcessesByName("DevHub.App"))
        {
            using (process)
            {
                if (process.Id == allowedProcess) continue;
                try
                {
                    if (process.MainModule?.FileName is { } file && DiscoveryService.SamePath(Path.GetDirectoryName(file)!, targetDirectory)) throw new InvalidOperationException("同一程序目录还有其他 DevHub 窗口，请先关闭。");
                }
                catch (System.ComponentModel.Win32Exception) { throw new InvalidOperationException("无法确认其他 DevHub 进程的目录，请先关闭其他 DevHub 窗口。"); }
                catch (ArgumentException) { }
            }
        }
    }
}
