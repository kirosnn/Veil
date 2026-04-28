[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$Version
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$projectRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
Set-Location $projectRoot

$tag = $Version.Trim()
if (-not $tag.StartsWith("v")) {
    $tag = "v$tag"
}

$status = git status --porcelain
if ($status) {
    Write-Host "The working tree is not clean. Commit and push your changes before creating a release tag:"
    Write-Host ""
    $status | ForEach-Object { Write-Host $_ }
    Write-Host ""
    Write-Host "Example:"
    Write-Host "  git add ."
    Write-Host "  git commit -m `"Prepare release $tag`""
    Write-Host "  git push"
    Write-Host "  powershell -ExecutionPolicy Bypass -File .\scripts\release.ps1 -Version $($tag.TrimStart('v'))"
    exit 1
}

$existingTag = git tag --list $tag
if ($existingTag) {
    throw "Tag $tag already exists."
}

git tag -a $tag -m "Release $tag"
git push origin $tag

Write-Host "Release tag pushed: $tag"
Write-Host "GitHub Actions will build and publish artifacts/inno/VeilSetup.exe."
