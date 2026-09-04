# Repo-less SND-DESK release handoff

This is the operational resume guide for moving PiPlay to a repository-free
SND-DESK acceptance flow. The product contract remains
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

## Frozen checkpoint

The following is historical evidence captured on 2026-09-04. Refresh every
volatile fact before acting.

| Item | Checkpoint |
|---|---|
| Feature branch | `snd-host/repo-less-desk-downloads-20260904` |
| Delivery implementation head | `0891ab1abd741df0bfc90f55af97e6daa08e8c1d` |
| Feature commits | Nine delivery commits after `9f514c8`, followed by this handoff change |
| Local `main` | `9f514c8f843309a7d6f8cf07d15f26d4450f58c5` |
| Recorded `origin/main` | `3fa16131f27e5ff3ded787babab43694f6b3d4f0` |
| Upstream | None configured for the feature branch |
| Worktree | Clean |
| Targeted verification | 23 release-policy and publisher tests passed; PowerShell parsed; `git diff --check` passed |
| Full local gate | `LOCAL CI: PASS`; 1,151 tests passed; Release build had 0 warnings and 0 errors with the handoff content present |
| GitHub access | `gh` credentials invalid and GitHub DNS resolution failed on SND-HOST |
| Provider state | Rulesets, Actions permissions, secret presence, workflows, PRs, and releases unverified live |

The feature branch currently contains local review commit `9f514c8` before its
nine delivery commits. Because the remote could not be queried, do not assume
that commit is now on `origin/main` or that it belongs in the delivery PR.

## What the feature branch implements

The nine commits from `0e96bdf` through `0891ab1` provide:

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

No live GitHub Release was created at the checkpoint.

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

In the feature worktree, verify the lane and preserve unexpected state:

```powershell
git status --short --branch
git branch --show-current
git rev-parse HEAD
git merge-base --is-ancestor 0891ab1 HEAD
git log --oneline --decorate -12
```

Expected delivery implementation ancestor: `0891ab1`; the current head also
contains the later handoff commit. If that ancestry check fails, the worktree is
dirty, or another process is writing it, stop and reconcile ownership before
changing anything. Never reset, clean, stage, or discard unattributed changes.

### 2. Restore and verify GitHub access

Authentication repair is an attended operator action:

```powershell
Resolve-DnsName github.com
gh auth status
git ls-remote --heads origin main
```

If authentication remains invalid, use the GitHub CLI's attended login or
refresh flow. Do not paste a token into a command or document. Continue only
when DNS, `gh`, and the Git remote all work for the intended account and
repository.

### 3. Refresh the base and decide the review-commit boundary

Fetching changes local remote-tracking state but does not change the worktree:

```powershell
git fetch --prune origin
git rev-parse origin/main
git log --left-right --graph --cherry-pick --oneline origin/main...HEAD
git diff --stat origin/main...HEAD
```

Explicitly decide whether review commit `9f514c8` belongs in the PR:

- If current `origin/main` already contains it, no special exclusion is needed.
- If it is absent and belongs in the PR, record that decision before integration.
- If it is absent and must not be in the PR, preserve the original branch and
  transplant only `9f514c8..0891ab1` onto a new branch from current
  `origin/main`. Do not rewrite or force-push the checkpoint branch merely to
  simplify the graph.

One preservation-first exclusion pattern is:

```powershell
git branch snd-host/repo-less-desk-downloads-20260904-checkpoint 0891ab1
git switch --create snd-host/repo-less-desk-downloads-20260904-pr origin/main
git cherry-pick 9f514c8..0891ab1
```

If any cherry-pick conflicts, stop and resolve the product/document authority
conflict deliberately. Do not use an automatic ours/theirs resolution.

### 4. Run the complete local gate

From the selected PR branch:

```powershell
pwsh -NoProfile -File .\scripts\Test-LocalCI.ps1
git diff --check
git status --short --branch
```

The exit gate is `LOCAL CI: PASS`, no diff-check errors, and a clean worktree.
The earlier targeted 23-test result is not a substitute for this gate.

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
git fetch --prune origin
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
```

The packaged verifier checks provenance and launches the automated UI smoke
with disposable data and evidence outside the immutable package root. Then run
the attended product checks: pop out a playing video, listen for double audio
through launch and return/close, repeat with a playlist or mix when available,
and record unavailable ad/account/profile states as not run.

Acceptance is tied to the exact commit and test tag. A passed test prerelease
does not authorize Stable promotion by itself.

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

The repo-less delivery commits do not close the immediate release-path findings
in [`reviews/review-controller-2026-09-02-piplay-readiness.md`](reviews/review-controller-2026-09-02-piplay-readiness.md):

- F-1: deploy-root validation and unrelated-content protection;
- F-6: `-SkipTests` release-evidence handling and local-gate parity;
- F-7: UI-smoke binding to the deployed Stable copy; and
- F-8: deploy-swap and publish-lock harnesses in the required gate.

Refresh those findings against merged `main` and close or explicitly disposition
them before the next `Publish-Stable.ps1` run. F-1 is a destructive-scope hard
block. A successful repo-less test prerelease does not satisfy this separate
readiness gate.

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
   smoke exactly as documented in the README. A successful exact-source publish
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
- the feature lane is dirty, moved, or actively written by another process;
- actual remote `main` cannot be fetched and identified;
- the `9f514c8` inclusion decision is missing;
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
