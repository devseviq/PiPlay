#requires -Version 7
<#
.SYNOPSIS
  Manual end-to-end UI smoke for PiPlay: launches a published Stable exe, asserts key UI elements via
  UI Automation, and captures a screenshot for the final deployed smoke.
.DESCRIPTION
  Final deployed-window smoke (spec 22.2). NOT part of `dotnet test` — it needs an
  interactive desktop, the WebView2 runtime, and network. It checks the five named Source controls
  and captures the rendered window from an isolated data root; real playback/audio acceptance
  remains an end-user check. The capture is per-monitor-DPI aware, foregrounds the actual PiPlay
  HWND, and rejects blank/uniform frames instead of reporting a false pass.

  The PASS is bound to the copy it actually ran (readiness review F-7): the target must be a
  fully-qualified path - with no -ExePath it resolves to $env:PIPLAY_STABLE_ROOT\PiPlay.exe, the
  deployed Stable copy - and ONE resolved path is used for the marker directory, the manifest
  cross-check and the launch, so the identity gate cannot vouch for one directory while a
  different PiPlay.exe starts. The run refuses an exe whose identity is not proven: its
  .piplay.publish.marker must declare channel=Stable with a semver-shaped version and a numeric
  build number, and where build-info.json sits beside the exe its version, buildNumber,
  publishLabel and sourceCommit must all agree with the marker. That rules out source and
  bin\publish output, which CLAUDE.md forbids as evidence. A verified package payload gets the
  same identity through -MarkerPath, which scripts\Test-DownloadedPackage.ps1 materialises outside
  the package root from the manifest it has just hash-checked. The marker's releaseEvidence flag
  is echoed into every message, so a test-prerelease smoke can never be read as release proof.
  The screenshot is named with the marker's version, build number and source commit.
.EXAMPLE
  pwsh -File scripts/Test-UiSmoke.ps1
  # Smokes the deployed Stable copy at $env:PIPLAY_STABLE_ROOT\PiPlay.exe.
.EXAMPLE
  pwsh -File scripts/Test-UiSmoke.ps1 -ExePath (Join-Path $env:PIPLAY_STABLE_ROOT 'PiPlay.exe')
#>
param(
    [string]$ExePath,
    [string]$EvidenceDir = "$PSScriptRoot\..\docs\evidence",
    [string]$DataRoot,
    [string]$MarkerPath,
    [int]$ReadyTimeoutSec = 30
)

$ErrorActionPreference = 'Stop'

# Review F-7 follow-up: a relative path resolves differently for Test-Path (session location),
# [IO.Path]::GetFullPath (process CWD) and Start-Process (session location), so the identity gate
# could validate one directory while launching another PiPlay.exe. Require one fully qualified path
# and resolve it exactly once (same rule as Publish-Stable's DeployRoot guard).
function Resolve-SmokePath {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Label
    )

    $root = [System.IO.Path]::GetPathRoot($Path)
    if (-not (($root -match '^[A-Za-z]:\\$') -or ($root -match '^\\\\[^\\]+\\[^\\]+$'))) {
        throw "$Label must be a fully qualified absolute path with its own drive or UNC share (got '$Path')."
    }
    return [System.IO.Path]::GetFullPath($Path)
}

if ([string]::IsNullOrWhiteSpace($ExePath)) {
    $stableRoot = [Environment]::GetEnvironmentVariable('PIPLAY_STABLE_ROOT')
    if ([string]::IsNullOrWhiteSpace($stableRoot)) {
        throw "No -ExePath and PIPLAY_STABLE_ROOT is unset; refusing to smoke an arbitrary build. " +
              "Set PIPLAY_STABLE_ROOT to the deployed Stable directory (docs\RELEASING.md) or pass " +
              "-ExePath to a published copy."
    }
    $ExePath = Resolve-SmokePath -Path (Join-Path $stableRoot 'PiPlay.exe') -Label 'PIPLAY_STABLE_ROOT'
} else {
    $ExePath = Resolve-SmokePath -Path $ExePath -Label '-ExePath'
}
if (-not (Test-Path -LiteralPath $ExePath -PathType Leaf)) {
    throw "PiPlay.exe not found at '$ExePath'."
}

$exeDir = Split-Path -Parent $ExePath
if ([string]::IsNullOrWhiteSpace($MarkerPath)) {
    $MarkerPath = Join-Path $exeDir '.piplay.publish.marker'
} else {
    # Trusted-orchestrator override, used by Test-DownloadedPackage.ps1 so the identity marker
    # stays OUTSIDE the extracted package (a file inside the root would fail the verifier's own
    # inventory check on re-run). The value is still fully qualified and still must pass every
    # identity assertion below - it changes where identity is read from, never whether it is proven.
    $MarkerPath = Resolve-SmokePath -Path $MarkerPath -Label '-MarkerPath'
}
if (-not (Test-Path -LiteralPath $MarkerPath -PathType Leaf)) {
    throw "No .piplay.publish marker at '$MarkerPath': this is not a published Stable copy. " +
          "Deploy with Publish-Stable.ps1 or verify a downloaded package with Test-DownloadedPackage.ps1, " +
          "and smoke THAT copy - source and bin output are never evidence (CLAUDE.md)."
}
$marker = @{}
foreach ($line in @(Get-Content -LiteralPath $MarkerPath)) {
    $kv = $line -split '=', 2
    if ($kv.Count -eq 2 -and -not [string]::IsNullOrWhiteSpace($kv[0]) -and -not [string]::IsNullOrWhiteSpace($kv[1])) {
        $marker[$kv[0].Trim()] = $kv[1].Trim()
    }
}
if ($marker['channel'] -cne 'Stable') {
    throw "Publish marker '$MarkerPath' declares channel '$($marker['channel'])', expected 'Stable'."
}
$identityVersion = [string]$marker['version']
$identityBuild   = [string]$marker['buildNumber']
$identityCommit  = [string]$marker['sourceCommit']
# The identity lands in the screenshot filename, so validate its shape here rather than trusting
# GDI+ to refuse a traversal like version='..\..\x' after the app is already running.
if ($identityVersion -notmatch '^[0-9]+\.[0-9]+\.[0-9]+(?:-[0-9A-Za-z.-]+)?$' -or
    $identityBuild -notmatch '^[0-9]+$') {
    throw "Publish marker '$MarkerPath' carries malformed identity (version '$identityVersion', buildNumber '$identityBuild'); refusing to derive an evidence filename from it."
}

# A published copy also carries build-info.json beside the exe; where it does, ALL four stamps must
# agree with the marker (mirroring Verify-StableDeploy.ps1), so evidence cannot be stamped with a
# commit or publishLabel the on-disk manifest contradicts.
$buildInfoPath = Join-Path $exeDir 'build-info.json'
if (Test-Path -LiteralPath $buildInfoPath -PathType Leaf) {
    $smokeBuildInfo = Get-Content -LiteralPath $buildInfoPath -Raw | ConvertFrom-Json
    foreach ($stamp in @(
        @{ Name = 'version';      Marker = $identityVersion;                 Manifest = [string]$smokeBuildInfo.version },
        @{ Name = 'buildNumber';  Marker = $identityBuild;                   Manifest = [string]$smokeBuildInfo.buildNumber },
        @{ Name = 'publishLabel'; Marker = [string]$marker['publishLabel'];  Manifest = [string]$smokeBuildInfo.publishLabel },
        @{ Name = 'sourceCommit'; Marker = $identityCommit;                  Manifest = [string]$smokeBuildInfo.sourceCommit })) {
        if ($stamp.Marker -ne $stamp.Manifest) {
            throw "Publish marker and build-info.json beside '$ExePath' disagree on $($stamp.Name): marker='$($stamp.Marker)' manifest='$($stamp.Manifest)'."
        }
    }
}
$releaseFlag = [string]$marker['releaseEvidence']
if ($releaseFlag -cne 'True') {
    Write-Warning "Marker releaseEvidence=$releaseFlag (publishLabel '$($marker['publishLabel'])'): a SMOKE PASS on this copy is package/diagnostic evidence, NOT Stable release proof."
}
$commitStamp = if ($identityCommit -match '^[0-9a-fA-F]{7,}$') {
    $identityCommit.Substring(0, [Math]::Min(12, $identityCommit.Length)).ToLowerInvariant()
} else { 'no-commit' }
Write-Host "Smoke target: $ExePath" -ForegroundColor Cyan
Write-Host "Identity    : v$identityVersion b$identityBuild @ $commitStamp releaseEvidence=$releaseFlag" -ForegroundColor Cyan
New-Item -ItemType Directory -Force -LiteralPath $EvidenceDir | Out-Null

$ownsDataRoot = [string]::IsNullOrWhiteSpace($DataRoot)
if ($ownsDataRoot) {
    $DataRoot = Join-Path ([IO.Path]::GetTempPath()) ("PiPlayUiSmokeData-" + [Guid]::NewGuid().ToString("N"))
}
New-Item -ItemType Directory -Force -LiteralPath $DataRoot | Out-Null

Add-Type @'
using System;
using System.Runtime.InteropServices;

public static class PiPlayUiSmokeNative
{
    [DllImport("user32.dll")]
    public static extern IntPtr SetThreadDpiAwarenessContext(IntPtr dpiContext);

    [DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern bool ShowWindow(IntPtr hWnd, int command);

    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
}
'@

# UI Automation reports physical pixels. CopyFromScreen must run in the same per-monitor-v2
# coordinate space or a window on a scaled secondary monitor can capture an unrelated/black region.
$previousDpiContext = [PiPlayUiSmokeNative]::SetThreadDpiAwarenessContext([IntPtr](-4))
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Windows.Forms, System.Drawing

$previousDataRoot = [Environment]::GetEnvironmentVariable('PIPLAY_DATA_ROOT', 'Process')
$proc = $null
try {
    [Environment]::SetEnvironmentVariable('PIPLAY_DATA_ROOT', $DataRoot, 'Process')
    try {
        $proc = Start-Process -FilePath $ExePath -PassThru
    }
    finally {
        [Environment]::SetEnvironmentVariable('PIPLAY_DATA_ROOT', $previousDataRoot, 'Process')
    }

    $deadline = (Get-Date).AddSeconds($ReadyTimeoutSec)
    $root = $null
    while ((Get-Date) -lt $deadline -and -not $root) {
        Start-Sleep -Milliseconds 400
        $root = [System.Windows.Automation.AutomationElement]::RootElement.FindFirst(
            [System.Windows.Automation.TreeScope]::Children,
            (New-Object System.Windows.Automation.PropertyCondition(
                [System.Windows.Automation.AutomationElement]::ProcessIdProperty, $proc.Id)))
    }
    if (-not $root) { throw "PiPlay main window did not appear within $ReadyTimeoutSec s." }

    function Assert-Element([string]$automationId, [string]$label) {
        $cond = New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::AutomationIdProperty, $automationId)
        $el = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cond)
        if (-not $el) { throw "MISSING UI element: $label (AutomationId=$automationId)" }
        Write-Host "OK  $label" -ForegroundColor Green
    }

    # WPF maps x:Name -> AutomationId, so these match the named controls in MainWindow.xaml.
    Assert-Element 'PopOutButton'  'Pop out video button'
    Assert-Element 'UrlBox'        'URL / address box'
    Assert-Element 'CloseButton'   'Close caption button'
    Assert-Element 'ProfilesCombo' 'Profiles dropdown'
    Assert-Element 'SettingsButton' 'Settings gear button'

    $windowHandle = [IntPtr]$root.Current.NativeWindowHandle
    if ($windowHandle -eq [IntPtr]::Zero) { throw 'PiPlay main window has no native HWND.' }
    [void][PiPlayUiSmokeNative]::ShowWindow($windowHandle, 9) # SW_RESTORE
    [void][PiPlayUiSmokeNative]::SetForegroundWindow($windowHandle)
    $root.SetFocus()
    Start-Sleep -Milliseconds 750
    $foregroundProcessId = [uint32]0
    [void][PiPlayUiSmokeNative]::GetWindowThreadProcessId(
        [PiPlayUiSmokeNative]::GetForegroundWindow(), [ref]$foregroundProcessId)
    if ($foregroundProcessId -ne [uint32]$proc.Id) {
        throw 'PiPlay main window could not be foregrounded for a trustworthy rendered capture.'
    }

    # Screenshot the window region for the chrome-acceptance review (section 22.2).
    $rect = $root.Current.BoundingRectangle
    if ($rect.Width -lt 1 -or $rect.Height -lt 1) { throw 'PiPlay main window has an empty capture rectangle.' }
    $bmp = New-Object System.Drawing.Bitmap([int]$rect.Width, [int]$rect.Height)
    try {
        $g = [System.Drawing.Graphics]::FromImage($bmp)
        try {
            $g.CopyFromScreen([int]$rect.X, [int]$rect.Y, 0, 0, $bmp.Size)
        }
        finally {
            $g.Dispose()
        }

        $colors = [System.Collections.Generic.HashSet[int]]::new()
        $stepX = [Math]::Max(1, [int]($bmp.Width / 24))
        $stepY = [Math]::Max(1, [int]($bmp.Height / 16))
        for ($y = 0; $y -lt $bmp.Height; $y += $stepY) {
            for ($x = 0; $x -lt $bmp.Width; $x += $stepX) {
                [void]$colors.Add($bmp.GetPixel($x, $y).ToArgb() -band 0x00FFFFFF)
            }
        }
        if ($colors.Count -lt 4) {
            throw "Rendered capture is blank or uniform ($($colors.Count) sampled color(s)); refusing a false smoke pass."
        }

        # Stamped with the identity under test so a filed screenshot can never be a stale dev build.
        $shot = Join-Path $EvidenceDir ("ui-smoke-v{0}-b{1}-{2}-{3}.png" -f `
            $identityVersion, $identityBuild, $commitStamp, (Get-Date -Format 'yyyyMMdd-HHmmss'))
        $bmp.Save($shot, [System.Drawing.Imaging.ImageFormat]::Png)
    }
    finally {
        $bmp.Dispose()
    }
    Write-Host "Saved screenshot: $shot" -ForegroundColor Cyan
    Write-Host "SMOKE PASS  v$identityVersion b$identityBuild @ $commitStamp" -ForegroundColor Green
}
finally {
    if ($null -ne $proc -and -not $proc.HasExited) {
        $proc.CloseMainWindow() | Out-Null
        Start-Sleep -Seconds 1
        if (-not $proc.HasExited) { $proc.Kill() }
    }
    if ($previousDpiContext -ne [IntPtr]::Zero) {
        [void][PiPlayUiSmokeNative]::SetThreadDpiAwarenessContext($previousDpiContext)
    }
    if ($ownsDataRoot -and (Test-Path -LiteralPath $DataRoot)) {
        for ($attempt = 1; $attempt -le 5; $attempt++) {
            try {
                Remove-Item -LiteralPath $DataRoot -Recurse -Force -ErrorAction Stop
                break
            }
            catch {
                if ($attempt -eq 5) {
                    Write-Warning "Could not remove isolated UI-smoke data at '$DataRoot': $($_.Exception.Message)"
                }
                else {
                    Start-Sleep -Milliseconds 400
                }
            }
        }
    }
}
