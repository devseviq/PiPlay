#Requires -Version 7
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Tag,
    [Parameter(Mandatory = $true)][string]$Commit,
    [Parameter(Mandatory = $true)][string]$Archive,
    [Parameter(Mandatory = $true)][string]$Checksum,
    [Parameter(Mandatory = $true)][string]$Repository,
    [Parameter(Mandatory = $true)][string]$Title
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if ($Commit -notmatch '^[0-9a-f]{40}$') { throw "Commit must be a lowercase 40-character SHA; received '$Commit'." }
$expectedTagPattern = '^test-' + [regex]::Escape($Commit) + '-r[0-9]+-a[0-9]+$'
if ($Tag -notmatch $expectedTagPattern) { throw "Test tag '$Tag' does not identify commit '$Commit'." }
if ($Repository -notmatch '^[^/\s]+/[^/\s]+$') { throw "Repository must be owner/name; received '$Repository'." }
foreach ($asset in @($Archive, $Checksum)) {
    if (-not (Test-Path -LiteralPath $asset -PathType Leaf)) { throw "Prerelease asset not found: '$asset'." }
}
if ([string]::IsNullOrWhiteSpace($env:GH_TOKEN)) {
    throw 'GH_TOKEN must contain a policy credential that can inspect ruleset bypass actors.'
}
if ([string]::IsNullOrWhiteSpace($env:PIPLAY_RELEASE_TOKEN)) {
    throw 'PIPLAY_RELEASE_TOKEN must contain the publication token.'
}

& (Join-Path $PSScriptRoot 'Test-StableTagPolicy.ps1') `
    -Repository $Repository `
    -RequiredPattern 'refs/tags/test-*'
if ($LASTEXITCODE -ne 0) { throw "Test tag policy verification failed (exit $LASTEXITCODE)." }

$env:GH_TOKEN = $env:PIPLAY_RELEASE_TOKEN
$refName = "refs/tags/$Tag"
$createBody = @{ ref = $refName; sha = $Commit } | ConvertTo-Json -Compress
$createdJson = $createBody | & gh api --method POST "repos/$Repository/git/refs" --input -
if ($LASTEXITCODE -ne 0) {
    throw "Atomic creation of test tag '$Tag' failed; the ref may already exist."
}
$created = $createdJson | ConvertFrom-Json
if ([string]$created.ref -cne $refName -or [string]$created.object.sha -cne $Commit) {
    throw "GitHub created test tag '$Tag' with an unexpected identity."
}

function Assert-ExactRemoteTag {
    $refJson = & gh api "repos/$Repository/git/ref/tags/$Tag"
    if ($LASTEXITCODE -ne 0) { throw "Could not read created test tag '$Tag'." }
    $ref = $refJson | ConvertFrom-Json
    if ([string]$ref.ref -cne $refName -or [string]$ref.object.sha -cne $Commit) {
        throw "Remote test tag '$Tag' does not identify '$Commit'."
    }
}

Assert-ExactRemoteTag
$notes = @"
Test package for commit $Commit.

NOT RELEASE EVIDENCE. Interactive verification remains pending on SND-DESK.
"@
& gh release create $Tag `
    $Archive `
    $Checksum `
    --repo $Repository `
    --verify-tag `
    --draft `
    --prerelease `
    --title $Title `
    --notes $notes
if ($LASTEXITCODE -ne 0) { throw "GitHub test prerelease draft creation failed (exit $LASTEXITCODE)." }

Assert-ExactRemoteTag
& gh release edit $Tag --repo $Repository --draft=false --prerelease=true
if ($LASTEXITCODE -ne 0) { throw "GitHub test prerelease publication failed (exit $LASTEXITCODE)." }

Assert-ExactRemoteTag
$releaseJson = & gh release view $Tag --repo $Repository --json isDraft,isPrerelease,tagName
if ($LASTEXITCODE -ne 0) { throw "Could not verify published test prerelease '$Tag'." }
$release = $releaseJson | ConvertFrom-Json
if ($release.isDraft -ne $false -or $release.isPrerelease -ne $true -or
    [string]$release.tagName -cne $Tag) {
    throw "Published GitHub object '$Tag' is not the expected visible prerelease."
}

Write-Host "TEST PRERELEASE PUBLISHED: $Tag @ $Commit"
