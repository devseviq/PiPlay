# PiPlay

Windows WPF app (`net10.0-windows`, WebView2 Evergreen: `src/PiPlay/PiPlay.csproj`, `global.json`) that plays YouTube in one Video Popout. Product contract: [`docs/PiPlay_Product_Engineering_Spec.md`](docs/PiPlay_Product_Engineering_Spec.md).

Command-line help is owned by the executable:

```powershell
& .\PiPlay.exe --help
```

`-h` and `/?` are equivalent exact aliases. PowerShell `Get-Help` does not inspect native executables, so invoke PiPlay with one of those arguments instead.

```powershell
pwsh -NoProfile -File .\scripts\Test-LocalCI.ps1
```

`-Plan` prints the gate commands. The script owns Node/version checks, restore, Debug tests, temporary test data, and the non-mutating Release build.

## Development and promotion

SND-HOST owns the working repository, feature work, builds, and publication. Start from current `main`, use a machine-namespaced `snd-host/...` branch, run the local gate, and open a pull request. GitHub-hosted `Build and test (Windows)` is the required merge check.

SND-DESK has no repository checkout and accepts downloads only from GitHub Releases. It uses either a test prerelease or a Stable release; every package contains its own hash-covered verifier and UI-smoke entrypoint.

## Downloaded test packages

Manually dispatch the `Publish test download` workflow for the commit to test. It creates a uniquely tagged GitHub prerelease containing a ZIP and SHA256 file. Download both from the Releases page on SND-DESK, then run:

```powershell
$commit = '<40-character commit from the prerelease tag or notes>'
$tag = "test-$commit-r<run-id>-a<attempt>"
$zip = ".\PiPlay-$tag.zip"
$checksum = "$zip.sha256"
$expectedHash = ((Get-Content -LiteralPath $checksum -Raw) -split '\s+')[0]
$actualHash = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash
if ($actualHash -ine $expectedHash) { throw 'Downloaded test ZIP hash mismatch.' }
Expand-Archive -LiteralPath $zip -DestinationPath ".\PiPlay-$tag"
Set-Location -LiteralPath ".\PiPlay-$tag"
pwsh -NoProfile -File .\scripts\Test-DownloadedPackage.ps1 -Kind Test -ExpectedCommit $commit
```

The command binds the package to the commit shown by GitHub, verifies the complete package and baked Stable channel, then launches the automated UI smoke. Test prereleases are explicitly marked as non-release evidence and remain on the Releases page until deliberately removed; the workflow does not delete them automatically. Publishing requires the same `PIPLAY_RELEASE_POLICY_TOKEN` secret plus an active, exclusion-free, no-bypass tag ruleset for `refs/tags/test-*`, preventing the verified test tag from moving during publication. Downloaded packages require PowerShell 7, the .NET 10 Desktop Runtime, and WebView2 Evergreen.

## Stable acceptance

Set `PIPLAY_STABLE_ROOT` to the machine-local Stable directory:

```powershell
$stableRoot = $env:PIPLAY_STABLE_ROOT
if ([string]::IsNullOrWhiteSpace($stableRoot)) { throw 'Set PIPLAY_STABLE_ROOT first.' }
.\scripts\Publish-Stable.ps1 -DeployRoot $stableRoot
.\scripts\Verify-StableDeploy.ps1 -DeployRoot $stableRoot
pwsh -NoProfile -File .\scripts\Test-UiSmoke.ps1 -ExePath (Join-Path $stableRoot 'PiPlay.exe')
```

After the verified tag is pushed, `.github/workflows/release.yml` rebuilds that exact tag and attaches a permanent ZIP plus SHA256 file to its GitHub Release. Publication fails closed unless GitHub has an active, exclusion-free tag ruleset for `refs/tags/stable-v*` with restrict-updates, restrict-deletions, and no bypass actors. Store a fine-grained token with Administration read access as the `PIPLAY_RELEASE_POLICY_TOKEN` Actions secret so the workflow can verify that provider rule; the normal workflow token still performs publication. Configure both gates before creating a Stable tag. On SND-DESK, extract the ZIP and run:

```powershell
$tag = '<stable-vX.Y.Z-bN from the GitHub Release page>'
$zip = ".\PiPlay-$tag.zip"
$checksum = "$zip.sha256"
$expectedHash = ((Get-Content -LiteralPath $checksum -Raw) -split '\s+')[0]
$actualHash = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash
if ($actualHash -ine $expectedHash) { throw 'Downloaded Stable ZIP hash mismatch.' }
Expand-Archive -LiteralPath $zip -DestinationPath ".\PiPlay-$tag"
Set-Location -LiteralPath ".\PiPlay-$tag"
pwsh -NoProfile -File .\scripts\Test-DownloadedPackage.ps1 -Kind Release -ExpectedTag $tag
```

Do not use source or `bin` output as release evidence. On the verified downloaded copy: pop out a playing video and listen through launch and return/close (Q-1); repeat once with a playlist or mix when available; record unavailable ads/account/profile states as not run.
