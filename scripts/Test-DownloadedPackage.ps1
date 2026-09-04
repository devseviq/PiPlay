#Requires -Version 7
<#
.SYNOPSIS
  Verifies a downloaded PiPlay test or Stable release package without a Git checkout.
.DESCRIPTION
  Validates package provenance, manifest shape, the complete file inventory, SHA256 hashes,
  executable identity, and test-versus-release evidence. With -ValidateOnly it performs no writes
  and launches nothing. Otherwise it runs the packaged UI smoke with data and evidence outside the
  immutable package root.
.EXAMPLE
  pwsh -NoProfile -File .\scripts\Test-DownloadedPackage.ps1 -Kind Test -ExpectedCommit <sha> -ValidateOnly
.EXAMPLE
  pwsh -NoProfile -File .\scripts\Test-DownloadedPackage.ps1 -Kind Release -ExpectedTag stable-v1.2.3-b45
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('Test', 'Release')]
    [string]$Kind,
    [string]$Root,
    [string]$ExpectedCommit,
    [string]$ExpectedTag,
    [switch]$ValidateOnly,
    [string]$EvidenceDir,
    [string]$DataRoot,
    [ValidateRange(1, 300)]
    [int]$ReadyTimeoutSec = 30
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$script:TestPackageReason =
    'GitHub test prerelease; interactive verification pending on SND-DESK'
$script:ReleasePackageReason =
    'source commit, version stamps, and artifact hashes were captured from a clean tree'

function Get-ObjectPropertyValue {
    param(
        [Parameter(Mandatory = $true)]$Object,
        [Parameter(Mandatory = $true)][string]$Name
    )

    $property = $Object.PSObject.Properties[$Name]
    if ($property) { return $property.Value }
    return $null
}

function Assert-RequiredProperties {
    param(
        [Parameter(Mandatory = $true)]$Object,
        [Parameter(Mandatory = $true)][string[]]$Names,
        [Parameter(Mandatory = $true)][string]$Context
    )

    foreach ($name in $Names) {
        if (-not ($Object.PSObject.Properties.Name -contains $name)) {
            throw "$Context is missing required property '$name'."
        }
    }
}

function Get-Sha256Hex {
    param([Parameter(Mandatory = $true)][string]$Path)

    $stream = [System.IO.File]::OpenRead($Path)
    try {
        $sha256 = [System.Security.Cryptography.SHA256]::Create()
        try {
            return ([System.BitConverter]::ToString($sha256.ComputeHash($stream))).Replace('-', '')
        }
        finally {
            $sha256.Dispose()
        }
    }
    finally {
        $stream.Dispose()
    }
}

function Resolve-FullyQualifiedDirectory {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Name
    )

    if (-not [System.IO.Path]::IsPathFullyQualified($Path)) {
        throw "$Name must be fully qualified: '$Path'."
    }
    $resolved = [System.IO.Path]::GetFullPath($Path)
    $trimmed = $resolved.TrimEnd([char[]]@('\', '/'))
    $rootPath = [System.IO.Path]::GetPathRoot($resolved).TrimEnd([char[]]@('\', '/'))
    if ($trimmed -ieq $rootPath) { throw "$Name must be below a filesystem root." }
    if (-not (Test-Path -LiteralPath $trimmed -PathType Container)) {
        throw "$Name does not exist: '$trimmed'."
    }
    return $trimmed
}

function Assert-NoReparsePointComponents {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Name
    )

    $resolved = [System.IO.Path]::GetFullPath($Path)
    $pathRoot = [System.IO.Path]::GetPathRoot($resolved)
    $current = $pathRoot
    foreach ($segment in @($resolved.Substring($pathRoot.Length) -split '[\\/]' |
            Where-Object { -not [string]::IsNullOrWhiteSpace($_) })) {
        $current = Join-Path $current $segment
        if (-not (Test-Path -LiteralPath $current)) { break }
        $attributes = (Get-Item -LiteralPath $current -Force).Attributes
        if (($attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "$Name crosses reparse point '$current'."
        }
    }
}

function Test-PathsOverlap {
    param(
        [Parameter(Mandatory = $true)][string]$First,
        [Parameter(Mandatory = $true)][string]$Second
    )

    $firstPath = [System.IO.Path]::GetFullPath($First).TrimEnd([char[]]@('\', '/'))
    $secondPath = [System.IO.Path]::GetFullPath($Second).TrimEnd([char[]]@('\', '/'))
    return $firstPath.Equals($secondPath, [System.StringComparison]::OrdinalIgnoreCase) -or
        $firstPath.StartsWith($secondPath + '\', [System.StringComparison]::OrdinalIgnoreCase) -or
        $secondPath.StartsWith($firstPath + '\', [System.StringComparison]::OrdinalIgnoreCase)
}

function Resolve-ManifestArtifactPath {
    param(
        [Parameter(Mandatory = $true)][string]$PackageRoot,
        [Parameter(Mandatory = $true)][string]$ManifestPath
    )

    if ([string]::IsNullOrWhiteSpace($ManifestPath)) {
        throw 'Artifact manifest paths must not be empty.'
    }
    if ([System.IO.Path]::IsPathRooted($ManifestPath)) {
        throw "Artifact manifest path must be relative: '$ManifestPath'."
    }
    if (@($ManifestPath -split '[\\/]') -contains '..') {
        throw "Artifact manifest path contains parent traversal: '$ManifestPath'."
    }

    $rootPrefix = $PackageRoot.TrimEnd([char[]]@('\', '/')) + '\'
    $resolved = [System.IO.Path]::GetFullPath((Join-Path $PackageRoot $ManifestPath))
    if (-not $resolved.StartsWith($rootPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Artifact manifest path escapes the package root: '$ManifestPath'."
    }
    Assert-NoReparsePointComponents -Path $resolved -Name "Artifact '$ManifestPath'"
    return $resolved
}

function Resolve-ExternalDirectory {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][string]$PackageRoot
    )

    if (-not [System.IO.Path]::IsPathFullyQualified($Path)) {
        throw "$Name must be fully qualified: '$Path'."
    }
    $resolvedExternal = [System.IO.Path]::GetFullPath($Path).TrimEnd([char[]]@('\', '/'))
    Assert-NoReparsePointComponents -Path $resolvedExternal -Name $Name
    if (Test-PathsOverlap -First $resolvedExternal -Second $PackageRoot) {
        throw "$Name must not overlap the immutable package root."
    }
    New-Item -ItemType Directory -Path $resolvedExternal -Force | Out-Null
    Assert-NoReparsePointComponents -Path $resolvedExternal -Name $Name
    if (Test-PathsOverlap -First $resolvedExternal -Second $PackageRoot) {
        throw "$Name must not overlap the immutable package root."
    }
    return $resolvedExternal
}

if ([string]::IsNullOrWhiteSpace($Root)) {
    $Root = Split-Path -Parent $PSScriptRoot
}
$packageRoot = Resolve-FullyQualifiedDirectory -Path $Root -Name 'Package root'
Assert-NoReparsePointComponents -Path $packageRoot -Name 'Package root'

$primaryManifest = Join-Path $packageRoot 'build-info.json'
$legacyManifest = Join-Path $packageRoot 'BUILDINFO.json'
foreach ($manifestPath in @($primaryManifest, $legacyManifest)) {
    if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
        throw "Downloaded package is missing '$manifestPath'."
    }
}
if ((Get-Sha256Hex $primaryManifest) -ine (Get-Sha256Hex $legacyManifest)) {
    throw 'build-info.json and BUILDINFO.json are not identical.'
}

$buildInfo = Get-Content -LiteralPath $primaryManifest -Raw | ConvertFrom-Json
Assert-RequiredProperties -Object $buildInfo -Context 'build-info.json' -Names @(
    'project', 'version', 'buildNumber', 'publishLabel', 'channel', 'configuration',
    'sourceCommit', 'publishedArtifacts', 'primaryArtifact', 'artifactCount', 'artifactHashes',
    'releaseEvidence', 'releaseEvidenceReason', 'sourceDirty', 'sourceDirtyEntries',
    'fileVersion', 'productVersion')

if ([string]$buildInfo.project -cne 'PiPlay') { throw "project must be 'PiPlay'." }
if ([string]$buildInfo.channel -cne 'Stable') { throw "channel must be 'Stable'." }
if ([string]$buildInfo.configuration -cne 'Release') { throw "configuration must be 'Release'." }
if ([string]$buildInfo.primaryArtifact -ine 'PiPlay.exe') { throw "primaryArtifact must be 'PiPlay.exe'." }
if ([string]$buildInfo.sourceCommit -notmatch '^[0-9a-fA-F]{40}$') {
    throw 'sourceCommit must contain exactly 40 hexadecimal characters.'
}
if ($buildInfo.sourceDirty -isnot [bool] -or $buildInfo.sourceDirty) {
    throw 'sourceDirty must be false.'
}
if (@($buildInfo.sourceDirtyEntries).Count -ne 0) {
    throw 'sourceDirtyEntries must be empty.'
}

if ($Kind -eq 'Test') {
    if ($ExpectedCommit -notmatch '^[0-9a-fA-F]{40}$') {
        throw 'Test packages require -ExpectedCommit from the GitHub artifact name.'
    }
    if ([string]$buildInfo.sourceCommit -ine $ExpectedCommit) {
        throw 'Package sourceCommit does not match -ExpectedCommit.'
    }
    if (-not [string]::IsNullOrWhiteSpace($ExpectedTag)) {
        throw '-ExpectedTag is valid only for Release packages.'
    }
    if ($buildInfo.releaseEvidence -isnot [bool] -or $buildInfo.releaseEvidence) {
        throw 'Test package releaseEvidence must be false.'
    }
    if ([string]$buildInfo.releaseEvidenceReason -cne $script:TestPackageReason) {
        throw "Test package releaseEvidenceReason must equal '$script:TestPackageReason'."
    }
    $expectedLabel = "test-$([string]$buildInfo.sourceCommit)"
    if ([string]$buildInfo.publishLabel -cne $expectedLabel) {
        throw "Test package publishLabel must equal '$expectedLabel'."
    }
}
else {
    if ([string]::IsNullOrWhiteSpace($ExpectedTag)) {
        throw 'Release packages require -ExpectedTag from the GitHub Release page.'
    }
    if (-not [string]::IsNullOrWhiteSpace($ExpectedCommit) -and
        ([string]$buildInfo.sourceCommit -ine $ExpectedCommit)) {
        throw 'Package sourceCommit does not match -ExpectedCommit.'
    }
    if ($buildInfo.releaseEvidence -isnot [bool] -or -not $buildInfo.releaseEvidence) {
        throw 'Release package releaseEvidence must be true.'
    }
    if ([string]$buildInfo.releaseEvidenceReason -cne $script:ReleasePackageReason) {
        throw "Release package releaseEvidenceReason must equal '$script:ReleasePackageReason'."
    }
    $expectedLabel = "stable-v$($buildInfo.version)-b$($buildInfo.buildNumber)"
    if ([string]$buildInfo.publishLabel -cne $expectedLabel) {
        throw "Release package publishLabel must equal '$expectedLabel'."
    }
    if ($ExpectedTag -cne $expectedLabel) {
        throw "Package identity '$expectedLabel' does not match -ExpectedTag '$ExpectedTag'."
    }
}

$artifacts = @($buildInfo.artifactHashes)
if ($artifacts.Count -eq 0) { throw 'artifactHashes must not be empty.' }
if ([int64]$buildInfo.artifactCount -ne $artifacts.Count) {
    throw 'artifactCount does not match artifactHashes.'
}

$seen = [System.Collections.Generic.HashSet[string]]::new(
    [System.StringComparer]::OrdinalIgnoreCase)
$resolvedArtifacts = [System.Collections.Generic.List[object]]::new()
foreach ($entry in $artifacts) {
    Assert-RequiredProperties -Object $entry -Context 'artifactHashes entry' -Names @('path', 'size', 'sha256')
    $artifactPath = Resolve-ManifestArtifactPath -PackageRoot $packageRoot -ManifestPath ([string]$entry.path)
    if (-not $seen.Add($artifactPath)) { throw "Duplicate artifact path '$($entry.path)'." }
    if (-not (Test-Path -LiteralPath $artifactPath -PathType Leaf)) {
        throw "Package artifact is missing: '$($entry.path)'."
    }
    if ([int64](Get-Item -LiteralPath $artifactPath).Length -ne [int64]$entry.size) {
        throw "Package artifact size mismatch: '$($entry.path)'."
    }
    if ([string]$entry.sha256 -notmatch '^[0-9a-fA-F]{64}$' -or
        (Get-Sha256Hex $artifactPath) -ine [string]$entry.sha256) {
        throw "Package artifact hash mismatch: '$($entry.path)'."
    }
    $resolvedArtifacts.Add([pscustomobject]@{ Entry = $entry; FullPath = $artifactPath })
}

foreach ($required in @('scripts/Test-DownloadedPackage.ps1', 'scripts/Test-UiSmoke.ps1')) {
    $requiredPath = [System.IO.Path]::GetFullPath((Join-Path $packageRoot $required))
    if (-not $seen.Contains($requiredPath)) {
        throw "Package manifest does not cover '$required'."
    }
}

$actualFiles = @(Get-ChildItem -LiteralPath $packageRoot -File -Recurse -Force | Where-Object {
    $_.FullName -ine $primaryManifest -and $_.FullName -ine $legacyManifest
})
$actualPaths = [System.Collections.Generic.HashSet[string]]::new(
    [System.StringComparer]::OrdinalIgnoreCase)
foreach ($file in $actualFiles) { [void]$actualPaths.Add([System.IO.Path]::GetFullPath($file.FullName)) }
if (-not $actualPaths.SetEquals($seen)) {
    throw 'Package file inventory does not exactly match artifactHashes.'
}

$exePath = Join-Path $packageRoot 'PiPlay.exe'
if (-not $seen.Contains([System.IO.Path]::GetFullPath($exePath))) {
    throw 'artifactHashes does not cover PiPlay.exe.'
}
$fileVersion = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($exePath)
$expectedFileVersion = "$($buildInfo.version).$($buildInfo.buildNumber)"
if ($fileVersion.FileVersion -ne $expectedFileVersion) {
    throw "PiPlay.exe FileVersion '$($fileVersion.FileVersion)' does not match '$expectedFileVersion'."
}
if ([string]$buildInfo.fileVersion -ne $fileVersion.FileVersion) {
    throw "PiPlay.exe FileVersion does not match build-info.json fileVersion."
}
if ($fileVersion.ProductVersion -ne [string]$buildInfo.productVersion) {
    throw "PiPlay.exe ProductVersion does not match build-info.json."
}

$dllPath = Join-Path $packageRoot 'PiPlay.dll'
if (-not (Test-Path -LiteralPath $dllPath -PathType Leaf)) {
    throw 'Downloaded package is missing PiPlay.dll.'
}
$assembly = [System.Reflection.Assembly]::LoadFile($dllPath)
$channelAttributes = @($assembly.GetCustomAttributes(
    [System.Reflection.AssemblyMetadataAttribute], $false) | Where-Object {
        $_.Key -ceq 'PiPlay.Channel'
    })
if ($channelAttributes.Count -ne 1 -or $channelAttributes[0].Value -cne 'Stable') {
    throw "PiPlay.dll AssemblyMetadataAttribute PiPlay.Channel must be 'Stable'."
}

Write-Host "PACKAGE VERIFIED: $Kind v$($buildInfo.version) b$($buildInfo.buildNumber) @ $($buildInfo.sourceCommit)" -ForegroundColor Green
if ($Kind -eq 'Test') {
    Write-Host 'This package is test evidence only, not a Stable release.' -ForegroundColor Yellow
}
if ($ValidateOnly) { return }

if ([string]::IsNullOrWhiteSpace($EvidenceDir)) {
    $EvidenceDir = Join-Path ([System.IO.Path]::GetTempPath()) `
        "PiPlayUiSmoke\$Kind-$($buildInfo.sourceCommit.Substring(0, 12))-$([Guid]::NewGuid().ToString('N'))"
}
if ([string]::IsNullOrWhiteSpace($DataRoot)) {
    $localAppData = [Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData)
    $DataRoot = Join-Path $localAppData `
        "PiPlay\DownloadedPackages\$Kind\$($buildInfo.sourceCommit)\$([Guid]::NewGuid().ToString('N'))"
}
$EvidenceDir = Resolve-ExternalDirectory -Path $EvidenceDir -Name 'EvidenceDir' -PackageRoot $packageRoot
$DataRoot = Resolve-ExternalDirectory -Path $DataRoot -Name 'PIPLAY_DATA_ROOT' -PackageRoot $packageRoot

$smokeScript = Join-Path $packageRoot 'scripts\Test-UiSmoke.ps1'
& (Get-Command pwsh -ErrorAction Stop).Source -NoProfile -File $smokeScript `
    -ExePath $exePath -EvidenceDir $EvidenceDir -DataRoot $DataRoot -ReadyTimeoutSec $ReadyTimeoutSec
if ($LASTEXITCODE -ne 0) { throw "Packaged UI smoke failed (exit $LASTEXITCODE)." }
