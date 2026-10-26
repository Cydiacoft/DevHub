$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$devHubRoot = Split-Path $PSScriptRoot -Parent
$archive = Join-Path $devHubRoot '.devhub/cache/downloads/ES-1.1.0.38.x64.zip'
$target = Join-Path $devHubRoot '.devhub/data/integrations/everything'
$expected = '5E0C70CBF4F694080C34AA7C6C745E606C16FE76A4B5423B93EBF9DC34274C99'
New-Item -ItemType Directory -Force -Path (Split-Path $archive) | Out-Null
if (-not (Test-Path -LiteralPath $archive)) { Invoke-WebRequest 'https://www.voidtools.com/ES-1.1.0.38.x64.zip' -OutFile $archive -TimeoutSec 60 }
if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ne $expected) { throw 'Everything ES 下载包校验失败，未解压。' }
Expand-Archive -LiteralPath $archive -DestinationPath $target -Force
Copy-Item -LiteralPath (Join-Path $devHubRoot 'third_party/everything/LICENSE.txt') -Destination (Join-Path $target 'LICENSE.txt') -Force
Write-Output "Everything ES 1.1.0.38 已准备：$target"
