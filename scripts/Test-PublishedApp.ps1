param([Parameter(Mandatory)][string]$PublishDirectory)
$ErrorActionPreference = 'Stop'
$devHubRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$buildRoot = Join-Path $devHubRoot '.devhub/build'
$smokeRoot = Join-Path $buildRoot ('smoke/中文 路径 ' + [guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $smokeRoot -Force
Copy-Item -LiteralPath (Join-Path $PublishDirectory 'release-manifest.json') -Destination (Join-Path $smokeRoot 'release-manifest.json')
$manifest = Get-Content -LiteralPath (Join-Path $smokeRoot 'release-manifest.json') -Raw -Encoding utf8 | ConvertFrom-Json
foreach ($file in $manifest.files) {
    $relative = [string]$file.path
    if ([IO.Path]::IsPathRooted($relative) -or $relative -match '(^|[\/])\.\.([\/]|$)|:|(^|[\/])\.devhub([\/]|$)') { throw '发布清单路径不安全。' }
    $source = Join-Path $PublishDirectory $relative
    if ((Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash -ine $file.sha256) { throw "发布文件校验失败：$relative" }
    $destination = Join-Path $smokeRoot $relative
    $null = New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force
    Copy-Item -LiteralPath $source -Destination $destination
}
$previousRoot = $env:DEVHUB_ROOT; $previousDotnet = $env:DOTNET_ROOT
try {
    $env:DEVHUB_ROOT = $smokeRoot
    $env:DOTNET_ROOT = Join-Path $smokeRoot 'no-installed-dotnet'
    $id = [guid]::NewGuid().ToString('N')
    $executable = Join-Path $smokeRoot 'DevHub.App.exe'
    $process = Start-Process -FilePath $executable -ArgumentList @('--smoke-test','--startup-id',$id) -WorkingDirectory $env:SystemRoot -WindowStyle Hidden -PassThru
    if (-not $process.WaitForExit(45000)) { $process.Kill(); throw '独立发布目录启动超时。' }
    $reportPath = Join-Path $smokeRoot ".devhub/data/startup/$id.json"
    if (-not (Test-Path -LiteralPath $reportPath)) { throw "没有启动就绪记录。进程退出码：$($process.ExitCode)" }
    $report = Get-Content -LiteralPath $reportPath -Raw -Encoding utf8 | ConvertFrom-Json
    if ($report.status -ne 'ready' -or $report.version -ne $manifest.version -or $process.ExitCode -ne 0) { throw '独立发布目录启动检查未通过。' }
    Write-Output "独立发布检查通过：$($report.version)，中文和空格路径，工作目录独立。"
    $report | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $buildRoot 'published-smoke-result.json') -Encoding utf8
    $guardRoot = Join-Path $buildRoot ('guard-smoke/中文 启动 ' + [guid]::NewGuid().ToString('N'))
    $powershell = Join-Path $env:SystemRoot 'System32/WindowsPowerShell/v1.0/powershell.exe'
    $guard = Join-Path $smokeRoot 'Start-DevHub.ps1'
    & $powershell -NoProfile -NonInteractive -ExecutionPolicy Bypass -File $guard -RootDirectory $guardRoot -SmokeTest
    if ($LASTEXITCODE -ne 0) { throw '系统自带 Windows PowerShell 启动保护检查失败。' }
    Write-Output 'Windows PowerShell 5.1 启动入口检查通过。'
} finally { $env:DEVHUB_ROOT = $previousRoot; $env:DOTNET_ROOT = $previousDotnet }
