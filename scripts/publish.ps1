<#
  Builds the self-contained app folder (no .NET needed on the target machine).
    .\scripts\publish.ps1                 -> dist\IdeaBoard\
    .\scripts\publish.ps1 -Out E:\apps\IdeaBoard
  Publishing over an existing folder keeps its appsettings.json and data\.
  Stop the service there first if it is running (the exe is locked).
#>
param(
    [string]$Out = (Join-Path $PSScriptRoot '..\dist\IdeaBoard')
)
$ErrorActionPreference = 'Stop'

$root = Resolve-Path (Join-Path $PSScriptRoot '..')
$stage = Join-Path $root 'obj\publish'
if (Test-Path $stage) { Remove-Item -Recurse -Force $stage }

dotnet publish (Join-Path $root 'src\IdeaBoard\IdeaBoard.csproj') -c Release -o $stage --nologo
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed' }

New-Item -ItemType Directory -Force $Out | Out-Null
foreach ($f in Get-ChildItem $stage -File) {
    if ($f.Name -in 'web.config', 'IdeaBoard.staticwebassets.endpoints.json') { continue }
    if ($f.Name -eq 'appsettings.json' -and (Test-Path (Join-Path $Out 'appsettings.json'))) { continue }
    Copy-Item $f.FullName $Out -Force
}
Copy-Item (Join-Path $PSScriptRoot 'install-service.ps1'), (Join-Path $PSScriptRoot 'uninstall-service.ps1') $Out -Force
Copy-Item (Join-Path $root 'README.md') (Join-Path $Out 'README.md') -Force

Write-Host "Gotowe: $(Resolve-Path $Out)"
Get-ChildItem $Out | Format-Table Name, Length -AutoSize
