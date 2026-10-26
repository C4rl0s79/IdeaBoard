#Requires -RunAsAdministrator
<#
  Installs IdeaBoard as a Windows service running from this folder.
  Run from an elevated PowerShell inside the app folder:  .\install-service.ps1
  Re-running is safe: an existing service is replaced, data\ is kept.
#>
param(
    [string]$Name = 'IdeaBoard'
)
$ErrorActionPreference = 'Stop'

$dir = $PSScriptRoot
$exe = Join-Path $dir 'IdeaBoard.exe'
$data = Join-Path $dir 'data'
if (-not (Test-Path $exe)) { throw "Nie znaleziono $exe" }
New-Item -ItemType Directory -Force $data | Out-Null

# The service runs as LOCAL SERVICE (S-1-5-19). It may modify its own folder so the in-app
# update can swap IdeaBoard.exe; nothing outside this folder is writable for it.
icacls $dir /grant '*S-1-5-19:(OI)(CI)M' | Out-Null

if (-not (Test-Path (Join-Path $data 'auth.json'))) {
    Write-Host 'Ustaw hasło do tablicy:'
    & $exe set-password
    if ($LASTEXITCODE -ne 0) { throw 'Hasło nie zostało ustawione.' }
}

$existing = Get-Service $Name -ErrorAction SilentlyContinue
if ($existing) {
    if ($existing.Status -ne 'Stopped') { Stop-Service $Name -Force }
    sc.exe delete $Name | Out-Null
    Start-Sleep -Seconds 2
}

New-Service -Name $Name -BinaryPathName "`"$exe`"" -DisplayName 'IdeaBoard' `
    -Description 'IdeaBoard - kanban pomysłów (self-hosted)' -StartupType Automatic | Out-Null
Get-CimInstance Win32_Service -Filter "Name='$Name'" |
    Invoke-CimMethod -MethodName Change -Arguments @{ StartName = 'NT AUTHORITY\LocalService'; StartPassword = '' } | Out-Null
sc.exe config $Name start= delayed-auto | Out-Null
# Restart on crash and on a non-zero exit, which is how an in-app update hands over to the new exe.
sc.exe failure $Name reset= 86400 actions= restart/2000/restart/5000/restart/60000 | Out-Null
sc.exe failureflag $Name 1 | Out-Null

Start-Service $Name

$url = ((Get-Content (Join-Path $dir 'appsettings.json') -Raw | ConvertFrom-Json).Urls -split ';')[0]
Write-Host "Usługa $Name działa: $url"
Write-Host "W tunelu Cloudflare ustaw service: $url"
