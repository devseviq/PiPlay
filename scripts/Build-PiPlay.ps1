#Requires -Version 5.1
<#
.SYNOPSIS
  PiPlay build, publish, and release pipeline.

.DESCRIPTION
  Builds the WPF .NET app, publishes versioned folders, writes SHA256 metadata,
  maintains VERSION_TABLE.json, creates release archives, and tracks a monotonic
  BUILD_NUMBER. Optional signing runs before metadata is written so manifest
  hashes describe the final bytes.
#>

[CmdletBinding()]
param(
    [switch]$Help,

    [ValidateSet("Build", "Publish", "Release")]
    [string]$Stage = "Release",

    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",

    [string]$Framework = "net10.0-windows",
    [string]$Runtime = "win-x64",
    [switch]$SelfContained,

    # Release channel baked into the binary as [AssemblyMetadata("PiPlay.Channel", ...)]. "Stable"
    # gives a deployed copy its own data root (beside the exe), single-instance identity, and title.
    [ValidateSet("Default", "Stable")]
    [string]$Channel = "Default",

    [switch]$NoRestore,
    [switch]$ClearCache,
    [switch]$NoVersionBump,
    [string]$Version,

    [int]$BuildNumber = 0,
    [switch]$NoBuildNumberBump,

    [string]$PublishRoot = "bin\publish",
    [string]$PublishLabel,

    [ValidateRange(1, 200)]
    [int]$KeepPublishCount = 3,

    [switch]$NoLatest,
    [switch]$NoVersionTable,
    [switch]$NoArchive,

    [string]$StopProcessName = "PiPlay",

    [string]$SignScript,

    # When set, records this publish as NOT release evidence with this exact reason, independent of
    # source-tree dirtiness. Publish-Stable.ps1 passes it for -AllowDirty / -AllowVersionBump
    # diagnostic publishes so a clean-tree no-op can never mint release evidence.
    [string]$NonReleaseReason
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

. (Join-Path $PSScriptRoot "NativeCommand.ps1")

$ProjectName = "PiPlay"
$ProjectRelativePath = "src\PiPlay\PiPlay.csproj"
$PublishExtras = @(
    "README.md",
    "docs\CHANGELOG.md",
    "docs\YouTube_Compliance.md",
    "scripts\Test-DownloadedPackage.ps1",
    "scripts\Test-UiSmoke.ps1"
)

$script:Utf8NoBom = [System.Text.UTF8Encoding]::new($false)

function Write-TextNoBom {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][AllowEmptyString()][string]$Value,
        [switch]$NoNewline
    )

    $directory = Split-Path -Parent $Path
    if ($directory -and -not (Test-Path -LiteralPath $directory)) {
        New-Item -ItemType Directory -Path $directory -Force | Out-Null
    }

    $content = if ($NoNewline) { $Value } else { "$Value`r`n" }
    [System.IO.File]::WriteAllText($Path, $content, $script:Utf8NoBom)
}

function Write-JsonNoBom {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)]$Value,
        [int]$Depth = 12
    )

    $json = $Value | ConvertTo-Json -Depth $Depth
    Write-TextNoBom -Path $Path -Value $json
}

function Resolve-RepoPath {
    param(
        [Parameter(Mandatory = $true)][string]$Root,
        [Parameter(Mandatory = $true)][string]$Path
    )

    if ([System.IO.Path]::IsPathRooted($Path)) { return $Path }
    return (Join-Path $Root $Path)
}

function Get-RelativePathCompat {
    param(
        [Parameter(Mandatory = $true)][string]$Root,
        [Parameter(Mandatory = $true)][string]$Path
    )

    $rootFull = [System.IO.Path]::GetFullPath($Root).TrimEnd('\', '/') + [System.IO.Path]::DirectorySeparatorChar
    $pathFull = [System.IO.Path]::GetFullPath($Path)
    $rootUri = [System.Uri]::new($rootFull)
    $pathUri = [System.Uri]::new($pathFull)
    return [System.Uri]::UnescapeDataString($rootUri.MakeRelativeUri($pathUri).ToString()).Replace('/', '\')
}

function Get-ObjectPropertyValue {
    param(
        [Parameter(Mandatory = $true)]$Object,
        [Parameter(Mandatory = $true)][string]$Name
    )

    $prop = $Object.PSObject.Properties[$Name]
    if ($prop) { return $prop.Value }
    return $null
}

function Invoke-External {
    param(
        [Parameter(Mandatory = $true)][string]$FilePath,
        [Parameter(Mandatory = $true)][string[]]$Arguments,
        [Parameter(Mandatory = $true)][string]$FailureMessage
    )

    Write-Host "  > $FilePath $($Arguments -join ' ')" -ForegroundColor DarkGray
    $global:LASTEXITCODE = 0
    & $FilePath @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "$FailureMessage (exit code: $LASTEXITCODE)."
    }
}

function Get-ProjectVersion {
    param([Parameter(Mandatory = $true)][string]$VersionFile)

    if (-not (Test-Path -LiteralPath $VersionFile)) {
        Write-TextNoBom -Path $VersionFile -Value "0.1.0" -NoNewline
    }

    $value = (Get-Content -LiteralPath $VersionFile -Raw).Trim()
    if ($value -notmatch '^\d+\.\d+\.\d+(-[A-Za-z0-9.\-]+)?$') {
        throw "Invalid VERSION value '$value'. Expected semantic version like 0.1.0 or 0.1.0-beta."
    }

    return $value
}

function Set-ProjectVersion {
    param(
        [Parameter(Mandatory = $true)][string]$VersionFile,
        [Parameter(Mandatory = $true)][string]$NewVersion
    )

    if ($NewVersion -notmatch '^\d+\.\d+\.\d+(-[A-Za-z0-9.\-]+)?$') {
        throw "Invalid version '$NewVersion'. Use patch, minor, major, or explicit semantic version."
    }

    Write-TextNoBom -Path $VersionFile -Value $NewVersion -NoNewline
}

function Step-SemVer {
    param(
        [Parameter(Mandatory = $true)][string]$Current,
        [Parameter(Mandatory = $true)][ValidateSet("patch", "minor", "major")][string]$Part
    )

    $numeric = $Current -replace '-.*$', ''
    $parts = $numeric.Split('.')
    $major = [int]$parts[0]
    $minor = [int]$parts[1]
    $patch = [int]$parts[2]

    switch ($Part) {
        "patch" { $patch++ }
        "minor" { $minor++; $patch = 0 }
        "major" { $major++; $minor = 0; $patch = 0 }
    }

    return "$major.$minor.$patch"
}

function Resolve-VersionParam {
    param(
        [string]$Value,
        [Parameter(Mandatory = $true)][string]$CurrentVersion
    )

    if ([string]::IsNullOrWhiteSpace($Value)) {
        return Step-SemVer -Current $CurrentVersion -Part "patch"
    }

    $normalized = $Value.Trim().ToLowerInvariant()
    if ($normalized -in @("patch", "minor", "major")) {
        return Step-SemVer -Current $CurrentVersion -Part $normalized
    }

    if ($Value.Trim() -match '^\d+\.\d+\.\d+(-[A-Za-z0-9.\-]+)?$') {
        return $Value.Trim()
    }

    throw "Invalid -Version value '$Value'. Use patch, minor, major, or explicit semantic version."
}

function Get-NumericVersionParts {
    param([Parameter(Mandatory = $true)][string]$Version)

    $numeric = $Version -replace '-.*$', ''
    $parts = $numeric.Split('.')
    return @([int]$parts[0], [int]$parts[1], [int]$parts[2])
}

function Get-BuildNumberValue {
    param([Parameter(Mandatory = $true)][string]$BuildNumberFile)

    if (-not (Test-Path -LiteralPath $BuildNumberFile)) {
        Write-TextNoBom -Path $BuildNumberFile -Value "0" -NoNewline
    }

    $raw = (Get-Content -LiteralPath $BuildNumberFile -Raw).Trim()
    if ($raw -notmatch '^\d+$') {
        throw "Invalid BUILD_NUMBER value '$raw'. Expected a non-negative integer."
    }

    return [int]$raw
}

function Set-BuildNumberValue {
    param(
        [Parameter(Mandatory = $true)][string]$BuildNumberFile,
        [Parameter(Mandatory = $true)][int]$Value
    )

    if ($Value -lt 0) { throw "Build number cannot be negative." }
    Write-TextNoBom -Path $BuildNumberFile -Value ([string]$Value) -NoNewline
}

function Get-Sha256Hex {
    param([Parameter(Mandatory = $true)][string]$Path)
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToUpperInvariant()
}

function Get-SourceCommit {
    param([Parameter(Mandatory = $true)][string]$RepositoryRoot)

    $git = Get-Command git -ErrorAction SilentlyContinue
    if (-not $git) { return $null }

    $commit = Invoke-NativeCommandQuiet { & $git.Source -C $RepositoryRoot rev-parse HEAD }
    if ($LASTEXITCODE -ne 0) { return $null }

    $trimmed = "$commit".Trim()
    if ([string]::IsNullOrWhiteSpace($trimmed)) { return $null }
    return $trimmed
}

function Get-SourceDirtyEntries {
    param([Parameter(Mandatory = $true)][string]$RepositoryRoot)

    $git = Get-Command git -ErrorAction SilentlyContinue
    if (-not $git) { return @() }

    $status = @(Invoke-NativeCommandQuiet { & $git.Source -C $RepositoryRoot status --porcelain --untracked-files=all })
    if ($LASTEXITCODE -ne 0) { return @() }
    return @($status | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
}

function Get-HashEntries {
    param([Parameter(Mandatory = $true)][string]$Root)

    $entries = @()
    $excludedNames = @("build-info.json", "BUILDINFO.json", "VERSION_TABLE.json")
    $files = @(Get-ChildItem -LiteralPath $Root -Recurse -File -Force -ErrorAction SilentlyContinue | Sort-Object FullName)
    foreach ($file in $files) {
        if ($excludedNames -contains $file.Name) { continue }
        $relative = (Get-RelativePathCompat -Root $Root -Path $file.FullName).Replace('\', '/')
        $entries += [ordered]@{
            path = $relative
            size = $file.Length
            sha256 = Get-Sha256Hex -Path $file.FullName
        }
    }

    return $entries
}

function Get-PublishedArtifacts {
    param(
        [Parameter(Mandatory = $true)][string]$VersionRoot,
        [Parameter(Mandatory = $true)][string]$ProjectName
    )

    $artifacts = @()
    foreach ($item in @(Get-ChildItem -LiteralPath $VersionRoot -File -Filter "*.exe" -ErrorAction SilentlyContinue | Sort-Object Name)) {
        $artifacts += (Get-RelativePathCompat -Root $VersionRoot -Path $item.FullName).Replace('\', '/')
    }

    if ($artifacts.Count -gt 0) { return $artifacts }

    $mainDll = Get-ChildItem -LiteralPath $VersionRoot -File -Filter "$ProjectName.dll" -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($mainDll) {
        $artifacts += (Get-RelativePathCompat -Root $VersionRoot -Path $mainDll.FullName).Replace('\', '/')
    }

    return $artifacts
}

function Get-FileVersionMetadata {
    param([Parameter(Mandatory = $true)][string]$FilePath)

    $info = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($FilePath)
    return [ordered]@{
        fileVersion = $info.FileVersion
        productVersion = $info.ProductVersion
        companyName = $info.CompanyName
        productName = $info.ProductName
        fileDescription = $info.FileDescription
        originalFilename = $info.OriginalFilename
    }
}

function Get-BuildInfoPath {
    param([Parameter(Mandatory = $true)][string]$DirectoryPath)

    foreach ($name in @("build-info.json", "BUILDINFO.json")) {
        $candidate = Join-Path $DirectoryPath $name
        if (Test-Path -LiteralPath $candidate) { return $candidate }
    }

    return $null
}

function Clear-BuildCache {
    param([Parameter(Mandatory = $true)][string]$RepositoryRoot)

    $srcRoot = Join-Path $RepositoryRoot "src"
    if (-not (Test-Path -LiteralPath $srcRoot)) { return }

    $removed = 0
    foreach ($folder in @(Get-ChildItem -LiteralPath $srcRoot -Recurse -Directory -Force -ErrorAction SilentlyContinue)) {
        if ($folder.Name -ine "bin" -and $folder.Name -ine "obj") { continue }
        Remove-Item -LiteralPath $folder.FullName -Recurse -Force
        $removed++
    }

    Write-Host "Cleared build cache folders: $removed" -ForegroundColor Gray
}

function Stop-RunningProcess {
    param([Parameter(Mandatory = $true)][string]$ProcessName)

    $procs = @(Get-Process -Name $ProcessName -ErrorAction SilentlyContinue)
    if ($procs.Count -eq 0) { return }

    Write-Host "Stopping $($procs.Count) running $ProcessName process(es)..." -ForegroundColor Yellow
    $procs | Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep -Milliseconds 500
}

function Copy-PublishExtras {
    param(
        [Parameter(Mandatory = $true)][string]$RepositoryRoot,
        [Parameter(Mandatory = $true)][string]$VersionRoot,
        [Parameter(Mandatory = $true)][string[]]$Extras
    )

    foreach ($extra in $Extras) {
        $source = Join-Path $RepositoryRoot $extra
        if (-not (Test-Path -LiteralPath $source)) { continue }

        $destination = Join-Path $VersionRoot $extra
        $destinationDirectory = Split-Path -Parent $destination
        if ($destinationDirectory -and -not (Test-Path -LiteralPath $destinationDirectory)) {
            New-Item -ItemType Directory -Path $destinationDirectory -Force | Out-Null
        }

        if ((Get-Item -LiteralPath $source).PSIsContainer) {
            Copy-Item -LiteralPath $source -Destination $destination -Recurse -Force
        } else {
            Copy-Item -LiteralPath $source -Destination $destination -Force
        }
    }
}

function Write-BuildInfo {
    param(
        [Parameter(Mandatory = $true)][string]$VersionRoot,
        [Parameter(Mandatory = $true)][string]$ProjectName,
        [Parameter(Mandatory = $true)][string]$ProjectVersion,
        [Parameter(Mandatory = $true)][int]$BuildNumber,
        [Parameter(Mandatory = $true)][string]$PublishLabel,
        [Parameter(Mandatory = $true)][string]$Configuration,
        [string]$Channel = "Default",
        [string]$Framework,
        [string]$Runtime,
        [bool]$SelfContained,
        [bool]$SigningEnabled = $false,
        [string]$SignScript,
        [string]$NonReleaseReason,
        [Parameter(Mandatory = $true)][string]$RepositoryRoot
    )

    $artifactHashes = @(Get-HashEntries -Root $VersionRoot)
    $publishedArtifacts = @(Get-PublishedArtifacts -VersionRoot $VersionRoot -ProjectName $ProjectName)
    $sourceCommit = Get-SourceCommit -RepositoryRoot $RepositoryRoot
    $sourceDirtyEntries = @(Get-SourceDirtyEntries -RepositoryRoot $RepositoryRoot)
    $sourceDirty = $sourceDirtyEntries.Count -gt 0
    # Release evidence requires BOTH a clean source tree AND no explicit non-release reason. An
    # explicit reason (a diagnostic escape hatch) forces non-release even from a clean tree, so a
    # no-op diagnostic publish can never mint release evidence. sourceDirty / sourceDirtyEntries are
    # still recorded independently below so the two signals never get conflated.
    $explicitNonRelease = -not [string]::IsNullOrWhiteSpace($NonReleaseReason)
    $releaseEvidence = (-not $sourceDirty) -and (-not $explicitNonRelease)
    $releaseEvidenceReason = if ($explicitNonRelease -and $sourceDirty) {
        "$NonReleaseReason; the source tree also had uncommitted or untracked changes when metadata was written"
    } elseif ($explicitNonRelease) {
        $NonReleaseReason
    } elseif ($sourceDirty) {
        "source tree had uncommitted or untracked changes when metadata was written"
    } else {
        "source commit, version stamps, and artifact hashes were captured from a clean tree"
    }

    $primaryArtifact = $null
    foreach ($preferred in @("$ProjectName.exe", "$ProjectName.dll")) {
        $match = $publishedArtifacts | Where-Object { $_ -ieq $preferred } | Select-Object -First 1
        if ($match) { $primaryArtifact = $match; break }
    }
    if (-not $primaryArtifact -and $publishedArtifacts.Count -gt 0) {
        $primaryArtifact = $publishedArtifacts[0]
    }

    $primaryHash = $null
    if ($primaryArtifact) {
        $primaryHash = $artifactHashes | Where-Object { $_.path -ieq $primaryArtifact } | Select-Object -First 1
    }
    if (-not $primaryHash -and $artifactHashes.Count -gt 0) {
        $primaryHash = $artifactHashes[0]
    }

    $versionMetadata = $null
    if ($primaryArtifact) {
        $primaryArtifactPath = Join-Path $VersionRoot $primaryArtifact
        if (Test-Path -LiteralPath $primaryArtifactPath) {
            $versionMetadata = Get-FileVersionMetadata -FilePath $primaryArtifactPath
        }
    }

    $buildInfo = [ordered]@{
        project = $ProjectName
        version = $ProjectVersion
        buildNumber = $BuildNumber
        publishLabel = $PublishLabel
        channel = $Channel
        configuration = $Configuration
        framework = $Framework
        runtime = $Runtime
        selfContained = $SelfContained
        sourceCommit = $sourceCommit
        publishedArtifacts = $publishedArtifacts
        primaryArtifact = $primaryArtifact
        sha256 = if ($primaryHash) { $primaryHash.sha256 } else { $null }
        size = if ($primaryHash) { $primaryHash.size } else { $null }
        artifactCount = $artifactHashes.Count
        artifactHashes = $artifactHashes
        releaseEvidence = $releaseEvidence
        releaseEvidenceReason = $releaseEvidenceReason
        sourceDirty = $sourceDirty
        sourceDirtyEntries = $sourceDirtyEntries
        signing = if ($SigningEnabled) {
            [ordered]@{
                enabled = $true
                mode = "external-script"
                signScript = $SignScript
                timing = "before-manifest-hash"
            }
        } else {
            [ordered]@{
                enabled = $false
                reason = "not configured"
            }
        }
        builtUtc = (Get-Date).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ")
    }

    if ($versionMetadata) {
        foreach ($field in @("fileVersion", "productVersion", "companyName", "productName", "fileDescription", "originalFilename")) {
            if ($versionMetadata[$field]) { $buildInfo[$field] = $versionMetadata[$field] }
        }
    }

    $buildInfoPath = Join-Path $VersionRoot "build-info.json"
    $legacyBuildInfoPath = Join-Path $VersionRoot "BUILDINFO.json"
    Write-JsonNoBom -Path $buildInfoPath -Value $buildInfo -Depth 12
    Write-JsonNoBom -Path $legacyBuildInfoPath -Value $buildInfo -Depth 12
    return $buildInfoPath
}

function Update-BuildInfoArchive {
    param(
        [Parameter(Mandatory = $true)][string]$BuildInfoPath,
        [Parameter(Mandatory = $true)][string]$ArchivePath
    )

    $buildInfo = Get-Content -LiteralPath $BuildInfoPath -Raw | ConvertFrom-Json
    $archiveName = Split-Path -Path $ArchivePath -Leaf
    if ($buildInfo.PSObject.Properties.Name -contains "archive") {
        $buildInfo.archive = $archiveName
    } else {
        $buildInfo | Add-Member -NotePropertyName archive -NotePropertyValue $archiveName
    }

    Write-JsonNoBom -Path $BuildInfoPath -Value $buildInfo -Depth 12
    Write-JsonNoBom -Path (Join-Path (Split-Path -Parent $BuildInfoPath) "BUILDINFO.json") -Value $buildInfo -Depth 12
    return $BuildInfoPath
}

function Invoke-SignScript {
    param(
        [Parameter(Mandatory = $true)][string]$ScriptPath,
        [Parameter(Mandatory = $true)][string]$VersionRoot,
        [Parameter(Mandatory = $true)][string]$ProjectName,
        [Parameter(Mandatory = $true)][string]$ProjectVersion,
        [Parameter(Mandatory = $true)][int]$BuildNumber,
        [Parameter(Mandatory = $true)][string]$Channel,
        [Parameter(Mandatory = $true)][string]$Configuration
    )

    Write-Host "  > $ScriptPath -PublishRoot $VersionRoot -ProjectName $ProjectName -Version $ProjectVersion -BuildNumber $BuildNumber -Channel $Channel -Configuration $Configuration" -ForegroundColor DarkGray
    $global:LASTEXITCODE = 0
    & $ScriptPath `
        -PublishRoot $VersionRoot `
        -ProjectName $ProjectName `
        -Version $ProjectVersion `
        -BuildNumber $BuildNumber `
        -Channel $Channel `
        -Configuration $Configuration
    if ($LASTEXITCODE -ne 0) {
        throw "Signing script failed (exit code: $LASTEXITCODE)."
    }
}

function Write-VersionTable {
    param(
        [Parameter(Mandatory = $true)][string]$PublishRoot,
        [Parameter(Mandatory = $true)][string]$ProjectName
    )

    $builds = @()
    $folders = @(Get-ChildItem -LiteralPath $PublishRoot -Directory -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -ne "latest" -and $_.Name -ne "archive" } |
        Sort-Object Name -Descending)

    foreach ($folder in $folders) {
        $buildInfoPath = Get-BuildInfoPath -DirectoryPath $folder.FullName
        if (-not $buildInfoPath) { continue }

        try {
            $buildInfo = Get-Content -LiteralPath $buildInfoPath -Raw | ConvertFrom-Json
        } catch {
            Write-Warning "Skipping malformed build-info file: $buildInfoPath"
            continue
        }

        $artifactHashes = @()
        foreach ($hashEntry in @(Get-ObjectPropertyValue -Object $buildInfo -Name "artifactHashes")) {
            if (-not $hashEntry) { continue }
            $artifactHashes += [ordered]@{
                path = Get-ObjectPropertyValue -Object $hashEntry -Name "path"
                size = Get-ObjectPropertyValue -Object $hashEntry -Name "size"
                sha256 = Get-ObjectPropertyValue -Object $hashEntry -Name "sha256"
            }
        }

        $entrySha = Get-ObjectPropertyValue -Object $buildInfo -Name "sha256"
        $entrySize = Get-ObjectPropertyValue -Object $buildInfo -Name "size"
        if ((-not $entrySha -or -not $entrySize) -and $artifactHashes.Count -gt 0) {
            $entrySha = $artifactHashes[0].sha256
            $entrySize = $artifactHashes[0].size
        }

        $publishedArtifacts = @(Get-ObjectPropertyValue -Object $buildInfo -Name "publishedArtifacts")
        $publishLabel = Get-ObjectPropertyValue -Object $buildInfo -Name "publishLabel"
        if (-not $publishLabel) { $publishLabel = $folder.Name }

        $builds += [ordered]@{
            version = Get-ObjectPropertyValue -Object $buildInfo -Name "version"
            buildNumber = Get-ObjectPropertyValue -Object $buildInfo -Name "buildNumber"
            sha256 = $entrySha
            size = $entrySize
            builtUtc = Get-ObjectPropertyValue -Object $buildInfo -Name "builtUtc"
            publishLabel = $publishLabel
            configuration = Get-ObjectPropertyValue -Object $buildInfo -Name "configuration"
            framework = Get-ObjectPropertyValue -Object $buildInfo -Name "framework"
            runtime = Get-ObjectPropertyValue -Object $buildInfo -Name "runtime"
            selfContained = Get-ObjectPropertyValue -Object $buildInfo -Name "selfContained"
            sourceCommit = Get-ObjectPropertyValue -Object $buildInfo -Name "sourceCommit"
            artifacts = $publishedArtifacts
            artifactHashes = $artifactHashes
            archive = Get-ObjectPropertyValue -Object $buildInfo -Name "archive"
            buildInfoPath = (Get-RelativePathCompat -Root $PublishRoot -Path $buildInfoPath).Replace('\', '/')
        }
    }

    $builds = @($builds | Sort-Object builtUtc -Descending)
    $table = [ordered]@{
        project = $ProjectName
        generatedAtUtc = (Get-Date).ToUniversalTime().ToString("o")
        buildCount = $builds.Count
        builds = $builds
        entryCount = $builds.Count
        entries = $builds
    }

    $tablePath = Join-Path $PublishRoot "VERSION_TABLE.json"
    Write-JsonNoBom -Path $tablePath -Value $table -Depth 12
    return $tablePath
}

function New-ReleaseArchive {
    param(
        [Parameter(Mandatory = $true)][string]$PublishRoot,
        [Parameter(Mandatory = $true)][string]$VersionRoot,
        [Parameter(Mandatory = $true)][string]$ProjectName,
        [Parameter(Mandatory = $true)][string]$PublishLabel
    )

    $archiveRoot = Join-Path $PublishRoot "archive"
    New-Item -ItemType Directory -Path $archiveRoot -Force | Out-Null

    $zipPath = Join-Path $archiveRoot "$ProjectName-$PublishLabel.zip"
    if (Test-Path -LiteralPath $zipPath) { Remove-Item -LiteralPath $zipPath -Force }

    Compress-Archive -Path (Join-Path $VersionRoot "*") -DestinationPath $zipPath -CompressionLevel Optimal
    return $zipPath
}

function Sync-ReleaseMetadataIntoArchive {
    param(
        [Parameter(Mandatory = $true)][string]$ArchivePath,
        [Parameter(Mandatory = $true)][string]$VersionRoot,
        [string[]]$FileNames = @("build-info.json", "BUILDINFO.json", "VERSION_TABLE.json")
    )

    Add-Type -AssemblyName System.IO.Compression
    Add-Type -AssemblyName System.IO.Compression.FileSystem

    $zip = [System.IO.Compression.ZipFile]::Open($ArchivePath, [System.IO.Compression.ZipArchiveMode]::Update)
    try {
        foreach ($name in $FileNames) {
            $sourcePath = Join-Path $VersionRoot $name
            if (-not (Test-Path -LiteralPath $sourcePath)) { continue }

            $existing = $zip.GetEntry($name)
            if ($existing) { $existing.Delete() }
            [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile(
                $zip, $sourcePath, $name,
                [System.IO.Compression.CompressionLevel]::Optimal
            ) | Out-Null
        }
    } finally {
        $zip.Dispose()
    }
}

function Set-LatestSnapshot {
    param(
        [Parameter(Mandatory = $true)][string]$PublishRoot,
        [Parameter(Mandatory = $true)][string]$VersionRoot
    )

    $latestRoot = Join-Path $PublishRoot "latest"
    if (Test-Path -LiteralPath $latestRoot) { Remove-Item -LiteralPath $latestRoot -Recurse -Force }
    New-Item -ItemType Directory -Path $latestRoot -Force | Out-Null
    Copy-Item -Path (Join-Path $VersionRoot "*") -Destination $latestRoot -Recurse -Force
    return $latestRoot
}

function Remove-OldPublishes {
    param(
        [Parameter(Mandatory = $true)][string]$PublishRoot,
        [Parameter(Mandatory = $true)][int]$KeepPublishCount,
        [string]$CurrentLabel
    )

    $removed = @()
    # Sort by recency, not folder name: a custom -PublishLabel that sorts lexically below the default
    # yyyyMMdd-HHmmss timestamps must never land the just-built folder in the prune set (data-loss).
    $publishFolders = @(Get-ChildItem -LiteralPath $PublishRoot -Directory -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -ne "latest" -and $_.Name -ne "archive" } |
        Sort-Object LastWriteTimeUtc -Descending)

    if ($publishFolders.Count -le $KeepPublishCount) { return $removed }

    foreach ($folder in @($publishFolders | Select-Object -Skip $KeepPublishCount)) {
        # Belt-and-braces: never delete the publish this run just produced.
        if ($CurrentLabel -and $folder.Name -eq $CurrentLabel) { continue }
        Remove-Item -LiteralPath $folder.FullName -Recurse -Force
        $removed += $folder.FullName
    }

    return $removed
}

if ($Help) {
    Write-Host "Build-PiPlay.ps1 - PiPlay C# build/publish/release pipeline" -ForegroundColor Cyan
    Write-Host ""
    Write-Host "USAGE" -ForegroundColor Yellow
    Write-Host "  .\Build-PiPlay.ps1 [-Stage Build|Publish|Release] [options]"
    Write-Host ""
    Write-Host "MAIN OPTIONS" -ForegroundColor Yellow
    Write-Host "  -Stage <value>          Build, Publish, or Release (default: Release)"
    Write-Host "  -Configuration <value>  Debug or Release (default: Release)"
    Write-Host "  -Framework <tfm>        Target framework (default: net10.0-windows)"
    Write-Host "  -Runtime <rid>          Publish runtime (default: win-x64)"
    Write-Host "  -SelfContained          Publish self-contained output"
    Write-Host "  -Version <value>        patch, minor, major, or explicit semver; default bumps patch"
    Write-Host "  -NoVersionBump          Keep VERSION unchanged"
    Write-Host "  -BuildNumber <n>        Use an explicit BUILD_NUMBER for publish/release"
    Write-Host "  -NoBuildNumberBump      Keep BUILD_NUMBER unchanged"
    Write-Host "  -NoRestore              Skip dotnet restore"
    Write-Host "  -ClearCache             Remove src\**\bin and src\**\obj before build"
    Write-Host "  -StopProcessName <n>    Stop this process before publish/release (default: PiPlay)"
    Write-Host ""
    Write-Host "OUTPUT OPTIONS" -ForegroundColor Yellow
    Write-Host "  -PublishRoot <path>     Publish root (default: bin\publish)"
    Write-Host "  -PublishLabel <label>   Explicit publish folder label"
    Write-Host "  -KeepPublishCount <n>   Keep newest n publish folders (default: 3)"
    Write-Host "  -NoLatest               Skip bin\publish\latest"
    Write-Host "  -NoVersionTable         Skip VERSION_TABLE.json"
    Write-Host "  -NoArchive              Skip archive zip in Release stage"
    Write-Host "  -SignScript <path>      Run this signing script after publish and before metadata hashes"
    Write-Host "  -NonReleaseReason <s>   Force releaseEvidence=false with this reason (diagnostic publishes)"
    Write-Host ""
    Write-Host "SIGNING" -ForegroundColor Yellow
    Write-Host "  Optional -SignScript runs before build-info.json hashes are written."
    Write-Host ""
    Write-Host "EXAMPLES" -ForegroundColor Yellow
    Write-Host "  .\Build-PiPlay.ps1 -Stage Build -NoVersionBump"
    Write-Host "  .\Build-PiPlay.ps1 -Stage Release -Version patch"
    Write-Host "  .\Build-PiPlay.ps1 -Stage Release -SelfContained"
    exit 0
}

$timer = [System.Diagnostics.Stopwatch]::StartNew()
$repoRoot = Split-Path -Parent $PSScriptRoot
$projectPath = Resolve-RepoPath -Root $repoRoot -Path $ProjectRelativePath
$publishRootResolved = Resolve-RepoPath -Root $repoRoot -Path $PublishRoot
$versionFile = Join-Path $repoRoot "VERSION"
$buildNumberFile = Join-Path $repoRoot "BUILD_NUMBER"
$selfContainedValue = if ($SelfContained) { "true" } else { "false" }

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw "dotnet CLI was not found in PATH. Install the .NET SDK before building PiPlay."
}

if (-not (Test-Path -LiteralPath $projectPath)) {
    throw "Project file not found: $projectPath. Create the WPF project at src\PiPlay\PiPlay.csproj before running this pipeline."
}

if ($PSBoundParameters.ContainsKey("BuildNumber") -and $BuildNumber -lt 0) {
    throw "-BuildNumber cannot be negative."
}

$originalVersion = Get-ProjectVersion -VersionFile $versionFile
$originalBuildNumber = Get-BuildNumberValue -BuildNumberFile $buildNumberFile
$resolvedVersion = $originalVersion
$resolvedBuildNumber = $originalBuildNumber
$versionFileUpdated = $false
$buildNumberUpdated = $false
$artifactProduced = $false
$versionRoot = $null
$versionRootCreated = $false
$sourceCommit = Get-SourceCommit -RepositoryRoot $repoRoot
$signScriptResolved = $null
if ($SignScript) {
    $signScriptResolved = Resolve-RepoPath -Root $repoRoot -Path $SignScript
    if (-not (Test-Path -LiteralPath $signScriptResolved)) {
        throw "SignScript not found: $signScriptResolved"
    }
}

Write-Host "--- PiPlay pipeline: $Stage / $Configuration ---" -ForegroundColor Cyan
Write-Host "Repo root      : $repoRoot"
Write-Host "Project path   : $projectPath"
Write-Host "Publish root   : $publishRootResolved"
if ($signScriptResolved) {
    Write-Host "Signing        : external script ($signScriptResolved)" -ForegroundColor Cyan
} else {
    Write-Host "Signing        : not configured" -ForegroundColor DarkGray
}

try {
    if ($ClearCache) {
        Write-Host ""
        Write-Host "[0] Clearing build cache..." -ForegroundColor Yellow
        Clear-BuildCache -RepositoryRoot $repoRoot
    }

    if ($StopProcessName -and ($Stage -eq "Publish" -or $Stage -eq "Release")) {
        Write-Host ""
        Write-Host "[0a] Stopping running process: $StopProcessName" -ForegroundColor Yellow
        Stop-RunningProcess -ProcessName $StopProcessName
    }

    Write-Host ""
    Write-Host "[1] Version and build number..." -ForegroundColor Yellow
    if ($NoVersionBump) {
        Write-Host "  Version     : $resolvedVersion (unchanged)" -ForegroundColor Cyan
    } else {
        $resolvedVersion = Resolve-VersionParam -Value $Version -CurrentVersion $originalVersion
        if ($resolvedVersion -ne $originalVersion) {
            Set-ProjectVersion -VersionFile $versionFile -NewVersion $resolvedVersion
            $versionFileUpdated = $true
            Write-Host "  Version     : $originalVersion -> $resolvedVersion" -ForegroundColor Cyan
        } else {
            Write-Host "  Version     : $resolvedVersion (unchanged)" -ForegroundColor Cyan
        }
    }

    if ($Stage -eq "Build") {
        Write-Host "  Build number: $resolvedBuildNumber (unchanged for build stage)" -ForegroundColor Cyan
    } else {
        if ($PSBoundParameters.ContainsKey("BuildNumber")) {
            $resolvedBuildNumber = $BuildNumber
        } elseif (-not $NoBuildNumberBump) {
            $resolvedBuildNumber = $originalBuildNumber + 1
        }

        if ($resolvedBuildNumber -ne $originalBuildNumber) {
            Set-BuildNumberValue -BuildNumberFile $buildNumberFile -Value $resolvedBuildNumber
            $buildNumberUpdated = $true
            Write-Host "  Build number: $originalBuildNumber -> $resolvedBuildNumber" -ForegroundColor Cyan
        } else {
            Write-Host "  Build number: $resolvedBuildNumber (unchanged)" -ForegroundColor Cyan
        }
    }

    $numericParts = Get-NumericVersionParts -Version $resolvedVersion
    $assemblyVersion = "$($numericParts[0]).$($numericParts[1]).$($numericParts[2]).$resolvedBuildNumber"
    # Keep InformationalVersion (-> Win32 ProductVersion, shown in file properties)
    # a clean semantic version, matching the SevIQ house style. The commit is still
    # captured in build-info.json (sourceCommit); see IncludeSourceRevisionIn-
    # InformationalVersion=false in PiPlay.csproj which suppresses the SDK's +sha.
    $informationalVersion = $resolvedVersion

    if (-not $NoRestore) {
        Write-Host ""
        Write-Host "[2] Restore..." -ForegroundColor Yellow
        $restoreArgs = @("restore", $projectPath, "-p:BuildInParallel=false")
        if ($Runtime) {
            # A previous no-RID restore (for example `dotnet test`) can leave project.assets.json
            # without the runtime target while dotnet still reports restore as up to date. Force
            # the RID restore so the following --no-restore build has netX-windows/<rid> assets.
            $restoreArgs += @("-r", $Runtime, "--force")
        }
        Invoke-External -FilePath "dotnet" -Arguments $restoreArgs -FailureMessage "Restore failed"
    } else {
        Write-Host ""
        Write-Host "[2] Restore skipped (-NoRestore)." -ForegroundColor Yellow
    }

    Write-Host ""
    Write-Host "[3] Build..." -ForegroundColor Yellow
    $propertyArgs = @(
        "-p:Version=$resolvedVersion",
        "-p:AssemblyVersion=$assemblyVersion",
        "-p:FileVersion=$assemblyVersion",
        "-p:InformationalVersion=$informationalVersion",
        "-p:BuildNumber=$resolvedBuildNumber",
        "-p:PiPlayChannel=$Channel",
        "-p:PublishTrimmed=false",
        "-p:PublishSingleFile=false"
    )

    $buildArgs = @("build", $projectPath, "-c", $Configuration, "-p:BuildInParallel=false")
    if ($Framework) { $buildArgs += @("-f", $Framework) }
    if ($Runtime) { $buildArgs += @("-r", $Runtime, "--self-contained", $selfContainedValue) }
    $buildArgs += $propertyArgs
    $buildArgs += "--no-restore"
    Invoke-External -FilePath "dotnet" -Arguments $buildArgs -FailureMessage "Build failed"

    if ($Stage -eq "Build") {
        $timer.Stop()
        Write-Host ""
        Write-Host "Build stage complete in $($timer.Elapsed.ToString('mm\:ss\.fff'))." -ForegroundColor Green
        exit 0
    }

    Write-Host ""
    Write-Host "[4] Publish..." -ForegroundColor Yellow
    if (-not $PublishLabel) {
        $PublishLabel = "$(Get-Date -Format 'yyyyMMdd-HHmmss')-v$resolvedVersion-b$resolvedBuildNumber"
        if ($Channel -ne "Default") { $PublishLabel += "-$($Channel.ToLowerInvariant())" }
    }

    New-Item -ItemType Directory -Path $publishRootResolved -Force | Out-Null
    $versionRoot = Join-Path $publishRootResolved $PublishLabel
    if (Test-Path -LiteralPath $versionRoot) {
        throw "Publish label already exists: $PublishLabel"
    }
    New-Item -ItemType Directory -Path $versionRoot -Force | Out-Null
    $versionRootCreated = $true

    $publishArgs = @(
        "publish", $projectPath,
        "-c", $Configuration,
        "--no-build",
        "-p:BuildInParallel=false",
        "-o", $versionRoot
    )
    if ($Framework) { $publishArgs += @("-f", $Framework) }
    if ($Runtime) { $publishArgs += @("-r", $Runtime, "--self-contained", $selfContainedValue) }
    $publishArgs += $propertyArgs
    $publishArgs += "--no-restore"
    Invoke-External -FilePath "dotnet" -Arguments $publishArgs -FailureMessage "Publish failed"
    # The stamped binary now exists on disk. A later post-publish step failing must NOT roll back the
    # version/build counters - that would orphan this artifact and break monotonic build numbers.
    $artifactProduced = $true

    Copy-PublishExtras -RepositoryRoot $repoRoot -VersionRoot $versionRoot -Extras $PublishExtras

    if ($signScriptResolved) {
        Write-Host ""
        Write-Host "[4a] Signing publish output before metadata..." -ForegroundColor Yellow
        Invoke-SignScript `
            -ScriptPath $signScriptResolved `
            -VersionRoot $versionRoot `
            -ProjectName $ProjectName `
            -ProjectVersion $resolvedVersion `
            -BuildNumber $resolvedBuildNumber `
            -Channel $Channel `
            -Configuration $Configuration
    }

    $buildInfoPath = Write-BuildInfo `
        -VersionRoot $versionRoot `
        -ProjectName $ProjectName `
        -ProjectVersion $resolvedVersion `
        -BuildNumber $resolvedBuildNumber `
        -PublishLabel $PublishLabel `
        -Configuration $Configuration `
        -Channel $Channel `
        -Framework $Framework `
        -Runtime $Runtime `
        -SelfContained ([bool]$SelfContained) `
        -SigningEnabled ([bool]$signScriptResolved) `
        -SignScript $signScriptResolved `
        -NonReleaseReason $NonReleaseReason `
        -RepositoryRoot $repoRoot

    $archivePath = $null
    if ($Stage -eq "Release" -and -not $NoArchive) {
        Write-Host ""
        Write-Host "[5] Archiving release..." -ForegroundColor Yellow
        $archivePath = New-ReleaseArchive -PublishRoot $publishRootResolved -VersionRoot $versionRoot -ProjectName $ProjectName -PublishLabel $PublishLabel
        $buildInfoPath = Update-BuildInfoArchive -BuildInfoPath $buildInfoPath -ArchivePath $archivePath
    }

    $removedFolders = @(Remove-OldPublishes -PublishRoot $publishRootResolved -KeepPublishCount $KeepPublishCount -CurrentLabel $PublishLabel)

    $versionTablePath = $null
    if (-not $NoVersionTable) {
        Write-Host ""
        Write-Host "[6] Writing version table..." -ForegroundColor Yellow
        $versionTablePath = Write-VersionTable -PublishRoot $publishRootResolved -ProjectName $ProjectName
        Copy-Item -LiteralPath $versionTablePath -Destination (Join-Path $versionRoot "VERSION_TABLE.json") -Force
    }

    $latestRoot = $null
    if (-not $NoLatest) {
        Write-Host ""
        Write-Host "[7] Updating latest snapshot..." -ForegroundColor Yellow
        $latestRoot = Set-LatestSnapshot -PublishRoot $publishRootResolved -VersionRoot $versionRoot
    }

    if ($archivePath) {
        Sync-ReleaseMetadataIntoArchive -ArchivePath $archivePath -VersionRoot $versionRoot
    }

    $timer.Stop()
    $summaryBuildInfo = Get-Content -LiteralPath $buildInfoPath -Raw | ConvertFrom-Json

    Write-Host ""
    Write-Host "--- SUCCESS: $ProjectName v$resolvedVersion build $resolvedBuildNumber ---" -ForegroundColor Green
    Write-Host "Stage          : $Stage"
    Write-Host "Version        : $resolvedVersion"
    Write-Host "Build number   : $resolvedBuildNumber"
    Write-Host "Publish label  : $PublishLabel"
    Write-Host "Version path   : $versionRoot"
    Write-Host "Build info     : $buildInfoPath"
    if ($summaryBuildInfo.sha256) { Write-Host "SHA256         : $($summaryBuildInfo.sha256)" -ForegroundColor Green }
    if ($summaryBuildInfo.size) { Write-Host ("Size           : {0:N0} bytes" -f $summaryBuildInfo.size) }
    if ($sourceCommit) { Write-Host "Commit         : $sourceCommit" }
    if ($latestRoot) { Write-Host "Latest path    : $latestRoot" }
    if ($versionTablePath) { Write-Host "Version table  : $versionTablePath" }
    if ($archivePath) { Write-Host ("Archive        : {0} ({1:N0} bytes)" -f (Split-Path $archivePath -Leaf), (Get-Item -LiteralPath $archivePath).Length) }
    Write-Host "Folders pruned : $($removedFolders.Count)"
    Write-Host ("Completed in {0:mm\:ss\.fff}" -f $timer.Elapsed)
} catch {
    if ($artifactProduced) {
        # The publish folder is already written and BUILD_NUMBER $resolvedBuildNumber is stamped into
        # its binary; only a post-publish step (archive / version table / latest snapshot) failed.
        # Keep VERSION/BUILD_NUMBER committed so the counter stays monotonic and the stamped artifact
        # is not orphaned behind a rolled-back number.
        Write-Warning "A post-publish step failed, but the v$resolvedVersion build $resolvedBuildNumber artifact was produced; keeping VERSION/BUILD_NUMBER."
    } else {
        if ($versionFileUpdated) {
            try {
                Set-ProjectVersion -VersionFile $versionFile -NewVersion $originalVersion
                Write-Warning "Build failed before publishing; restored VERSION to $originalVersion."
            } catch {
                Write-Warning "Build failed and VERSION could not be restored automatically: $($_.Exception.Message)"
            }
        }

        if ($buildNumberUpdated) {
            try {
                Set-BuildNumberValue -BuildNumberFile $buildNumberFile -Value $originalBuildNumber
                Write-Warning "Build failed before publishing; restored BUILD_NUMBER to $originalBuildNumber."
            } catch {
                Write-Warning "Build failed and BUILD_NUMBER could not be restored automatically: $($_.Exception.Message)"
            }
        }

        # Remove the partial publish folder we created so a rolled-back counter leaves nothing behind.
        if ($versionRootCreated -and $versionRoot -and (Test-Path -LiteralPath $versionRoot)) {
            try {
                Remove-Item -LiteralPath $versionRoot -Recurse -Force
                Write-Warning "Removed partial publish folder: $versionRoot"
            } catch {
                Write-Warning "Could not remove partial publish folder '$versionRoot': $($_.Exception.Message)"
            }
        }
    }

    throw
}

exit 0
