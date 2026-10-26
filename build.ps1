param([ValidateSet('Build','Test','Publish','Package','Scan','Providers','Caches','Agents','Doctor')][string]$Task = 'Build', [switch]$NoRestore, [ValidateSet('Debug','Release')][string]$Configuration = 'Release')
$ErrorActionPreference = 'Stop'
$devHubRoot = $PSScriptRoot
$env:DEVHUB_ROOT = $devHubRoot
$env:DOTNET_CLI_HOME = Join-Path $devHubRoot '.devhub/data/dotnet-home'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
$env:NUGET_PACKAGES = Join-Path $devHubRoot '.devhub/cache/nuget/packages'
$env:NUGET_HTTP_CACHE_PATH = Join-Path $devHubRoot '.devhub/cache/nuget/http'
$env:NUGET_SCRATCH = Join-Path $devHubRoot '.devhub/cache/nuget/scratch'
$env:NUGET_PLUGINS_CACHE_PATH = Join-Path $devHubRoot '.devhub/cache/nuget/plugins'
$env:TEMP = Join-Path $devHubRoot '.devhub/cache/tmp'
$env:TMP = $env:TEMP
foreach ($directory in @($env:DOTNET_CLI_HOME,$env:NUGET_PACKAGES,$env:NUGET_HTTP_CACHE_PATH,$env:NUGET_SCRATCH,$env:NUGET_PLUGINS_CACHE_PATH,$env:TEMP)) { New-Item -ItemType Directory -Force -Path $directory | Out-Null }
Push-Location $devHubRoot
try {
    $project = if ($Task -in @('Test','Scan','Providers','Caches','Agents','Doctor')) { 'tests/DevHub.Tests/DevHub.Tests.csproj' } else { 'DevHub.slnx' }
    if (-not $NoRestore) { & dotnet restore $project -p:Platform=x64 --disable-parallel; if ($LASTEXITCODE) { throw '依赖还原失败' } }
    if ($Task -in @('Publish','Package')) {
        $publishDirectory = if ($Task -eq 'Package') { Join-Path $devHubRoot ('.devhub/build/staging/package-' + [guid]::NewGuid().ToString('N')) } else { Join-Path $devHubRoot '.devhub/build/publish/DevHub' }
        & dotnet publish 'src/DevHub.App/DevHub.App.csproj' -c Release -p:Platform=x64 --no-restore -m:1 -nr:false -o $publishDirectory
        if ($LASTEXITCODE) { throw '界面发布失败' }
        & dotnet publish 'src/DevHub.Helper/DevHub.Helper.csproj' -c Release -r win-x64 --self-contained true -o (Join-Path $publishDirectory 'helper') --no-restore -m:1 -nr:false
        if ($LASTEXITCODE) { throw '系统配置辅助程序发布失败' }
        & dotnet publish 'src/DevHub.Updater/DevHub.Updater.csproj' -c Release -r win-x64 --self-contained true -o (Join-Path $publishDirectory 'updater') --no-restore -m:1 -nr:false
        if ($LASTEXITCODE) { throw '更新程序发布失败' }
        $version = ([xml](Get-Content -LiteralPath (Join-Path $devHubRoot 'Directory.Build.props') -Raw)).Project.PropertyGroup.Version
        & (Join-Path $devHubRoot 'scripts/New-ReleasePackage.ps1') -PublishDirectory $publishDirectory -Version $version -Archive:($Task -eq 'Package')
        if ($Task -eq 'Publish') { & (Join-Path $devHubRoot 'scripts/New-DevHubLauncher.ps1') }
    } elseif ($Task -in @('Test','Scan','Providers','Caches','Agents','Doctor')) {
        $runArgs = @('run','--project',$project,'-p:Platform=x64','--configuration',$Configuration,'--no-restore')
        if ($Task -eq 'Scan') { $runArgs += @('--','--scan') }
        if ($Task -eq 'Providers') { $runArgs += @('--','--providers') }
        if ($Task -eq 'Caches') { $runArgs += @('--','--caches') }
        if ($Task -eq 'Agents') { $runArgs += @('--','--agents') }
        if ($Task -eq 'Doctor') { $runArgs += @('--','--doctor') }
        & dotnet @runArgs
        if ($LASTEXITCODE) { throw '验证失败' }
    } else {
        & dotnet build $project -c $Configuration -p:Platform=x64 --no-restore -m:1 -nr:false
        if ($LASTEXITCODE) { throw '构建失败' }
    }
} finally { Pop-Location }
