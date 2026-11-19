[CmdletBinding()]
param([ValidateSet('win-x64', 'win-arm64')][string]$Runtime = 'win-x64')
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
[xml]$props = Get-Content (Join-Path $projectRoot 'Directory.Build.props')
$appVersion = [string]$props.Project.PropertyGroup.Version
if ($appVersion -notmatch '^\d+\.\d+\.\d+$') { throw 'Ungültige Projektversion.' }
$publishPath = Join-Path $projectRoot "artifacts/$Runtime"
$exePath = Join-Path $publishPath 'CM-AnrufMonitor.exe'
if (-not (Test-Path $exePath -PathType Leaf)) { throw 'Zuerst Build.ps1 ausführen.' }
$outputRoot = Join-Path $projectRoot 'release'
$architecture = $Runtime -replace '^win-', ''
$packageName = "CM-AnrufMonitor_${appVersion}_Windows-$architecture"
$stage = Join-Path $outputRoot $packageName
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory -Force -Path $stage | Out-Null
Copy-Item (Join-Path $publishPath '*') $stage -Recurse -Force
$smokePath = Join-Path $projectRoot 'qa-output/smoke-result.json'
$windowsSmoke = $false
if (Test-Path $smokePath) {
    $smoke = Get-Content $smokePath -Raw | ConvertFrom-Json
    if ($smoke.passed -ne $true -or $smoke.version -ne $appVersion) { throw 'Der vorhandene Windows-Prüfbericht passt nicht zu einem erfolgreichen Test dieser Version.' }
    $windowsSmoke = $true
    Copy-Item $smokePath (Join-Path $stage 'windows-smoke.json')
}
$sourceCommit = $env:GITHUB_SHA
if (-not $sourceCommit) {
    $sourceCommit = (& git -C $projectRoot rev-parse HEAD 2>$null)
    if ($LASTEXITCODE -ne 0) { $sourceCommit = 'local-uncommitted-build' }
}
$runtimeConfig = Join-Path $projectRoot "src/CM.AnrufMonitor.App/bin/Release/net10.0-windows/$Runtime/CM-AnrufMonitor.runtimeconfig.json"
$frameworks = @()
if (Test-Path $runtimeConfig) { $frameworks = (Get-Content $runtimeConfig -Raw | ConvertFrom-Json).runtimeOptions.includedFrameworks }
$buildInfo = [ordered]@{
    application = 'CM AnrufMonitor'; version = $appVersion; runtime = $Runtime
    source_commit = $sourceCommit; repository = 'https://github.com/ceromihalik-dev/CM-AnrufMonitor'
    built_utc = [DateTime]::UtcNow.ToString('o'); sdk = (& dotnet --version)
    included_frameworks = $frameworks; publisher_signed = $false
    real_fritzbox_test = $false; windows_smoke = $windowsSmoke
}
$buildInfo | ConvertTo-Json -Depth 8 | Set-Content (Join-Path $stage 'BUILD-INFO.json') -Encoding utf8
$shaLines = Get-ChildItem $stage -File -Recurse |
    Where-Object { $_.Name -ne 'SHA256SUMS.txt' } | Sort-Object FullName | ForEach-Object {
        $relative = [IO.Path]::GetRelativePath($stage, $_.FullName).Replace('\', '/')
        ((Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()) + '  ' + $relative
    }
$shaLines | Set-Content (Join-Path $stage 'SHA256SUMS.txt') -Encoding utf8
$zipPath = Join-Path $outputRoot ($packageName + '.zip')
Compress-Archive -Path $stage -DestinationPath $zipPath -CompressionLevel Optimal -Force
$zipHash = (Get-FileHash $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
($zipHash + '  ' + [IO.Path]::GetFileName($zipPath)) | Set-Content ($zipPath + '.sha256') -Encoding ascii
Write-Host "Paket: $zipPath"
Write-Host "SHA-256: $zipHash"
