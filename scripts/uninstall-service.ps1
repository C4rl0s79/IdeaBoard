#Requires -RunAsAdministrator
<# Removes the IdeaBoard Windows service. The folder and data\ stay untouched. #>
param(
    [string]$Name = 'IdeaBoard'
)
$ErrorActionPreference = 'Stop'

$svc = Get-Service $Name -ErrorAction SilentlyContinue
if (-not $svc) { Write-Host "Usługa $Name nie jest zainstalowana."; return }
if ($svc.Status -ne 'Stopped') { Stop-Service $Name -Force }
sc.exe delete $Name | Out-Null
Write-Host "Usługa $Name usunięta. Dane zostały w $(Join-Path $PSScriptRoot 'data')."
