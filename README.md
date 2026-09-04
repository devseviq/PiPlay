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

SND-DESK has no repository checkout. It downloads either a temporary `PiPlay-test-<commit>` Actions artifact or a permanent Stable ZIP from GitHub Releases. Every package contains its own hash-covered verifier and UI-smoke entrypoint.

## Downloaded test packages

Manually dispatch the `CI` workflow for the commit to test. Download and extract its `PiPlay-test-<commit>` artifact on SND-DESK, open PowerShell 7 in the extracted directory, and run:

```powershell
$commit = '<40-character commit from the artifact name>'
pwsh -NoProfile -File .\scripts\Test-DownloadedPackage.ps1 -Kind Test -ExpectedCommit $commit
```

The command binds the package to the commit shown by GitHub, verifies the complete package and baked Stable channel, then launches the automated UI smoke. Test packages are explicitly marked as non-release evidence and expire with GitHub Actions artifact retention. Downloaded packages require PowerShell 7, the .NET 10 Desktop Runtime, and WebView2 Evergreen.

## Stable acceptance

Set `PIPLAY_STABLE_ROOT` to the machine-local Stable directory:

```powershell
$stableRoot = $env:PIPLAY_STABLE_ROOT
if ([string]::IsNullOrWhiteSpace($stableRoot)) { throw 'Set PIPLAY_STABLE_ROOT first.' }
.\scripts\Publish-Stable.ps1 -DeployRoot $stableRoot
.\scripts\Verify-StableDeploy.ps1 -DeployRoot $stableRoot
pwsh -NoProfile -File .\scripts\Test-UiSmoke.ps1 -ExePath (Join-Path $stableRoot 'PiPlay.exe')
```

After the verified tag is pushed, `.github/workflows/release.yml` rebuilds that exact tag and attaches a permanent ZIP plus SHA256 file to its GitHub Release. Publication fails closed unless GitHub has an active tag ruleset for `refs/tags/stable-v*` with both restrict-updates and restrict-deletions enabled; configure that provider rule before creating a Stable tag. On SND-DESK, extract the ZIP and run:

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
