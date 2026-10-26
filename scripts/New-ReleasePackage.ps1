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
