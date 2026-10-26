param([Parameter(Mandatory)][string]$PublishDirectory, [Parameter(Mandatory)][string]$Version, [switch]$Archive)
$ErrorActionPreference = 'Stop'
$devHubRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$publishRoot = [IO.Path]::GetFullPath($PublishDirectory)
$buildRoot = [IO.Path]::GetFullPath((Join-Path $devHubRoot '.devhub/build'))
if (-not $publishRoot.StartsWith($buildRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw '发布输入必须位于项目构建目录内。' }
if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw '请输入稳定版三段版本号。' }
$launcher = '@echo off' + "`r`n" + '"%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -NonInteractive -WindowStyle Hidden -ExecutionPolicy Bypass -File "%~dp0Start-DevHub.ps1"' + "`r`n"
[IO.File]::WriteAllText((Join-Path $publishRoot 'Start-DevHub.cmd'), $launcher, [Text.ASCIIEncoding]::new())
$guardPath = Join-Path $publishRoot 'Start-DevHub.ps1'
[IO.File]::WriteAllText($guardPath, [IO.File]::ReadAllText($guardPath), [Text.UTF8Encoding]::new($true))
Copy-Item -LiteralPath (Join-Path $devHubRoot 'THIRD_PARTY_NOTICES.md') -Destination (Join-Path $publishRoot 'THIRD_PARTY_NOTICES.md') -Force
$instructions = "DevHub $Version - Windows x64`r`n`r`n请完整解压到可写目录，双击 Start-DevHub.cmd。程序和随附运行库需要一起保留。`r`n运行数据保存在 .devhub；请保留其中的 SDK、项目配置及备份。`r`n检查更新和恢复入口位于设置页。第三方许可见 THIRD_PARTY_NOTICES.md 和 licenses 目录。`r`n"
[IO.File]::WriteAllText((Join-Path $publishRoot 'START-HERE.txt'), $instructions, [Text.UTF8Encoding]::new($true))
$nugetRoot = [IO.Path]::GetFullPath((Join-Path $devHubRoot '.devhub/cache/nuget/packages'))
$packagePaths = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($assets in Get-ChildItem -LiteralPath (Join-Path $buildRoot 'obj') -Filter 'project.assets.json' -Recurse -File) {
    $graph = Get-Content -LiteralPath $assets.FullName -Raw | ConvertFrom-Json -AsHashtable
    foreach ($library in $graph.libraries.Values) { if ($library.type -eq 'package') { $null = $packagePaths.Add([string]$library.path) } }
}
foreach ($packagePath in $packagePaths) {
    $packageDirectory = [IO.Path]::GetFullPath((Join-Path $nugetRoot $packagePath))
    if (-not $packageDirectory.StartsWith($nugetRoot + [IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)) { throw '依赖包路径越界。' }
    if (-not (Test-Path -LiteralPath $packageDirectory)) { continue }
    foreach ($notice in Get-ChildItem -LiteralPath $packageDirectory -Recurse -File | Where-Object Name -Match '^(licen[cs]e|thirdpartynotices|notice|copying)(\.|$)') {
        if ($notice.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw '许可文件不能是链接。' }
        $relativeNotice = [IO.Path]::GetRelativePath($packageDirectory, $notice.FullName)
        $destination = Join-Path $publishRoot (Join-Path 'licenses/nuget' (Join-Path $packagePath $relativeNotice))
        $null = New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force
        Copy-Item -LiteralPath $notice.FullName -Destination $destination -Force
    }
}
foreach ($required in @('DevHub.App.exe','DevHub.App.dll','DevHub.App.pri','Assets/DevHub.ico','helper/DevHub.Helper.exe','updater/DevHub.Updater.exe')) {
    if (-not (Test-Path -LiteralPath (Join-Path $publishRoot $required))) { throw "缺少发布组件：$required" }
}
$files = @(Get-ChildItem -LiteralPath $publishRoot -Recurse -File | Where-Object Name -ne 'release-manifest.json' | ForEach-Object {
    if ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw '发布目录包含链接文件。' }
    $relative = [IO.Path]::GetRelativePath($publishRoot, $_.FullName).Replace('\','/')
    if ($relative -match '(^|/)\.devhub(/|$)') { throw '发布目录混入运行数据。' }
    [ordered]@{ path=$relative; sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant(); length=$_.Length }
})
$manifest = [ordered]@{ schemaVersion=1; version=$Version; runtime='win-x64'; minimumWindowsBuild=19041; files=$files }
$manifest | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $publishRoot 'release-manifest.json') -Encoding utf8
if ($Archive) {
    $releaseRoot = Join-Path $buildRoot 'releases'; $null = New-Item -ItemType Directory -Path $releaseRoot -Force
    $assetName = "DevHub-$Version-win-x64.zip"; $archivePath = Join-Path $releaseRoot $assetName
    if (Test-Path -LiteralPath $archivePath) { Remove-Item -LiteralPath $archivePath }
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [IO.Compression.ZipFile]::CreateFromDirectory($publishRoot, $archivePath, [IO.Compression.CompressionLevel]::Optimal, $false)
    $hash = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash.ToLowerInvariant()
    [ordered]@{ schemaVersion=1; version=$Version; runtime='win-x64'; assetName=$assetName; sha256=$hash; size=(Get-Item -LiteralPath $archivePath).Length } |
        ConvertTo-Json | Set-Content -LiteralPath (Join-Path $releaseRoot "DevHub-$Version-win-x64.update.json") -Encoding utf8
    "$hash  $assetName" | Set-Content -LiteralPath (Join-Path $releaseRoot "DevHub-$Version-win-x64.sha256") -Encoding ascii
    Write-Output "完整发布包：$archivePath"
}
