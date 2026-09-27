# Repo-less SND-DESK release handoff

This is the operational resume guide for PiPlay's repository-free SND-DESK
acceptance flow. The product contract remains
[`PiPlay_Product_Engineering_Spec.md`](PiPlay_Product_Engineering_Spec.md); this
file records the resumable checkpoint, activation gates, and operator commands.

The delivery repository is `devseviq/PiPlay` (`origin`). The upstream repository
is not the target for these workflows or release downloads.

## Authority and boundaries

- SND-HOST owns the repository, feature branches, builds, GitHub publication,
  and Stable promotion.
- SND-DESK has no source authority. It accepts packages only from GitHub
  Releases and performs downloaded-package and interactive acceptance.
- A test prerelease is test evidence only. It is never Stable release evidence.
- The Stable runtime is replaced only by the existing Stable scripts from a
  clean, committed, merged release commit.
- This handoff does not authorize a push, merge, GitHub settings change,
  publication, Stable promotion, SND-DESK transport, or checkout removal.
  Reconfirm the requested operation and its live prerequisites first.
- REMOTE is not enrolled and is outside this procedure.

Do not place credentials in this repository, command history, logs, packages,
or this guide. Do not copy a repository, build tree, or host-only state to
SND-DESK.

## Beta.3 test candidate — 2026-09-27

The candidate is **0.14.0-beta.3, build 42**. It contains the source fixes
through `12fef1a5581accaf2b4f1a1459e61922aff649cd` plus the release stamps
and documentation updates. Use the [GitHub Releases page](https://github.com/devseviq/PiPlay/releases)
for the published package's complete tag, exact merged source commit, source
CI run, ZIP, and checksum; this source document does not attest publication.

The [beta.3 changelog](CHANGELOG.md) describes its held-shortcut, Auto navigation,
player sync warning, and release-tooling changes. Beta.2 does not contain them.
Follow the [interactive checklist](#snd-desk-interactive-checklist) on the exact
downloaded beta.3 package. Startup smoke, live playback/audio, renderer
recovery, and mixed-DPI results must identify the package and machine tested;
SND-DESK acceptance remains pending until those results are recorded.

## Pre-publication checkpoint — 2026-09-27

The following snapshot was captured before beta.3 preparation. It explains
the starting point, not the current release list or branch state.

The current source passes the local gate, but its latest fixes are not yet
available in a SND-DESK download. The published beta.2 package can be tested
for its own behavior; it cannot validate the Unreleased changes. Refresh this
dated checkpoint before publication or acceptance.

| Item | Verified checkpoint |
|---|---|
| Local source | `12fef1a5581accaf2b4f1a1459e61922aff649cd` on `polish/review-2026-09-10` |
| Remote `main` | `78a39146e3968c76722ab285c0d8d891c1b62220`; local source is 20 commits ahead |
| Remote feature branch | `969ba083d4568902493646c8c84130eb5467d4f8`; 18 local commits have not been pushed |
| Source stamps | `0.14.0-beta.2`, build `41`, unchanged since the published beta; use the source commit to distinguish them |
| Working tree at source verification | Tracked files clean; pre-existing untracked `.qoder/` preserved. This documentation refresh follows the verified source commit |
| Full local gate | `LOCAL CI: PASS`; 1,642 tests passed, 0 failed/skipped; Release build had 0 warnings/errors; deploy-swap harness 84 passed and publish-lock harness 8 passed |
| Required GitHub CI | [Successful main CI](https://github.com/devseviq/PiPlay/actions/runs/36058308184) covers `78a3914`; the local candidate has not reached required GitHub CI |
| Latest published package | [0.14.0-beta.2, build 41](https://github.com/devseviq/PiPlay/releases/tag/test-78a39146e3968c76722ab285c0d8d891c1b62220-r36058308184-a1), published 2026-09-25, source `78a3914`, visible prerelease |
| Download verification | Fresh GitHub ZIP and checksum matched; `Test-DownloadedPackage.ps1 -Kind Test -ValidateOnly` verified inventory, hashes, binary identity, and exact source commit |
| Automated publication | `PIPLAY_RELEASE_POLICY_TOKEN` is absent from the Actions secret listing; the latest [publication run](https://github.com/devseviq/PiPlay/actions/runs/36059802202) failed at `Create GitHub test prerelease`. Beta.2 was published through the local-build route |
| Other provider gates | Actions permissions and tag rulesets were not rechecked in this refresh; verify them before either publication route |
| SND-DESK acceptance | Not run in this refresh; current desk installation, dependencies, startup, playback/audio, and mixed-DPI behavior remain unverified |

The verified beta.2 ZIP SHA256 is
`0f651fe7765051eb73a0965827cb607c7f00ed0f6a3110c691d2fa6a2edb3d5b`.
Its exact tag is
`test-78a39146e3968c76722ab285c0d8d891c1b62220-r36058308184-a1`.
No new package was published and no Stable or SND-DESK runtime was changed
during this readiness check.

Before SND-DESK can test the latest fixes, land the intended changes through a
PR and required CI, publish a new exact-source test package, and verify that
download. The local source includes held-shortcut, Auto navigation, and player
sync hint fixes now listed under [beta.3](CHANGELOG.md), plus the
release-script repairs described below. Preserve `.qoder/` and any later local
edits when preparing a clean candidate checkout.

This checkpoint supersedes the 2026-09-04 activation checkpoint. The delivery
workflows are already on `main`, GitHub access works, and test prereleases
exist; do not replay the old delivery-branch cherry-pick sequence.

## Delivery contract

The existing delivery implementation provides:

- a manual `Publish test download` workflow that builds an exact-source,
  non-release package and publishes it as a uniquely tagged GitHub prerelease;
- a `Publish Stable download` tag workflow that rebuilds the exact Stable tag
  and publishes a permanent GitHub Release ZIP and SHA256 file;
- complete downloaded-package verification, including exact file inventory,
  hashes, binary identity, baked Stable channel, source commit, and
  test-versus-release evidence;
- immutable-tag policy checks for `refs/tags/test-*` and
  `refs/tags/stable-v*`, including active enforcement, no exclusions, no bypass
  actors, and restrictions on updates and deletion;
- separate policy-verification and publication credentials;
- atomic test-tag creation before a draft prerelease is uploaded, followed by
  tag checks before and after publication;
- behavioral and policy tests for the publication boundary.

## Resume sequence

### 1. Verify the machine and recover the exact lane

Run this on SND-HOST before a machine-sensitive write:

```powershell
$verifier = Join-Path $env:LOCALAPPDATA 'common_dev\v2\Test-LocalMachineIdentity.ps1'
$identity = & $verifier -AsJson |
    ConvertFrom-Json
if ($identity.status -cne 'VERIFIED' -or
    $identity.machineId -cne 'snd-host' -or
    $identity.instanceId -cne '13af5dd3-9cfe-4c8f-82ef-806f256cc1c2') {
    throw 'This handoff is valid only on the enrolled SND-HOST instance.'
}
```

Locate the existing worktree without assuming a user-specific path:

```powershell
git worktree list --porcelain
```

Verify elevation separately; the local build/test gate does not require
administrator privileges. In the candidate worktree, verify the lane and
preserve unexpected state:

```powershell
git status --short --branch
git branch --show-current
git rev-parse HEAD
git log --oneline --decorate -12
```

Compare the source with the dated checkpoint above and account for later
changes. Use an isolated candidate worktree for integration and packaging if
the existing checkout has unrelated files or active writers. Never reset,
clean, stage, or discard unattributed changes.

### 2. Verify GitHub access and refresh the delivery base

These checks identify the live delivery repository:

```powershell
Resolve-DnsName github.com
gh auth status
git ls-remote --heads origin main
```

Fetching changes local remote-tracking state but does not change the worktree:

```powershell
git fetch origin
git rev-parse origin/main
git log --left-right --graph --cherry-pick --oneline origin/main...HEAD
git diff --stat origin/main...HEAD
```

If authentication fails, use the GitHub CLI's attended login or refresh flow.
Do not paste a token into a command or document.

### 3. Prepare the exact candidate

Review all commits and the final diff against refreshed `origin/main`. Prepare
a machine-namespaced `snd-host/...` candidate branch without rewriting the
existing feature branch. Include the intended fixes and documentation, and
commit the chosen prerelease version/build stamps if they change. The source
commit, package manifest, and release tag remain the exact candidate identity;
the current version/build stamps alone do not distinguish the unpublished fixes.

### 4. Run the complete local gate

From the selected PR branch:

```powershell
pwsh -NoProfile -File .\scripts\Test-LocalCI.ps1
git diff --check
git status --short --branch
```

The exit gate is `LOCAL CI: PASS`, no diff-check errors, and a clean candidate
worktree. Rerun it on the final candidate; the checkpoint's source result does
not certify later code changes.

### 5. Push, review, and merge

Only with current authorization:

```powershell
$branch = git branch --show-current
git push --set-upstream origin $branch
gh pr create --base main --head $branch
```

The PR must show only the intentionally selected commits, and the
GitHub-hosted `Build and test (Windows)` check must pass. Merge through the
repository's normal protected-branch flow. Do not publish from the feature
branch.

## GitHub activation prerequisites

Before dispatching either publication workflow, verify live provider state:

1. Actions can grant the workflow token `contents: write`.
2. The Actions secret `PIPLAY_RELEASE_POLICY_TOKEN` exists. It is a fine-grained
   token scoped only to `devseviq/PiPlay`, with Administration write access and
   no Contents permission. GitHub requires write access to the ruleset to expose
   its bypass actors. The workflow uses this token only to inspect policy;
   publication continues to use `github.token`.
3. One or more active tag rulesets cover the exact include patterns
   `refs/tags/test-*` and `refs/tags/stable-v*`.
4. Each qualifying ruleset has no exclusions, no bypass actors, and restricts
   both tag updates and tag deletion.
5. The merged default branch contains the applicable workflow and scripts.

Useful read-only checks after authentication:

```powershell
gh secret list --app actions --repo devseviq/PiPlay
gh workflow list --repo devseviq/PiPlay
gh api repos/devseviq/PiPlay/actions/permissions/workflow
gh api --paginate 'repos/devseviq/PiPlay/rulesets?includes_parents=true'
```

Secret listing confirms only the name, not that its value or permissions are
correct. The workflows fail closed by running
`.github/scripts/Test-StableTagPolicy.ps1` against the required pattern. The
gate verifies the returned empty `bypass_actors` list, not a token's scope label.

Changing workflow permissions, secrets, or rulesets is an external access-control
write and requires explicit operator authorization. Configure provider state
before creating a Stable tag or dispatching the test workflow.

## Publish and accept a test prerelease

After the PR is merged and local `origin/main` is refreshed:

```powershell
git fetch origin
$expectedCommit = (git rev-parse origin/main).Trim()
gh workflow run 'Publish test download' --repo devseviq/PiPlay --ref main
gh run list --repo devseviq/PiPlay --workflow 'Publish test download' `
    --event workflow_dispatch --limit 5 `
    --json databaseId,headSha,status,conclusion,url
```

Match the selected run's `headSha` to `$expectedCommit`; do not assume the most
recent run is yours. Wait for success, then verify on GitHub that the release:

- is visible, non-draft, and marked prerelease;
- uses a tag shaped as
  `test-<40-character-commit>-r<run-id>-a<attempt>`;
- has that exact commit as its tag target;
- contains the matching ZIP and `.sha256` assets; and
- says `NOT RELEASE EVIDENCE` in its notes.

On SND-DESK, download only those two assets from the GitHub Releases page.
PowerShell 7, the .NET 10 Desktop Runtime, and WebView2 Evergreen are required.
From the download directory:

```powershell
$ErrorActionPreference = 'Stop'
$commit = '<40-character commit from the release tag>'
$tag = '<complete test tag from the GitHub Release>'
$zip = ".\PiPlay-$tag.zip"
$checksum = "$zip.sha256"
$expectedHash = ((Get-Content -LiteralPath $checksum -Raw) -split '\s+')[0]
$actualHash = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash
if ($actualHash -ine $expectedHash) { throw 'Downloaded test ZIP hash mismatch.' }
Expand-Archive -LiteralPath $zip -DestinationPath ".\PiPlay-$tag"
Set-Location -LiteralPath ".\PiPlay-$tag"
pwsh -NoProfile -File .\scripts\Test-DownloadedPackage.ps1 `
    -Kind Test -ExpectedCommit $commit
if ($LASTEXITCODE -ne 0) { throw 'Package verification or startup smoke failed.' }
```

The packaged verifier checks provenance and launches the automated UI smoke
with disposable data and evidence outside the immutable package root. Close
other PiPlay instances first: downloaded packages share a single-instance
identity. Use an active, unlocked desktop for the startup smoke. With
`-ValidateOnly`, the verifier checks the package without launching PiPlay;
that result does not establish startup or interactive acceptance.

Acceptance is tied to the exact commit and test tag. A passed test prerelease
does not authorize Stable promotion by itself.

### SND-DESK interactive checklist

Use the verified downloaded package, then record its tag, source commit,
version/build, Windows version, WebView2 version, display scaling, and results.
Keep screenshots and logs outside the immutable package. Record each case as
pass, fail, or not run, with reproduction steps for failures. The current
checkpoint has no SND-DESK results.

| Check | Expected result |
|---|---|
| Package and startup | ZIP checksum and complete package verification pass; the packaged UI smoke opens the correct executable and records a nonblank screenshot |
| Pop out and return/close (Q-1) | Repeat on a playing and a paused video; hear only one audio stream and preserve position, volume, mute, speed, and play/pause state |
| Ads, autoplay, playlist/mix | Repeat transfers across available transitions; listen for overlap and verify YouTube controls, captions, and ad disclosure remain usable. Record unavailable account/ad cases as not run |
| Auto | Bring a video back and confirm no immediate repeat popout. On beta.3, leave for Home/search and return to the same video; Auto should pop it out again |
| Shortcuts and windows | Exercise the README shortcuts, top-bar drag/double-click, corner parking, preset sizes, Pin, Fade, and Expand. On the new candidate, hold a shortcut while pressing/releasing an unrelated key; it should still act only once |
| Recovery and sync hint | On a disposable test profile, terminate only a renderer belonging to this PiPlay instance and check Retry/return recovery. If **Player sync degraded** appears on the new candidate, verify it clears after the failing operations recover or their browser surface closes |
| Displays and themes | Move between monitors with different scaling; check resizing, preset aspect ratio, theme contrast, the profile-menu shadow, and Focused controls against YouTube's ad disclosure |
| Settings and restart | Restart the downloaded copy and confirm window positions, profiles, and presentation settings persist |

Brief audio overlap, playlist queue position, and mixed-DPI appearance remain
known acceptance gaps. A package or startup pass does not close them. Redact
logs/screenshots before feedback; never attach browser profiles, cookies, or
credentials.

### Local test publication while the Actions policy secret is pending

SND-HOST may publish a test package with the existing scripts and its current
GitHub CLI identity while a scoped Actions policy credential is being provisioned:

1. Fetch `origin/main` and use a clean checkout at that exact merged commit.
   Require a successful GitHub `Build and test (Windows)` run on `main` whose
   `headSha` matches it, then run `scripts/Test-LocalCI.ps1` locally.
2. Build with `scripts/Build-PiPlay.ps1 -Stage Publish -Configuration Release
   -Channel Stable -NoVersionBump -NoBuildNumberBump`, an external `-PublishRoot`,
   `-PublishLabel test-<commit>`, `-NoLatest -NoVersionTable -StopProcessName ''`,
   and `-NonReleaseReason 'GitHub test prerelease; interactive verification pending on SND-DESK'`.
3. Run the packaged `scripts/Test-DownloadedPackage.ps1 -Kind Test
   -ExpectedCommit <commit> -ValidateOnly`. Archive the complete payload with
   `System.IO.Compression.ZipFile.CreateFromDirectory`, extract the ZIP into a
   fresh external directory, and reverify it with the trusted checkout's
   `scripts/Test-DownloadedPackage.ps1 -Kind Test -Root <extracted-root>
   -ExpectedCommit <commit> -ValidateOnly`. Write the ZIP's SHA256 companion file.
4. Use `test-<commit>-r<successful-source-CI-run-id>-a<publication-attempt>` as the
   unique tag and ZIP identity. Keep the authenticated CLI credential only in
   the publication process's `GH_TOKEN` and `PIPLAY_RELEASE_TOKEN` environment
   variables; restore their prior values afterward. Do not print the credential
   or store it as an Actions secret.
5. Invoke `.github/scripts/Publish-TestPrerelease.ps1` with that tag, commit,
   archive, checksum, repository, and title. Its active tag-policy, visible
   empty-bypass-list, atomic tag-creation, and draft/publication verification
   gates remain mandatory. Give the release a title that identifies the local
   build, then update its notes before handing it to the desk agent.
6. The release notes and build receipt must state that the ZIP was built on
   SND-HOST, link the successful source CI run, and explain that the tag's run ID
   identifies source verification rather than ZIP production. Retain
   `NOT RELEASE EVIDENCE` and pending SND-DESK acceptance. Verify the visible
   prerelease, exact remote tag target, ZIP, checksum, and downloaded ZIP hash.

This route delivers the same test-package contract. It does not verify the
Actions policy secret or establish that the automated publication workflow is
ready, and it does not satisfy the separate Stable readiness gate.

## Separate next-Stable readiness gate

Beta.3 contains repairs for the release-path findings in the
[2026-09-02 review](reviews/review-controller-2026-09-02-piplay-readiness.md).
The beta.2 ZIP does not contain these repairs. Verify the final merged source
and downloaded package identity before using them as test evidence.

| Finding | Local implementation and remaining evidence |
|---|---|
| F-1 | Deploy-root guards reject unrelated content and repository overlap before mutation; repair requires evidence for the affected payload names. The deploy-swap harness passed 84 checks |
| F-6 | Stable publishing runs `Test-LocalCI.ps1`; `-SkipTests` produces diagnostic, non-release evidence and creates no Stable tag |
| F-7 | UI smoke binds its target and evidence to the deployed Stable identity. The live default-path smoke on the intended deployment remains pending |
| F-8 | Deploy-swap and publish-lock harnesses are included in the canonical local/GitHub gate; both passed locally |
| A-2 | A failed final deployment verification removes the Stable tag created by that run; tags remain local until explicitly pushed |

Before the next `Publish-Stable.ps1` run, land and verify the intended repairs
on merged `main`, then satisfy the clean-source and deployed-copy gates below.
Live renderer recovery (F-4), the deployed default-path smoke (F-7), and attended
audio/journey acceptance (Q-1) remain separate checks. Source tests and a test
prerelease do not establish those results.

The existing Stable swap still treats `.staging` and `.backup` siblings beside
a complete installation as deploy-owned and may delete them without proving
their contents belong to PiPlay. The missing-root safeguards do not cover that
case. Resolve foreign sibling custody before Stable deployment; test-package
build and download verification do not invoke that deployment path.

## First Stable release after activation

Stable promotion remains a separate SND-HOST operation:

1. Close the separate next-Stable readiness gate above.
2. Select the exact release `VERSION` and `BUILD_NUMBER`, commit them, and land
   that release-stamp commit through the normal PR and required CI flow.
3. Start from that merged, fetched `main` commit with a clean worktree. The
   stamps must already be committed; do not use `-AllowVersionBump` for release
   evidence.
4. Set `PIPLAY_STABLE_ROOT` to the dedicated machine-local Stable directory.
5. Run `Publish-Stable.ps1`, `Verify-StableDeploy.ps1`, and the deployed UI
   smoke exactly as documented in [`RELEASING.md`](RELEASING.md#stable-acceptance). A successful exact-source publish
   creates the local `stable-vX.Y.Z-bN` tag only after pre-tag deploy
   verification.
6. Complete the attended audio/journey acceptance on the verified deployed copy.
7. Push the corresponding `stable-vX.Y.Z-bN` tag only after the local release
   evidence, manual acceptance, and provider tag-policy gate are satisfied.
8. Verify that `Publish Stable download` succeeds and produces a normal,
   non-prerelease GitHub Release with the ZIP and SHA256 assets for that exact
   tag and commit.

Never use source output, `bin` output, a test prerelease, a dirty tree, or an
unmerged commit as Stable release evidence.

## SND-DESK checkout retirement gate

Retire an old SND-DESK repository checkout only after the downloaded-package
flow has passed on SND-DESK and the user has explicitly authorized removal.
Before removal, inspect branch, status, untracked and ignored files, stashes,
unique commits, active processes, and open handles. Any unique or unattributed
state needs a preserve-or-discard decision. Checkout retirement is not part of
publication and must not be inferred from this handoff.

## Stop conditions

Stop rather than improvise if any of these is true:

- machine identity is not the exact enrolled SND-HOST instance;
- the publication candidate is dirty, moved, or actively written by another process;
- actual remote `main` cannot be fetched and identified;
- the intended fixes are not in the selected merged source commit;
- full local CI or required GitHub CI fails;
- provider permissions, secret scope, ruleset coverage, exclusions, or bypass
  actors cannot be verified;
- a tag already exists unexpectedly or does not identify the selected commit;
- a release is draft/normal/prerelease in the wrong state or its assets do not
  match;
- downloaded SHA256, manifest, inventory, channel, source commit, or evidence
  classification fails; or
- SND-DESK acceptance or old-checkout disposition is incomplete.

At each stop, preserve the exact branch, commit, run URL, tag, release URL,
error text, and worktree state. Do not weaken a gate to advance the workflow.
