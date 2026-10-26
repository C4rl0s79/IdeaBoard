<#
  Publishes a GitHub release from a clean, committed main:
    1. bump <Version> in src\IdeaBoard\IdeaBoard.csproj and commit
    2. .\scripts\release.ps1 [-Notes "Co nowego"]
  Builds the app folder, zips it as IdeaBoard-X.Y.Z-win-x64.zip with a .sha256 file,
  pushes main and tag vX.Y.Z, and creates the release. Servers pick it up via
  the "Aktualizacja" button or update.ps1.
#>
param(
    [string]$Notes = ''
)
$ErrorActionPreference = 'Stop'

$root = Resolve-Path (Join-Path $PSScriptRoot '..')
Push-Location $root
try {
    $version = ([xml](Get-Content 'src\IdeaBoard\IdeaBoard.csproj')).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
    if (-not $version) { throw 'Brak <Version> w IdeaBoard.csproj' }
    $tag = "v$version"

    if (git status --porcelain) { throw 'Są niezatwierdzone zmiany — najpierw commit.' }
    if ((git branch --show-current) -ne 'main') { throw 'Wydania tylko z gałęzi main.' }
    if (git tag --list $tag) { throw "Tag $tag już istnieje — podbij <Version>." }

    $out = Join-Path $root 'obj\release\IdeaBoard'
    if (Test-Path (Split-Path $out)) { Remove-Item -Recurse -Force (Split-Path $out) }
    & (Join-Path $PSScriptRoot 'publish.ps1') -Out $out | Out-Null

    $built = ((Get-Item (Join-Path $out 'IdeaBoard.exe')).VersionInfo.ProductVersion -split '\+')[0]
    if ($built -ne $version) { throw "Zbudowany exe ma wersję $built, oczekiwano $version." }

    $zip = Join-Path $root "obj\release\IdeaBoard-$version-win-x64.zip"
    Compress-Archive -Path (Join-Path $out '*') -DestinationPath $zip
    $hash = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLower()
    Set-Content "$zip.sha256" "$hash  $(Split-Path $zip -Leaf)" -Encoding Ascii -NoNewline

    git tag -a $tag -m "IdeaBoard $version"
    git push origin main $tag
    if ($LASTEXITCODE -ne 0) { throw 'git push failed' }

    $notesArgs = if ($Notes) { @('--notes', $Notes) } else { @('--generate-notes') }
    gh release create $tag $zip "$zip.sha256" --title "IdeaBoard $version" @notesArgs
    if ($LASTEXITCODE -ne 0) { throw 'gh release create failed' }
    Write-Host "Opublikowano $tag ($hash)"
}
finally {
    Pop-Location
}
