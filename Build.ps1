# CM AnrufMonitor: nachvollziehbarer lokaler Windows-Build.
[CmdletBinding()]
param([ValidateSet('win-x64', 'win-arm64')][string]$Runtime = 'win-x64')
$ErrorActionPreference = 'Stop'
$projectRoot = $PSScriptRoot
$appProject = Join-Path $projectRoot 'src\CM.AnrufMonitor.App\CM.AnrufMonitor.App.csproj'
$releasePath = Join-Path $projectRoot "artifacts\$Runtime"
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw 'Zum Bauen wird das .NET 10 SDK benötigt: https://dotnet.microsoft.com/download/dotnet/10.0'
}
Push-Location $projectRoot
try {
    foreach ($checkProject in @('CoreChecks', 'NetworkChecks', 'HistorySyncChecks', 'Tr064Checks')) {
        Write-Host "Prüfe $checkProject ..."
        & dotnet run --project "tests/$checkProject/$checkProject.csproj" -c Release
        if ($LASTEXITCODE -ne 0) { throw "Die Prüfung $checkProject ist fehlgeschlagen." }
    }
    Write-Host "Erstelle das eigenständig startbare Paket für $Runtime ..."
    & dotnet publish $appProject -c Release -r $Runtime --self-contained true `
        -p:EnableWindowsTargeting=true -p:PublishSingleFile=true `
        -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true `
        -p:PublishTrimmed=false -p:PublishReadyToRun=false -o $releasePath
    if ($LASTEXITCODE -ne 0) { throw 'Der Windows-Build ist fehlgeschlagen.' }
    Copy-Item (Join-Path $projectRoot 'Anleitung.html') $releasePath
    Copy-Item (Join-Path $projectRoot 'LIES-MICH.txt') $releasePath
    Copy-Item (Join-Path $projectRoot 'Beispiel-Kontakte.csv') $releasePath
    Copy-Item (Join-Path $projectRoot 'PRUEFBERICHT.md') $releasePath
    Copy-Item (Join-Path $projectRoot 'RELEASE-NOTES.md') $releasePath
    Copy-Item (Join-Path $projectRoot 'Lizenzhinweise') $releasePath -Recurse -Force
    Get-FileHash (Join-Path $releasePath 'CM-AnrufMonitor.exe') -Algorithm SHA256 |
        Format-List | Out-File (Join-Path $releasePath 'SHA256.txt') -Encoding utf8
    Write-Host "Fertig: $releasePath"
}
finally { Pop-Location }
