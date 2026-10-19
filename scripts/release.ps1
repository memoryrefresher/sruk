<#
.SYNOPSIS
  Builds, signs and (optionally) publishes a SeaRaider update.

.EXAMPLE
  .\scripts\release.ps1 -NewKey
      Once: makes searaider-update-key.pem (keep it secret, never commit it) and prints the public key for
      Directory.Build.props <SeaRaiderUpdatePublicKey>.

.EXAMPLE
  .\scripts\release.ps1 -Version 0.11.0 -Repo you/SeaRaider -Notes "Faster downloads, FileCrypt parts" -Upload
      Publishes SeaRaider.exe 0.11.0, signs latest.json, and creates the GitHub release v0.11.0 with both files
      (needs the GitHub CLI "gh", signed in). Without -Upload it prints what to upload.
#>
param(
    [string]$Version,
    [string]$Repo,
    [string]$Notes = "",
    [switch]$NewKey,
    [switch]$Upload,
    [string]$KeyPath = (Join-Path $PSScriptRoot "..\searaider-update-key.pem")
)
$ErrorActionPreference = "Stop"
$root = Resolve-Path (Join-Path $PSScriptRoot "..")
$tool = Join-Path $root "tools\SeaRaider.ReleaseTool"

if ($NewKey) {
    dotnet run --project $tool -c Release -- keygen --out $root
    exit $LASTEXITCODE
}

if (-not $Version -or -not $Repo) { throw "Give -Version x.y.z and -Repo owner/name (or -NewKey the first time)." }
if (-not (Test-Path $KeyPath)) { throw "No signing key at $KeyPath. Run .\scripts\release.ps1 -NewKey once (and keep the file safe)." }

$props = Get-Content (Join-Path $root "Directory.Build.props") -Raw
if ($props -notmatch "<SeaRaiderUpdatePublicKey[^>]*>\s*[A-Za-z0-9+/=]{40,}") {
    throw "Directory.Build.props has no <SeaRaiderUpdatePublicKey>. Paste the key from -NewKey there first, or this build couldn't update itself later."
}

$out = Join-Path $root "release\$Version"
Write-Host "Publishing SeaRaider $Version..." -ForegroundColor Cyan
dotnet publish (Join-Path $root "src\SeaRaider.App") -c Release -r win-x64 --self-contained `
    -p:PublishSingleFile=true -p:Version=$Version -o $out
if ($LASTEXITCODE -ne 0) { throw "Publish failed." }

$exe = Join-Path $out "SeaRaider.exe"
$url = "https://github.com/$Repo/releases/download/v$Version/SeaRaider.exe"
Write-Host "Signing..." -ForegroundColor Cyan
dotnet run --project $tool -c Release -- sign --exe $exe --url $url --key $KeyPath --version $Version --notes $Notes --out (Join-Path $out "latest.json")
if ($LASTEXITCODE -ne 0) { throw "Signing failed." }

if ($Upload) {
    gh release create "v$Version" $exe (Join-Path $out "latest.json") --repo $Repo --title "SeaRaider $Version" --notes $Notes
    if ($LASTEXITCODE -ne 0) { throw "Creating the GitHub release failed." }
    Write-Host "Released. Every SeaRaider will offer $Version within a few hours (or at its next start)." -ForegroundColor Green
} else {
    Write-Host ""
    Write-Host "Done. Create GitHub release v$Version in $Repo and attach both files from ${out}:" -ForegroundColor Green
    Write-Host "  SeaRaider.exe   (must be reachable at $url)"
    Write-Host "  latest.json"
}
