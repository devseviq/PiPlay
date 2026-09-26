#Requires -Version 5.1
<#
.SYNOPSIS
  Behavioural harness for scripts\DeploySwap.ps1 - the staged-swap deploy used by Publish-Stable.ps1.

.DESCRIPTION
  The C# lane (ReleaseScriptPolicyTests) can only assert on script TEXT, which cannot tell you whether
  a rollback actually restores the previous copy. This exercises the real thing against a throwaway
  deploy root: a clean swap, a corrupt staged payload, a failure mid-swap (a locked file, the way a
  lingering process pins a dll), and both interrupted-publish recovery shapes.

  It caught a genuine data-loss bug on first run: Move-Item on a directory whose child is locked
  half-moves it and still throws, so a rollback keyed on "moves I recorded as successful" silently
  dropped the backed-up children before deleting the backup. Case C3 pins that.

  Cases G2-G6 and I-M pin the other half of the same promise: the deploy root must be DEDICATED to
  the deployed copy. The swap moves every non-PiPlayData child into a backup it deletes on success, so
  a mistaken PIPLAY_STABLE_ROOT used to be emptied by a deploy that reported no failure at all
  (readiness review F-1). A foreign root is now refused before it is touched - by the swap, by both
  recovery branches, and by Publish-Stable itself before the lock, the test lane and the build (case N
  spawns the real script; N9 holds the deploy-root lock to prove the refusal also beats the lock) -
  while a legitimate first install, redeploy, or mid-swap recovery is not.

  Deploys nothing and touches no real deploy root - everything happens under a temp sandbox.

.EXAMPLE
  .\scripts\Test-DeploySwap.ps1
#>
[CmdletBinding()]
param([string]$RepoRoot = (Split-Path -Parent $PSScriptRoot))

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

. (Join-Path $RepoRoot "scripts\DeploySwap.ps1")
. (Join-Path $RepoRoot "scripts\PublishLock.ps1")

$sandbox = Join-Path ([System.IO.Path]::GetTempPath()) ("DeploySwapTests-" + [guid]::NewGuid().ToString("N"))
$pass = 0
$fail = 0

function Check([string]$name, [scriptblock]$assertion) {
    try {
        $result = & $assertion
        if ($result -eq $false) { throw "assertion returned false" }
        Write-Host "[ PASS ] $name" -ForegroundColor Green
        $script:pass++
    } catch {
        Write-Host "[ FAIL ] $name :: $($_.Exception.Message)" -ForegroundColor Red
        $script:fail++
    }
}

function New-Payload {
    param([string]$Dir, [string]$Token, [int]$ExtraFiles = 2, [switch]$WithBuildMetadata)

    New-Item -ItemType Directory -Path $Dir -Force | Out-Null
    Set-Content -LiteralPath (Join-Path $Dir "PiPlay.exe") -Value "exe-$Token" -Encoding UTF8
    New-Item -ItemType Directory -Path (Join-Path $Dir "runtimes") -Force | Out-Null
    for ($i = 0; $i -lt $ExtraFiles; $i++) {
        Set-Content -LiteralPath (Join-Path $Dir "runtimes\lib$i.dll") -Value "lib$i-$Token" -Encoding UTF8
    }

    # Manifest describing exactly these bytes (mirrors Build-PiPlay's artifactHashes shape).
    $hashes = @()
    foreach ($f in @(Get-ChildItem -LiteralPath $Dir -Recurse -File)) {
        $rel = $f.FullName.Substring($Dir.Length).TrimStart('\')
        $hashes += [pscustomobject]@{
            path   = $rel
            sha256 = (Get-FileHash -LiteralPath $f.FullName -Algorithm SHA256).Hash.ToUpperInvariant()
            size   = [int64]$f.Length
        }
    }
    [pscustomobject]@{ version = "9.9.9"; buildNumber = 99; artifactHashes = $hashes } |
        ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $Dir "build-info.json") -Encoding UTF8

    if ($WithBuildMetadata) {
        # Build-PiPlay keeps its own metadata out of artifactHashes on purpose, so these names describe
        # the payload without the manifest listing them - the same shape a real deployed copy has.
        $manifest = Get-Content -LiteralPath (Join-Path $Dir "build-info.json") -Raw
        Set-Content -LiteralPath (Join-Path $Dir "BUILDINFO.json") -Value $manifest -Encoding UTF8
        Set-Content -LiteralPath (Join-Path $Dir "VERSION_TABLE.json") -Value '{"builds":[]}' -Encoding UTF8
    }
}

function New-DeployRootWithOldPayload {
    param([string]$Root)
    New-Payload -Dir $Root -Token "OLD"
    New-Item -ItemType Directory -Path (Join-Path $Root "PiPlayData") -Force | Out-Null
    Set-Content -LiteralPath (Join-Path $Root "PiPlayData\settings.json") -Value '{"session":"precious"}' -Encoding UTF8
    Set-Content -LiteralPath (Join-Path $Root ".piplay.publish.marker") -Value "version=old" -Encoding UTF8
}

function Test-DataPreserved([string]$Root) {
    $p = Join-Path $Root "PiPlayData\settings.json"
    return (Test-Path -LiteralPath $p) -and ((Get-Content -LiteralPath $p -Raw).Trim() -eq '{"session":"precious"}')
}

function Test-NoLeftovers([string]$Root) {
    $paths = Get-DeploySwapPaths -DeployRoot $Root
    return (-not (Test-Path -LiteralPath $paths.Staging)) -and (-not (Test-Path -LiteralPath $paths.Backup))
}

function Get-ExeToken([string]$Root) {
    return (Get-Content -LiteralPath (Join-Path $Root "PiPlay.exe") -Raw).Trim()
}

Write-Host "`n--- DeploySwap behavioural harness ---" -ForegroundColor Cyan
Write-Host "Sandbox: $sandbox`n"

try {
    # ---------------------------------------------------------------- A. happy path
    $rootA = Join-Path $sandbox "A\PiPlay"
    $srcA = Join-Path $sandbox "A\src"
    New-DeployRootWithOldPayload -Root $rootA
    New-Payload -Dir $srcA -Token "NEW"

    Invoke-StagedDeploy -DeployRoot $rootA -SourceDir $srcA -DataFolderName "PiPlayData" `
        -MarkerName ".piplay.publish.marker" -MarkerText "version=new" | Out-Null

    Check "A1 new payload is live"            { (Get-ExeToken $rootA) -eq "exe-NEW" }
    Check "A2 runtime data preserved"          { Test-DataPreserved $rootA }
    Check "A3 marker replaced with new"        { (Get-Content -LiteralPath (Join-Path $rootA ".piplay.publish.marker") -Raw).Trim() -eq "version=new" }
    Check "A4 no staging/backup left behind"   { Test-NoLeftovers $rootA }
    Check "A5 nested artifacts came across"    { (Get-Content -LiteralPath (Join-Path $rootA "runtimes\lib1.dll") -Raw).Trim() -eq "lib1-NEW" }

    # ------------------------------------------------- B. corrupt staged payload aborts pre-swap
    $rootB = Join-Path $sandbox "B\PiPlay"
    $srcB = Join-Path $sandbox "B\src"
    New-DeployRootWithOldPayload -Root $rootB
    New-Payload -Dir $srcB -Token "NEW"
    # Tamper AFTER the manifest was written: the staged copy will not match its own hashes.
    Set-Content -LiteralPath (Join-Path $srcB "runtimes\lib0.dll") -Value "CORRUPTED" -Encoding UTF8

    $threwB = $false
    try {
        Invoke-StagedDeploy -DeployRoot $rootB -SourceDir $srcB -DataFolderName "PiPlayData" `
            -MarkerName ".piplay.publish.marker" -MarkerText "version=new" | Out-Null
    } catch { $threwB = $true }

    Check "B1 corrupt payload throws"                  { $threwB }
    Check "B2 live copy UNTOUCHED (still old)"         { (Get-ExeToken $rootB) -eq "exe-OLD" }
    Check "B3 old marker untouched"                    { (Get-Content -LiteralPath (Join-Path $rootB ".piplay.publish.marker") -Raw).Trim() -eq "version=old" }
    Check "B4 runtime data preserved"                  { Test-DataPreserved $rootB }

    # ------------------------------------------- C. failure mid-swap rolls the old payload back
    $rootC = Join-Path $sandbox "C\PiPlay"
    $srcC = Join-Path $sandbox "C\src"
    New-DeployRootWithOldPayload -Root $rootC
    New-Payload -Dir $srcC -Token "NEW"

    # Hold a handle open on a live file so moving it aside fails partway through the swap - the
    # realistic failure (a lingering process pinning a dll).
    $locked = [System.IO.File]::Open(
        (Join-Path $rootC "runtimes\lib1.dll"),
        [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::None)

    $threwC = $false
    try {
        Invoke-StagedDeploy -DeployRoot $rootC -SourceDir $srcC -DataFolderName "PiPlayData" `
            -MarkerName ".piplay.publish.marker" -MarkerText "version=new" | Out-Null
    } catch { $threwC = $true } finally { $locked.Dispose() }

    Check "C1 mid-swap failure throws"                 { $threwC }
    Check "C2 old exe rolled back into place"          { (Get-ExeToken $rootC) -eq "exe-OLD" }
    Check "C3 old nested artifact restored"            { (Get-Content -LiteralPath (Join-Path $rootC "runtimes\lib0.dll") -Raw).Trim() -eq "lib0-OLD" }
    Check "C4 old marker restored"                     { (Get-Content -LiteralPath (Join-Path $rootC ".piplay.publish.marker") -Raw).Trim() -eq "version=old" }
    Check "C5 runtime data preserved"                  { Test-DataPreserved $rootC }
    Check "C6 no staging/backup left behind"           { Test-NoLeftovers $rootC }
    Check "C7 deployed copy is runnable again"         { Test-DeployPayloadComplete -DeployRoot $rootC }

    # ---------- H. rollback that CANNOT restore must keep the backup, not delete it.
    # Case C is the easy direction: the swap died during move-OUT, so every restore destination was
    # already vacated and each Move-Item succeeded. The dangerous direction is a failure during
    # move-IN where a second lock (an AV scan of the freshly written binaries is the realistic one)
    # blocks BOTH the removal of the moved-in file AND the restore over it. Every rollback step is
    # -ErrorAction SilentlyContinue, so the restore fails silently - and an earlier version of this
    # code then deleted the backup anyway and reported a successful rollback, destroying the only
    # remaining copy of that artifact. Drive Undo-DeploySwap directly at exactly that state.
    $rootH = Join-Path $sandbox "H\PiPlay"
    $pathsH = Get-DeploySwapPaths -DeployRoot $rootH

    New-Payload -Dir $rootH -Token "NEW"                  # the half-swapped-in new payload
    New-Item -ItemType Directory -Path (Join-Path $rootH "PiPlayData") -Force | Out-Null
    Set-Content -LiteralPath (Join-Path $rootH "PiPlayData\settings.json") -Value '{"session":"precious"}' -Encoding UTF8
    New-Payload -Dir $pathsH.Backup -Token "OLD"          # the previous payload, moved aside
    New-Item -ItemType Directory -Path $pathsH.Staging -Force | Out-Null

    # Hold the moved-in PiPlay.exe open with no sharing: neither the rollback's Remove-Item nor the
    # restore's Move-Item -Force can touch it.
    $lockedH = [System.IO.File]::Open(
        (Join-Path $rootH "PiPlay.exe"),
        [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::None)

    $errH = $null
    try {
        Undo-DeploySwap -DeployRoot $rootH -BackupDir $pathsH.Backup -StagingDir $pathsH.Staging `
            -MovedIn @("PiPlay.exe") -SwapError "simulated move-in failure" 3>$null
    } catch { $errH = $_.Exception.Message } finally { $lockedH.Dispose() }

    Check "H1 an unrestorable rollback throws"          { $null -ne $errH }
    Check "H2 it says the backup was PRESERVED"         { $errH -match 'PRESERVED at' }
    Check "H3 the backup still EXISTS on disk"          { Test-Path -LiteralPath $pathsH.Backup }
    Check "H4 the old exe is still recoverable from it" {
        (Get-Content -LiteralPath (Join-Path $pathsH.Backup "PiPlay.exe") -Raw).Trim() -eq "exe-OLD"
    }
    Check "H5 it does NOT claim a successful rollback"  { $errH -notmatch 'previous copy was rolled back' }
    Check "H6 runtime data preserved"                   { Test-DataPreserved $rootH }

    # --------------------- D. interrupted swap (old moved aside, new never landed) -> restore old
    $rootD = Join-Path $sandbox "D\PiPlay"
    New-DeployRootWithOldPayload -Root $rootD
    $pathsD = Get-DeploySwapPaths -DeployRoot $rootD
    # Simulate the kill window: everything but the data folder is sitting in .backup, root is bare.
    New-Item -ItemType Directory -Path $pathsD.Backup -Force | Out-Null
    foreach ($item in @(Get-ChildItem -LiteralPath $rootD -Force)) {
        if ($item.Name -ieq "PiPlayData") { continue }
        Move-Item -LiteralPath $item.FullName -Destination (Join-Path $pathsD.Backup $item.Name) -Force
    }
    New-Item -ItemType Directory -Path $pathsD.Staging -Force | Out-Null
    Set-Content -LiteralPath (Join-Path $pathsD.Staging "PiPlay.exe") -Value "exe-HALFSTAGED" -Encoding UTF8

    Check "D0 precondition: deploy root is broken"     { -not (Test-DeployPayloadComplete -DeployRoot $rootD) }

    $repairedD = Repair-InterruptedDeploy -DeployRoot $rootD -DataFolderName "PiPlayData" 3>$null

    Check "D1 repair reports it acted"                 { $repairedD -eq $true }
    Check "D2 previous payload restored"               { (Get-ExeToken $rootD) -eq "exe-OLD" }
    Check "D3 deployed copy runnable again"            { Test-DeployPayloadComplete -DeployRoot $rootD }
    Check "D4 runtime data survived the interruption"  { Test-DataPreserved $rootD }
    Check "D5 leftovers cleared"                       { Test-NoLeftovers $rootD }

    # ------------- E. interrupted AFTER the new payload landed (only cleanup lost) -> keep the new
    $rootE = Join-Path $sandbox "E\PiPlay"
    New-Payload -Dir $rootE -Token "NEW"
    New-Item -ItemType Directory -Path (Join-Path $rootE "PiPlayData") -Force | Out-Null
    Set-Content -LiteralPath (Join-Path $rootE "PiPlayData\settings.json") -Value '{"session":"precious"}' -Encoding UTF8
    $pathsE = Get-DeploySwapPaths -DeployRoot $rootE
    New-Payload -Dir $pathsE.Backup -Token "OLD"   # stale backup nobody removed

    $repairedE = Repair-InterruptedDeploy -DeployRoot $rootE -DataFolderName "PiPlayData" 3>$null

    Check "E1 repair reports it acted"                 { $repairedE -eq $true }
    Check "E2 the NEW payload is kept"                 { (Get-ExeToken $rootE) -eq "exe-NEW" }
    Check "E3 stale backup discarded"                  { Test-NoLeftovers $rootE }
    Check "E4 runtime data preserved"                  { Test-DataPreserved $rootE }

    # ------------------------------------------------ F. clean root: repair is a no-op
    $rootF = Join-Path $sandbox "F\PiPlay"
    New-DeployRootWithOldPayload -Root $rootF
    $repairedF = Repair-InterruptedDeploy -DeployRoot $rootF -DataFolderName "PiPlayData" 3>$null
    Check "F1 nothing to repair on a coherent root"    { $repairedF -eq $false }
    Check "F2 payload untouched"                       { (Get-ExeToken $rootF) -eq "exe-OLD" }

    # ------------------------------------------------ G. drive-root guard
    $threwG = $false
    try { Get-DeploySwapPaths -DeployRoot "E:\" | Out-Null } catch { $threwG = $true }
    Check "G1 refuses a drive root (no sibling space)" { $threwG }

    # ------------------------------------ G2-G6. a root that is not a PiPlay install is refused outright
    # The realistic failure: PIPLAY_STABLE_ROOT pointed at a directory holding somebody's documents. The
    # swap does not fail there - it succeeds, replaces the contents, and deletes the backup holding them,
    # so no rollback path ever engages. Everything here runs in the throwaway sandbox above.
    $rootG = Join-Path $sandbox "G\Documents"
    $srcG = Join-Path $sandbox "G\src"
    New-Item -ItemType Directory -Path (Join-Path $rootG "Taxes2025") -Force | Out-Null
    New-Item -ItemType Directory -Path (Join-Path $rootG "PiPlayData") -Force | Out-Null
    Set-Content -LiteralPath (Join-Path $rootG "wedding-photo.jpg") -Value "precious" -Encoding UTF8
    Set-Content -LiteralPath (Join-Path $rootG "Taxes2025\return.pdf") -Value "precious" -Encoding UTF8
    New-Payload -Dir $srcG -Token "NEW"

    $errG = $null
    try {
        Invoke-StagedDeploy -DeployRoot $rootG -SourceDir $srcG -DataFolderName "PiPlayData" `
            -MarkerName ".piplay.publish.marker" -MarkerText "version=new" | Out-Null
    } catch { $errG = $_.Exception.Message }

    Check "G2 refuses a non-empty unrelated deploy root" { $null -ne $errG -and $errG -match 'not a PiPlay install' }
    Check "G3 the stray file survives the refusal"       { Test-Path -LiteralPath (Join-Path $rootG "wedding-photo.jpg") }
    Check "G4 the stray folder tree survives too"        { Test-Path -LiteralPath (Join-Path $rootG "Taxes2025\return.pdf") }
    Check "G5 nothing was deployed into the root"        { -not (Test-DeployPayloadComplete -DeployRoot $rootG) }
    Check "G6 no staging/backup left beside it"          { Test-NoLeftovers $rootG }

    # ---------------- I. the recovery path deletes the root's children too, so it needs the same gate.
    # Repair-InterruptedDeploy trusts a '<leaf>.backup' sibling; a mistaken root can have an unrelated
    # sibling of that shape (and the roll-back branch removes every non-data child before restoring).
    $rootI = Join-Path $sandbox "I\Documents"
    $pathsI = Get-DeploySwapPaths -DeployRoot $rootI
    New-Item -ItemType Directory -Path (Join-Path $rootI "Taxes2025") -Force | Out-Null
    Set-Content -LiteralPath (Join-Path $rootI "Taxes2025\return.pdf") -Value "precious" -Encoding UTF8
    New-Item -ItemType Directory -Path $pathsI.Backup -Force | Out-Null
    Set-Content -LiteralPath (Join-Path $pathsI.Backup "old-tax-return.zip") -Value "not a payload" -Encoding UTF8

    $errI = $null
    try { Repair-InterruptedDeploy -DeployRoot $rootI -DataFolderName "PiPlayData" 3>$null | Out-Null }
    catch { $errI = $_.Exception.Message }

    Check "I1 the recovery path refuses a foreign root"  { $null -ne $errI -and $errI -match 'not a PiPlay install' }
    Check "I2 the foreign tree survives the refusal"     { Test-Path -LiteralPath (Join-Path $rootI "Taxes2025\return.pdf") }
    Check "I3 the untrusted backup is left intact"       { Test-Path -LiteralPath $pathsI.Backup }

    # A missing root proves nothing in the backup branch either (Task 1 review follow-up): a
    # foreign '<leaf>.backup' beside a nonexistent root must survive the rollback attempt.
    $rootI2 = Join-Path $sandbox "I2\Documents"
    $pathsI2 = Get-DeploySwapPaths -DeployRoot $rootI2
    New-Item -ItemType Directory -Path $pathsI2.Backup -Force | Out-Null
    Set-Content -LiteralPath (Join-Path $pathsI2.Backup "somebody-elses-backup.txt") -Value "precious" -Encoding UTF8

    $errI2 = $null
    try { Repair-InterruptedDeploy -DeployRoot $rootI2 -DataFolderName "PiPlayData" 3>$null | Out-Null }
    catch { $errI2 = $_.Exception.Message }

    Check "I2 a backup sibling beside a missing root needs payload evidence" {
        $null -ne $errI2 -and $errI2 -match 'carries no PiPlay payload'
    }
    Check "I3b the foreign backup sibling beside a missing root survives" {
        Test-Path -LiteralPath (Join-Path $pathsI2.Backup "somebody-elses-backup.txt")
    }

    # ------------- J. the gate must not refuse the roots a real operator legitimately has.
    $srcJ = Join-Path $sandbox "J\src"
    New-Payload -Dir $srcJ -Token "NEW"

    $rootJ1 = Join-Path $sandbox "J\fresh\PiPlay"        # first install: the location does not exist yet
    Invoke-StagedDeploy -DeployRoot $rootJ1 -SourceDir $srcJ -DataFolderName "PiPlayData" `
        -MarkerName ".piplay.publish.marker" -MarkerText "version=new" | Out-Null
    Check "J1 a missing root deploys normally"           { (Get-ExeToken $rootJ1) -eq "exe-NEW" }

    $rootJ2 = Join-Path $sandbox "J\dataonly\PiPlay"     # payload cleared by hand, runtime data kept
    New-Item -ItemType Directory -Path (Join-Path $rootJ2 "PiPlayData") -Force | Out-Null
    Set-Content -LiteralPath (Join-Path $rootJ2 "PiPlayData\settings.json") -Value '{"session":"precious"}' -Encoding UTF8
    Invoke-StagedDeploy -DeployRoot $rootJ2 -SourceDir $srcJ -DataFolderName "PiPlayData" `
        -MarkerName ".piplay.publish.marker" -MarkerText "version=new" | Out-Null
    Check "J2 a PiPlayData-only root deploys normally"   { (Get-ExeToken $rootJ2) -eq "exe-NEW" }
    Check "J3 its runtime data survived"                 { Test-DataPreserved $rootJ2 }

    # ------------------------ K. a swap interrupted mid-move is still recoverable (guard must not bite).
    # The kill window the repair exists for leaves payload bytes on BOTH sides of the rename, so the root
    # alone no longer looks like an install. Its siblings carry the manifest that proves the leftovers in
    # the root are ours - without that evidence case K2 below would be refused like case I1.
    $rootK = Join-Path $sandbox "K\PiPlay"
    $pathsK = Get-DeploySwapPaths -DeployRoot $rootK
    New-Item -ItemType Directory -Path (Join-Path $rootK "PiPlayData") -Force | Out-Null
    Set-Content -LiteralPath (Join-Path $rootK "PiPlayData\settings.json") -Value '{"session":"precious"}' -Encoding UTF8
    New-Item -ItemType Directory -Path (Join-Path $rootK "runtimes") -Force | Out-Null
    Set-Content -LiteralPath (Join-Path $rootK "runtimes\lib0.dll") -Value "lib0-NEW" -Encoding UTF8
    Set-Content -LiteralPath (Join-Path $rootK ".piplay.publish.marker") -Value "version=new" -Encoding UTF8
    New-Payload -Dir $pathsK.Backup -Token "OLD"         # the previous payload, moved aside and never restored

    Check "K0 precondition: the root is mid-swap, not runnable" { -not (Test-DeployPayloadComplete -DeployRoot $rootK) }

    $repairedK = Repair-InterruptedDeploy -DeployRoot $rootK -DataFolderName "PiPlayData" 3>$null

    Check "K1 the mid-swap root still rolls back"        { (Get-ExeToken $rootK) -eq "exe-OLD" }
    Check "K2 the restored copy is runnable again"       { Test-DeployPayloadComplete -DeployRoot $rootK }
    Check "K3 runtime data survived the interruption"    { Test-DataPreserved $rootK }
    Check "K4 leftovers cleared"                         { Test-NoLeftovers $rootK }

    # ------------------- L. the staging branch of repair deletes too, so it carries the same proof.
    # A foreign root with an unrelated '<leaf>.staging' sibling and no backup: the backup branch is
    # gone (case I covers it), but Remove-Item on the staging sibling is still somebody else's
    # directory deleted. The root itself must therefore be proven ours here too.
    $rootL = Join-Path $sandbox "L\Documents"
    $pathsL = Get-DeploySwapPaths -DeployRoot $rootL
    New-Item -ItemType Directory -Path (Join-Path $rootL "Taxes2025") -Force | Out-Null
    Set-Content -LiteralPath (Join-Path $rootL "Taxes2025\return.pdf") -Value "precious" -Encoding UTF8
    New-Item -ItemType Directory -Path $pathsL.Staging -Force | Out-Null
    Set-Content -LiteralPath (Join-Path $pathsL.Staging "somebody-elses-file.txt") -Value "precious" -Encoding UTF8

    $errL = $null
    try { Repair-InterruptedDeploy -DeployRoot $rootL -DataFolderName "PiPlayData" 3>$null | Out-Null }
    catch { $errL = $_.Exception.Message }

    Check "L1 a foreign root is refused by the staging branch" { $null -ne $errL -and $errL -match 'not a PiPlay install' }
    Check "L2 its contents survive the refusal"          { Test-Path -LiteralPath (Join-Path $rootL "Taxes2025\return.pdf") }
    Check "L3 the foreign staging sibling is left intact" { Test-Path -LiteralPath (Join-Path $pathsL.Staging "somebody-elses-file.txt") }

    # A legitimate interrupted run CAN leave a manifest-less staging (killed before the manifest was
    # written) beside a real install - the root itself proves ownership, so the deletion must proceed.
    $rootL2 = Join-Path $sandbox "L2\PiPlay"
    $pathsL2 = Get-DeploySwapPaths -DeployRoot $rootL2
    New-DeployRootWithOldPayload -Root $rootL2
    New-Item -ItemType Directory -Path $pathsL2.Staging -Force | Out-Null
    Set-Content -LiteralPath (Join-Path $pathsL2.Staging "PiPlay.exe") -Value "exe-HALFSTAGED" -Encoding UTF8

    $repairedL2 = Repair-InterruptedDeploy -DeployRoot $rootL2 -DataFolderName "PiPlayData" 3>$null

    Check "L4 a manifest-less staging beside an install is removed" { -not (Test-Path -LiteralPath $pathsL2.Staging) }
    Check "L5 repair reports it acted"                              { $repairedL2 -eq $true }

    # A MISSING root cannot prove anything (the dedicated check returns for a first install), so the
    # staging sibling must carry this payload's own manifest before repair deletes it.
    $rootL3 = Join-Path $sandbox "L3\Documents"
    $pathsL3 = Get-DeploySwapPaths -DeployRoot $rootL3
    New-Item -ItemType Directory -Path $pathsL3.Staging -Force | Out-Null
    Set-Content -LiteralPath (Join-Path $pathsL3.Staging "somebody-elses-file.txt") -Value "precious" -Encoding UTF8

    $errL3 = $null
    try { Repair-InterruptedDeploy -DeployRoot $rootL3 -DataFolderName "PiPlayData" 3>$null | Out-Null }
    catch { $errL3 = $_.Exception.Message }

    Check "L6 a staging sibling beside a missing root needs payload evidence" {
        $null -ne $errL3 -and $errL3 -match 'carries no PiPlay payload'
    }
    Check "L7 the foreign staging sibling beside a missing root survives" {
        Test-Path -LiteralPath (Join-Path $pathsL3.Staging "somebody-elses-file.txt")
    }

    # Genuine staging debris (killed between staging and the swap) IS deleted even beside a missing
    # root: the manifest it carries is the proof.
    $rootL4 = Join-Path $sandbox "L4\Documents"
    $pathsL4 = Get-DeploySwapPaths -DeployRoot $rootL4
    New-Payload -Dir $pathsL4.Staging -Token "HALFSTAGED"

    $repairedL4 = Repair-InterruptedDeploy -DeployRoot $rootL4 -DataFolderName "PiPlayData" 3>$null

    Check "L8 genuine staging debris beside a missing root is removed" { -not (Test-Path -LiteralPath $pathsL4.Staging) }
    Check "L9 repair reports it acted" { $repairedL4 -eq $true }

    # ------------- M. a complete install with one-off files deploys - but says so before displacing.
    $rootM = Join-Path $sandbox "M\PiPlay"
    $srcM = Join-Path $sandbox "M\src"
    New-DeployRootWithOldPayload -Root $rootM
    Set-Content -LiteralPath (Join-Path $rootM "release-notes.txt") -Value "operator's own file" -Encoding UTF8
    New-Payload -Dir $srcM -Token "NEW"

    $warnsM = @()
    Invoke-StagedDeploy -DeployRoot $rootM -SourceDir $srcM -DataFolderName "PiPlayData" `
        -MarkerName ".piplay.publish.marker" -MarkerText "version=new" 3>&1 |
        ForEach-Object { $warnsM += $_ }

    Check "M1 the stray file is named in a warning" {
        @($warnsM | Where-Object { $_ -is [System.Management.Automation.WarningRecord] -and $_.Message -match 'release-notes\.txt' }).Count -ge 1
    }
    Check "M2 the deploy still proceeds"            { (Get-ExeToken $rootM) -eq "exe-NEW" }
    Check "M3 runtime data preserved"               { Test-DataPreserved $rootM }

    # A deployed copy also carries BUILDINFO.json and VERSION_TABLE.json, which the build deliberately
    # leaves out of artifactHashes. They are the pipeline's own bytes, so a redeploy over an install that
    # has them must stay silent - otherwise every real publish warns and operators learn to ignore it.
    $rootM4 = Join-Path $sandbox "M4\PiPlay"
    $srcM4a = Join-Path $sandbox "M4\src-old"
    $srcM4b = Join-Path $sandbox "M4\src-new"
    New-Payload -Dir $srcM4a -Token "OLD" -WithBuildMetadata
    New-Payload -Dir $srcM4b -Token "NEW" -WithBuildMetadata
    Invoke-StagedDeploy -DeployRoot $rootM4 -SourceDir $srcM4a -DataFolderName "PiPlayData" `
        -MarkerName ".piplay.publish.marker" -MarkerText "version=old" | Out-Null

    $warnsM4 = @()
    Invoke-StagedDeploy -DeployRoot $rootM4 -SourceDir $srcM4b -DataFolderName "PiPlayData" `
        -MarkerName ".piplay.publish.marker" -MarkerText "version=new" 3>&1 |
        ForEach-Object { $warnsM4 += $_ }

    Check "M4 the install really does carry the manifest-less metadata" {
        (Test-Path -LiteralPath (Join-Path $rootM4 "BUILDINFO.json")) -and (Test-Path -LiteralPath (Join-Path $rootM4 "VERSION_TABLE.json"))
    }
    Check "M5 a redeploy over it warns about nothing" {
        @($warnsM4 | Where-Object { $_ -is [System.Management.Automation.WarningRecord] }).Count -eq 0
    }
    Check "M6 the redeploy still swapped in the new payload" { (Get-ExeToken $rootM4) -eq "exe-NEW" }

    # --------------------- N. Publish-Stable's own up-front refusals, through the real entry point.
    # The guard block runs before the locks, the test lane and the build, so spawning the real script
    # with a bad root is cheap and pins behaviour a text-shape assertion cannot: the exact input an
    # operator mistypes is rejected with an actionable message and zero side effects.
    $pwsh = (Get-Command pwsh).Source
    $publishScript = Join-Path $RepoRoot "scripts\Publish-Stable.ps1"

    function Invoke-PublishStableRefusal {
        param([string]$Root)

        $raw = & $pwsh -NoProfile -File $publishScript -DeployRoot $Root 2>&1
        $code = $LASTEXITCODE
        return [pscustomobject]@{ Exit = $code; Text = ($raw | Out-String) }
    }

    $rootN = Join-Path $sandbox "N\Documents"
    New-Item -ItemType Directory -Path $rootN -Force | Out-Null
    Set-Content -LiteralPath (Join-Path $rootN "wedding-photo.jpg") -Value "precious" -Encoding UTF8

    $refN1 = Invoke-PublishStableRefusal -Root "Documents"
    Check "N1 bare token refused as non-absolute"   { $refN1.Exit -ne 0 -and $refN1.Text -match 'must be an absolute path' }
    $refN2 = Invoke-PublishStableRefusal -Root "\Stable"
    Check "N2 drive-relative root refused"          { $refN2.Exit -ne 0 -and $refN2.Text -match 'fully qualified' }
    $refN3 = Invoke-PublishStableRefusal -Root "C:\"
    Check "N3 drive root refused"                   { $refN3.Exit -ne 0 -and $refN3.Text -match 'INSIDE a parent' }
    $refN4 = Invoke-PublishStableRefusal -Root $RepoRoot
    Check "N4 repository root refused"              { $refN4.Exit -ne 0 -and $refN4.Text -match 'repository root' }
    $refN5 = Invoke-PublishStableRefusal -Root (Join-Path $RepoRoot "bin\stable")
    Check "N5 root inside the repository refused"   { $refN5.Exit -ne 0 -and $refN5.Text -match 'repository root' }
    $refN6 = Invoke-PublishStableRefusal -Root $rootN
    Check "N6 foreign non-empty root refused"       { $refN6.Exit -ne 0 -and $refN6.Text -match 'not a PiPlay install' }
    Check "N7 refusal happens before the lock and preflight" { $refN6.Text -notmatch 'Tag preflight' }
    Check "N8 the foreign root survives the refusal" { Test-Path -LiteralPath (Join-Path $rootN "wedding-photo.jpg") }

    # N9 makes "before the lock" (which N7 only infers from absent prose) an observable property,
    # so a guard-ordering regression cannot hide. Hold the EXACT per-deploy-root lock Publish-Stable
    # takes, then invoke it against the same foreign root. With the dedicated check ahead of
    # New-PublishLock, the child refuses with 'not a PiPlay install' and never touches the mutex;
    # if the check ever slid behind the lock, the child would block on 'already running' instead.
    $rootNLockKey = "deploy|" + [System.IO.Path]::GetFullPath($rootN)
    New-PublishLock -Key $rootNLockKey -What "N9 guard-ordering probe" | Out-Null
    try {
        $refN9 = Invoke-PublishStableRefusal -Root $rootN
    } finally {
        Close-PublishLocks
    }
    Check "N9 refusal precedes the publish lock (guard ordering is behavioral)" {
        $refN9.Exit -ne 0 -and $refN9.Text -match 'not a PiPlay install' -and $refN9.Text -notmatch 'already running'
    }
}
finally {
    Write-Host ""
    if (Test-Path -LiteralPath $sandbox) {
        Remove-Item -LiteralPath $sandbox -Recurse -Force -ErrorAction SilentlyContinue
    }
}

Write-Host "--- $pass passed, $fail failed ---" -ForegroundColor $(if ($fail -gt 0) { "Red" } else { "Green" })
if ($fail -gt 0) { exit 1 }
exit 0
