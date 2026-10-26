param([string]$RootDirectory, [switch]$SmokeTest)
$ErrorActionPreference = 'Stop'
$devHubUpdateLock = $null
try {
    $executable = Join-Path $PSScriptRoot 'DevHub.App.exe'
    if (-not $RootDirectory) {
        $RootDirectory = $PSScriptRoot
        for ($candidate = [IO.DirectoryInfo]::new($PSScriptRoot); $null -ne $candidate; $candidate = $candidate.Parent) {
            if (Test-Path -LiteralPath (Join-Path $candidate.FullName 'DevHub.root')) { $RootDirectory = $candidate.FullName; break }
        }
    }
    $env:DEVHUB_ROOT = [IO.Path]::GetFullPath($RootDirectory)
    $reportDirectory = Join-Path $env:DEVHUB_ROOT '.devhub/data/startup'
    $null = New-Item -ItemType Directory -Path $reportDirectory -Force
    try {
        $devHubUpdateLock = [IO.FileStream]::new((Join-Path $env:DEVHUB_ROOT '.devhub/data/app-update.lock'), [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
    } catch { throw 'DevHub 正在更新，或数据目录不可写。请等待更新完成后再启动。' }
    $updateRoot = Join-Path $env:DEVHUB_ROOT '.devhub/data/app-updates'
    if (Test-Path -LiteralPath $updateRoot) {
        foreach ($updateDirectory in Get-ChildItem -LiteralPath $updateRoot -Directory) {
            $journalFile = Join-Path $updateDirectory.FullName 'journal.json'
            if (-not (Test-Path -LiteralPath $journalFile)) { continue }
            try { $journal = Get-Content -LiteralPath $journalFile -Raw -Encoding utf8 | ConvertFrom-Json } catch { continue }
            if ($journal.status -notin @('applying','awaiting-startup') -or $journal.request.id -ne $updateDirectory.Name) { continue }
            if ([IO.Path]::GetFullPath($journal.request.targetDirectory).TrimEnd('\') -ine $PSScriptRoot.TrimEnd('\')) { continue }
            if ([IO.Path]::GetFullPath($journal.request.root).TrimEnd('\') -ine $env:DEVHUB_ROOT.TrimEnd('\')) { continue }
            Add-Type -AssemblyName System.Windows.Forms
            $answer = [Windows.Forms.MessageBox]::Show('上次 DevHub 更新被中断。是否先恢复旧版程序？SDK 和配置保持独立。', 'DevHub 更新恢复', 'YesNo', 'Warning')
            if ($answer -ne 'Yes') { exit 1 }
            $recovery = Join-Path $updateDirectory.FullName 'runner/DevHub.Updater.exe'
            if (-not (Test-Path -LiteralPath $recovery)) { throw '更新恢复程序缺失，请重新下载完整发布包，并保留现有 .devhub 数据目录。' }
            $devHubUpdateLock.Dispose(); $devHubUpdateLock = $null
            $env:DEVHUB_WAIT_PID = '0'
            Start-Process -FilePath $recovery -ArgumentList @('--recover', ('"' + (Join-Path $updateDirectory.FullName 'request.json') + '"')) -WorkingDirectory $updateDirectory.FullName -WindowStyle Hidden
            exit 0
        }
    }
    if (-not (Test-Path -LiteralPath $executable)) { throw '程序文件不完整，请重新解压完整发布包。' }
    $identifier = [guid]::NewGuid().ToString('N'); $reportFile = Join-Path $reportDirectory "$identifier.json"
    $launchArguments = @('--startup-id',$identifier)
    if ($SmokeTest) { $launchArguments += '--smoke-test' }
    $process = Start-Process -FilePath $executable -ArgumentList $launchArguments -WorkingDirectory $PSScriptRoot -PassThru
    $deadline = [DateTime]::UtcNow.AddSeconds(45)
    while ([DateTime]::UtcNow -lt $deadline) {
        if (Test-Path -LiteralPath $reportFile) {
            $report = Get-Content -LiteralPath $reportFile -Raw -Encoding utf8 | ConvertFrom-Json
            if ($report.status -eq 'ready') { exit 0 }
            if ($report.status -eq 'failed') { exit 1 } # The application presents its own error dialog.
        }
        if ($process.HasExited) { throw "程序提前退出，退出码：$($process.ExitCode)。请检查完整发布目录及 .devhub/logs/app-errors.log。" }
        Start-Sleep -Milliseconds 200
    }
    throw '程序未在 45 秒内完成启动。请检查是否有等待处理的错误窗口，并查看 .devhub/logs。'
} catch {
    Add-Type -AssemblyName System.Windows.Forms
    [Windows.Forms.MessageBox]::Show($_.Exception.Message, 'DevHub 启动失败', 'OK', 'Error') | Out-Null
    exit 1
} finally { if ($null -ne $devHubUpdateLock) { $devHubUpdateLock.Dispose() } }
