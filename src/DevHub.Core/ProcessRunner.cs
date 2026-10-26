using System.Diagnostics;
using System.Text;

namespace DevHub.Core;

public interface ICommandRunner
{
    Task<CommandResult> RunAsync(CommandSpec spec, Action<string>? output = null, CancellationToken cancellation = default);
}

public sealed class ProcessRunner(HubPaths paths) : ICommandRunner
{
    public static string PowerShellLiteral(string value) => "'" + value.Replace("'", "''") + "'";
    public static string EncodedScript(string script) => Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
    public static string PowerShellExe => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");

    public static ProcessStartInfo StartInfo(CommandSpec spec)
    {
        var ext = Path.GetExtension(spec.FileName);
        var batch = ext.Equals(".bat", StringComparison.OrdinalIgnoreCase) || ext.Equals(".cmd", StringComparison.OrdinalIgnoreCase);
        var script = ext.Equals(".ps1", StringComparison.OrdinalIgnoreCase);
        var info = new ProcessStartInfo(batch ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe") : script ? PowerShellExe : spec.FileName)
        {
            UseShellExecute = false,
            CreateNoWindow = !spec.Interactive,
            RedirectStandardOutput = !spec.Interactive,
            RedirectStandardError = !spec.Interactive,
            RedirectStandardInput = !spec.Interactive,
            StandardOutputEncoding = spec.Interactive ? null : Encoding.UTF8,
            StandardErrorEncoding = spec.Interactive ? null : Encoding.UTF8,
            WorkingDirectory = spec.WorkingDirectory ?? Environment.CurrentDirectory
        };
        if (batch)
        {
            if (spec.Arguments.Any(a => a.IndexOfAny(['"', '\r', '\n', '\0']) >= 0)) throw new ArgumentException("批处理参数不能包含双引号或换行。");
            info.Environment["DEVHUB_COMMAND_FILE"] = spec.FileName;
            var arguments = new List<string>();
            for (var i = 0; i < spec.Arguments.Count; i++)
            {
                var name = "DEVHUB_COMMAND_ARG_" + i;
                info.Environment[name] = spec.Arguments[i]; arguments.Add("\"%" + name + "%\"");
            }
            // Expand argument values once, inside quotes, with delayed expansion disabled.
            // In particular %, &, |, < and > in user-supplied package names are never shell syntax.
            info.Arguments = "/d /v:off /s /c \"\"%DEVHUB_COMMAND_FILE%\" " + string.Join(" ", arguments) + "\"";
        }
        else if (script)
        {
            info.ArgumentList.Add("-NoProfile");
            info.ArgumentList.Add("-NonInteractive");
            info.ArgumentList.Add("-EncodedCommand");
            var command = "[Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false); $OutputEncoding = [Console]::OutputEncoding; & " + PowerShellLiteral(spec.FileName) + " " + string.Join(" ", spec.Arguments.Select(PowerShellLiteral)) + "; if ($null -ne $LASTEXITCODE) { exit $LASTEXITCODE }";
            info.ArgumentList.Add(EncodedScript(command));
        }
        else foreach (var argument in spec.Arguments) info.ArgumentList.Add(argument);
        if (spec.Environment is not null)
            foreach (var entry in spec.Environment) info.Environment[entry.Key] = entry.Value;
        return info;
    }

    public async Task<CommandResult> RunAsync(CommandSpec spec, Action<string>? output = null, CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        var env = spec.PreserveHostEnvironment ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) : paths.CacheEnvironment();
        if (spec.Environment is not null) foreach (var pair in spec.Environment) env[pair.Key] = pair.Value;
        var info = StartInfo(spec with { Environment = env });
        var logPath = Path.Combine(paths.Logs, $"{DateTime.UtcNow:yyyyMMdd-HHmmss-ffff}-{Guid.NewGuid():N}.log");
        using var log = new StreamWriter(logPath, false, new UTF8Encoding(false)) { AutoFlush = true };
        var gate = new object();
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        using var process = new Process { StartInfo = info };
        void Capture(string? line, StringBuilder target)
        {
            if (line is null) return;
            lock (gate)
            {
                if (target.Length < 4 * 1024 * 1024) target.AppendLine(line);
                log.WriteLine(line);
            }
            output?.Invoke(line);
        }
        process.OutputDataReceived += (_, e) => Capture(e.Data, stdout);
        process.ErrorDataReceived += (_, e) => Capture(e.Data, stderr);
        log.WriteLine($"[{DateTimeOffset.Now:O}] {spec.Display}");
        if (!process.Start()) throw new InvalidOperationException("无法启动命令。");
        if (spec.Interactive) return new(0, "交互终端已打开。", "");
        process.StandardInput.Close();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(spec.TimeoutSeconds));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation, timeout.Token);
        try
        {
            await process.WaitForExitAsync(linked.Token);
            process.WaitForExit();
            return new(process.ExitCode, stdout.ToString(), stderr.ToString());
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(true); await process.WaitForExitAsync(CancellationToken.None); process.WaitForExit(); } catch (InvalidOperationException) { }
            return new(-1, stdout.ToString(), timeout.IsCancellationRequested && !cancellation.IsCancellationRequested ? "命令执行超时。\n" + stderr : stderr.ToString(), cancellation.IsCancellationRequested);
        }
    }

    public static void OpenDirectory(string directory)
    {
        Directory.CreateDirectory(directory);
        Process.Start(new ProcessStartInfo("explorer.exe") { UseShellExecute = true, ArgumentList = { directory } });
    }

    public static void OpenWebsite(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https") throw new InvalidOperationException("只支持 HTTPS 官网链接。");
        Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
    }
}
