$ErrorActionPreference = 'Stop'
$devHubRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$devHubBatch = Join-Path $devHubRoot '启动 DevHub.cmd'
# cmd.exe requires CRLF here; LF-only files can silently lose command prefixes.
$devHubBatchText = [IO.File]::ReadAllText($devHubBatch).Replace("`r`n", "`n").Replace("`n", "`r`n")
[IO.File]::WriteAllText($devHubBatch, $devHubBatchText, [Text.UTF8Encoding]::new($false))

$devHubExecutable = Join-Path $devHubRoot '.devhub/build/publish/DevHub/DevHub.App.exe'
if (-not (Test-Path -LiteralPath $devHubExecutable)) { throw '请先发布 DevHub。' }
$devHubShell = New-Object -ComObject WScript.Shell
try {
    $devHubShortcut = $devHubShell.CreateShortcut((Join-Path $devHubRoot 'DevHub.lnk'))
    $devHubShortcut.TargetPath = Join-Path $env:SystemRoot 'System32/WindowsPowerShell/v1.0/powershell.exe'
    $devHubShortcut.Arguments = '-NoProfile -NonInteractive -WindowStyle Hidden -ExecutionPolicy Bypass -File "' + (Join-Path (Split-Path -Parent $devHubExecutable) 'Start-DevHub.ps1') + '" -RootDirectory "' + $devHubRoot + '"'
    $devHubShortcut.WindowStyle = 7
    $devHubShortcut.WorkingDirectory = Split-Path -Parent $devHubExecutable
    $devHubShortcut.IconLocation = $devHubExecutable + ',0'
    $devHubShortcut.Description = 'DevHub 开发环境管理器'
    $devHubShortcut.Save()
} finally {
    if ($devHubShortcut) { $null = [Runtime.InteropServices.Marshal]::FinalReleaseComObject($devHubShortcut) }
    $null = [Runtime.InteropServices.Marshal]::FinalReleaseComObject($devHubShell)
}
Write-Output '已更新启动脚本和 DevHub 快捷方式。'
