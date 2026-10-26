#Requires -RunAsAdministrator
<#
  Updates IdeaBoard in this folder to the latest GitHub release.
  Run from an elevated PowerShell inside the app folder:  .\update.ps1
  appsettings.json and data\ are never touched. Also (re)applies the permissions and
  recovery settings the in-app "Aktualizacja" button relies on.
#>
param(
    [string]$Name = 'IdeaBoard',
    [string]$Repo = 'C4rl0s79/IdeaBoard',
    [switch]$Force
)
$ErrorActionPreference = 'Stop'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$dir = $PSScriptRoot
$exe = Join-Path $dir 'IdeaBoard.exe'
$headers = @{ 'User-Agent' = 'IdeaBoard-update' }

$release = Invoke-RestMethod "https://api.github.com/repos/$Repo/releases/latest" -Headers $headers
$zipAsset = $release.assets | Where-Object { $_.name -like '*-win-x64.zip' } | Select-Object -First 1
$shaAsset = $release.assets | Where-Object { $_.name -like '*-win-x64.zip.sha256' } | Select-Object -First 1
if (-not $zipAsset -or -not $shaAsset) { throw "Wydanie $($release.tag_name) nie ma paczki win-x64." }

$latest = [version]($release.tag_name.TrimStart('v'))
$current = if (Test-Path $exe) { [version](((Get-Item $exe).VersionInfo.ProductVersion -split '\+')[0]) } else { [version]'0.0.0' }
Write-Host "Zainstalowana: $current, najnowsza: $latest"
if ($latest -le $current -and -not $Force) { Write-Host 'Masz najnowszą wersję.'; return }

$tmp = Join-Path $env:TEMP 'IdeaBoard-update'
if (Test-Path $tmp) { Remove-Item -Recurse -Force $tmp }
New-Item -ItemType Directory $tmp | Out-Null
$zip = Join-Path $tmp $zipAsset.name
Invoke-WebRequest $zipAsset.browser_download_url -OutFile $zip -UseBasicParsing -Headers $headers
Invoke-WebRequest $shaAsset.browser_download_url -OutFile "$zip.sha256" -UseBasicParsing -Headers $headers

$expected = ((Get-Content "$zip.sha256" -Raw).Trim() -split '\s+')[0]
$actual = (Get-FileHash $zip -Algorithm SHA256).Hash
if ($actual -ne $expected) { throw 'Suma kontrolna paczki się nie zgadza — przerywam.' }

$staging = Join-Path $tmp 'staging'
Expand-Archive $zip $staging

$svc = Get-Service $Name -ErrorAction SilentlyContinue
if ($svc -and $svc.Status -ne 'Stopped') {
    Stop-Service $Name -Force
    (Get-Service $Name).WaitForStatus('Stopped', [TimeSpan]::FromSeconds(30))
}

Get-ChildItem $staging -File | Where-Object { $_.Name -ne 'appsettings.json' } | Copy-Item -Destination $dir -Force
if (-not (Test-Path (Join-Path $dir 'appsettings.json'))) { Copy-Item (Join-Path $staging 'appsettings.json') $dir }

# The service (LOCAL SERVICE) may rewrite its own folder, so the in-app update can swap files,
# and a non-zero exit after an update counts as a failure that triggers the restart.
icacls $dir /grant '*S-1-5-19:(OI)(CI)M' | Out-Null
if ($svc) {
    sc.exe failure $Name reset= 86400 actions= restart/2000/restart/5000/restart/60000 | Out-Null
    sc.exe failureflag $Name 1 | Out-Null
    Start-Service $Name
}
Remove-Item -Recurse -Force $tmp
Write-Host "Zaktualizowano do $latest."
