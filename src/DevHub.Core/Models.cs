using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DevHub.Core;

public static class JsonDefaults
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };
}

public sealed record ToolCatalog(int SchemaVersion, List<ToolDefinition> Tools);
public sealed record ToolDefinition
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Category { get; init; } = "其他工具";
    public string Description { get; init; } = "";
    public string[] Executables { get; init; } = [];
    public string[] VersionArgs { get; init; } = ["--version"];
    public string? WingetId { get; init; }
    public string? ScoopId { get; init; }
    public string? ChocoId { get; init; }
    public string? Website { get; init; }
    public bool Custom { get; init; }
    public string Glyph { get; init; } = "\uE756";
}

public sealed record ToolInstallation
{
    public string Id { get; init; } = "";
    public string ToolId { get; init; } = "";
    public string Name { get; init; } = "";
    public string Category { get; init; } = "";
    public string ExecutablePath { get; init; } = "";
    public string HomePath { get; init; } = "";
    public string RealPath { get; init; } = "";
    public string Version { get; set; } = "未检测";
    public string Vendor { get; init; } = "";
    public string Source { get; set; } = "本地目录";
    public string? Owner { get; init; }
    public bool IsActive { get; set; }
    public bool Managed { get; init; }
    public bool IsJdk { get; init; }
    public string? PackageId { get; set; }
    public string? PackageManager { get; set; }
    public string? SelectionVersion { get; init; }
    public string Status => Owner is not null ? $"随附于 {Owner}" : IsActive ? "当前入口" : "已发现";
    public string Title => $"{Name}  {Version}";
    public override string ToString() => $"{Name} · {Version} · {HomePath}";
}

public sealed record DownloadRelease
{
    public string ToolId { get; init; } = "";
    public string Vendor { get; init; } = "";
    public string Version { get; init; } = "";
    public string Url { get; init; } = "";
    public string? Sha256 { get; init; }
    public string? Sha512 { get; init; }
    public string? Sha1 { get; init; }
    public string ArchiveType { get; init; } = "zip";
    public string? Channel { get; init; }
    public bool Manual { get; init; }
    public override string ToString() => $"{Vendor} · {Version}{(Channel is null ? "" : " · " + Channel)}{(Manual ? " · 官网下载" : "")}";
}

public sealed record ProjectProfile
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string Directory { get; set; } = "";
    public Dictionary<string, string> Bindings { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public string? VirtualEnvironment { get; set; }
    public string? IdeaConfigDirectory { get; set; }
    public override string ToString() => $"{Name} — {Directory}";
}

public sealed record HubSettings
{
    public int SchemaVersion { get; init; } = 1;
    public string Theme { get; set; } = "Default";
    public List<string> ScanDirectories { get; set; } = [];
    public List<ToolDefinition> CustomTools { get; set; } = [];
    public string PreferredPackageManager { get; set; } = "winget";
    public string? AndroidSdkRoot { get; set; }
    public int ProbeConcurrency { get; set; } = 5;
    public int ProbeTimeoutSeconds { get; set; } = 6;
    public int VersionCacheHours { get; set; } = 24;
    public int AdditionalScanDepth { get; set; } = 4;
    public bool UseEverything { get; set; } = true;
    public string? EverythingCliPath { get; set; }
    public List<string> CacheProjectRoots { get; set; } = [];
}

public sealed record VersionCacheEntry(string Fingerprint, string Version, DateTimeOffset Updated);

public sealed record Diagnostic(string Severity, string Title, string Detail, string? ToolId = null);
public sealed record PackageInfo(string Name, string Version, string? Detail = null)
{
    public override string ToString() => $"{Name}   {Version}{(Detail is null ? "" : "   " + Detail)}";
}

public sealed record CommandSpec(string FileName, IReadOnlyList<string> Arguments, string? WorkingDirectory = null,
    IReadOnlyDictionary<string, string>? Environment = null, int TimeoutSeconds = 120, bool Interactive = false, bool PreserveHostEnvironment = false)
{
    public string Display => FileName + " " + string.Join(" ", Arguments.Select(a => a.Contains(' ') ? '"' + a + '"' : a));
}
public sealed record CommandResult(int ExitCode, string Output, string Error, bool Canceled = false)
{
    public bool Success => ExitCode == 0 && !Canceled;
    public string Combined => (Output + "\n" + Error).Trim();
    public void EnsureSuccess()
    {
        if (!Success) throw new InvalidOperationException(Canceled ? "操作已取消。" : $"命令失败（{ExitCode}）：{Combined}");
    }
}

public sealed class HubTask : ObservableObject
{
    private string status = "等待中", detail = "";
    private double progress;
    public string Id { get; } = Guid.NewGuid().ToString("N");
    public string Title { get; init; } = "";
    public DateTimeOffset Started { get; } = DateTimeOffset.Now;
    public string Status { get => status; set => Set(ref status, value); }
    public string Detail { get => detail; set => Set(ref detail, value); }
    public double Progress { get => progress; set => Set(ref progress, value); }
    public CancellationTokenSource Cancellation { get; } = new();
}

public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new(name));
    }
}
