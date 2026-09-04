using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;

namespace PiPlay.Tests;

/// <summary>
/// Static invariants for the PowerShell release scripts. The scripts are still validated by
/// running their focused commands, but these checks keep the fail-closed provenance policy from
/// being accidentally edited out.
/// </summary>
[Trait(TestCategories.Key, TestCategories.Markup)]
public class ReleaseScriptPolicyTests
{
    private static string RepoRoot
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "PiPlay.sln")))
                dir = dir.Parent;
            if (dir is null)
                throw new InvalidOperationException("Could not locate repo root from " + AppContext.BaseDirectory);
            return dir.FullName;
        }
    }

    private static string Script(string relativePath) =>
        File.ReadAllText(Path.Combine(RepoRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)));

    [Fact]
    public void Publish_stable_defaults_to_exact_source_and_requires_escape_hatches()
    {
        var script = Script("scripts/Publish-Stable.ps1");

        Assert.Contains("[string]$DeployRoot", script);
        Assert.Contains("PIPLAY_STABLE_ROOT", script);
        Assert.DoesNotContain(@"E:\Dev_test_implemenations\PiPlay", script);
        Assert.Contains("[switch]$AllowDirty", script);
        Assert.Contains("[switch]$AllowVersionBump", script);
        Assert.Contains("Release-verified stable deploys require a clean tree", script);
        Assert.Contains("only allowed with -AllowVersionBump", script);
        Assert.Contains("$buildParams[\"NoVersionBump\"] = $true", script);
        Assert.Contains("$buildParams[\"NoBuildNumberBump\"] = $true", script);
        Assert.Contains("Assert-StableTag", script);
        Assert.Contains("-AllowNonReleaseEvidence", script);
    }

    [Fact]
    public void Verify_stable_fail_closes_release_provenance()
    {
        var script = Script("scripts/Verify-StableDeploy.ps1");

        Assert.Contains("[string]$DeployRoot", script);
        Assert.Contains("PIPLAY_STABLE_ROOT", script);
        Assert.DoesNotContain(@"E:\Dev_test_implemenations\PiPlay", script);
        Assert.Contains("function Write-ProvenanceIssue", script);
        Assert.Contains("ProductVersion", script);
        Assert.Contains("Manifest marks this deploy as NOT release evidence", script);
        Assert.Contains("Expected stable tag", script);
        Assert.Contains("Repo working tree is DIRTY", script);
        Assert.Contains("VERDICT: RELEASE VERIFIED", script);
    }

    [Fact]
    public void Ui_smoke_is_isolated_dpi_aware_and_rejects_blank_captures()
    {
        var script = Script("scripts/Test-UiSmoke.ps1");

        Assert.Contains("PIPLAY_DATA_ROOT", script);
        Assert.Contains("SetThreadDpiAwarenessContext", script);
        Assert.Contains("SetForegroundWindow", script);
        Assert.Contains("GetForegroundWindow", script);
        Assert.Contains("GetWindowThreadProcessId", script);
        Assert.Contains("Rendered capture is blank or uniform", script);
        Assert.Contains("SMOKE PASS", script);
    }

    [Fact]
    public void Verify_stable_fails_closed_on_missing_source_commit()
    {
        var script = Script("scripts/Verify-StableDeploy.ps1");

        // A manifest with no sourceCommit cannot be tied to a commit, so default verification must
        // fail closed; -AllowNonReleaseEvidence still downgrades it via Write-ProvenanceIssue.
        Assert.Contains("$commit = [string](Get-ObjectPropertyValue -Object $buildInfo -Name \"sourceCommit\")", script);
        Assert.Contains("Write-ProvenanceIssue \"Manifest has no sourceCommit", script);
        Assert.DoesNotContain("$commit = [string]$buildInfo.sourceCommit", script);
        Assert.DoesNotContain("Write-Warn2 \"Manifest has no sourceCommit", script);
    }

    [Fact]
    public void Build_pipeline_signs_before_manifest_and_records_source_state()
    {
        var script = Script("scripts/Build-PiPlay.ps1");

        Assert.Contains("[string]$SignScript", script);
        Assert.Contains("sourceDirtyEntries", script);
        Assert.Contains("releaseEvidence", script);
        Assert.Contains("signing = if ($SigningEnabled)", script);

        var signStep = script.IndexOf("[4a] Signing publish output before metadata", StringComparison.Ordinal);
        var manifestStep = script.IndexOf("$buildInfoPath = Write-BuildInfo", StringComparison.Ordinal);
        Assert.True(signStep >= 0, "Build script should run the signing step after publish.");
        Assert.True(manifestStep >= 0, "Build script should write build-info.json.");
        Assert.True(signStep < manifestStep, "Signing must happen before manifest hashes are written.");
    }

    [Fact]
    public void Diagnostic_publishes_are_recorded_as_non_release_evidence()
    {
        var build = Script("scripts/Build-PiPlay.ps1");
        var publish = Script("scripts/Publish-Stable.ps1");

        // Build can be forced non-release independently of source-tree dirtiness, so a clean no-op
        // diagnostic run cannot mint release evidence.
        Assert.Contains("[string]$NonReleaseReason", build);
        Assert.Contains("$explicitNonRelease = -not [string]::IsNullOrWhiteSpace($NonReleaseReason)", build);
        Assert.Contains("$releaseEvidence = (-not $sourceDirty) -and (-not $explicitNonRelease)", build);

        // Publish hands the reason to the build whenever an escape hatch is used, and refuses to
        // surface a diagnostic deploy as release evidence even if that plumbing regresses.
        Assert.Contains("$buildParams[\"NonReleaseReason\"]", publish);
        Assert.Contains("refusing to present a diagnostic deploy as release evidence", publish);
    }

    [Fact]
    public void Publish_creates_stable_tag_only_after_pretag_verification()
    {
        var publish = Script("scripts/Publish-Stable.ps1");
        var verify = Script("scripts/Verify-StableDeploy.ps1");

        // The verifier has a narrow pre-tag mode that downgrades ONLY the missing expected tag.
        Assert.Contains("[switch]$AllowMissingStableTag", verify);
        Assert.Contains("not yet created (pre-tag verification)", verify);

        // Order: pre-tag verification -> create tag -> final (tag-required) verification.
        var preTagVerify = publish.IndexOf("-AllowMissingStableTag", StringComparison.Ordinal);
        var createTag = publish.IndexOf("Assert-StableTag -TagName", StringComparison.Ordinal);
        var finalVerify = publish.LastIndexOf("& $verifyScript -DeployRoot $DeployRoot", StringComparison.Ordinal);

        Assert.True(preTagVerify >= 0, "Publish should run a pre-tag verification with -AllowMissingStableTag.");
        Assert.True(createTag >= 0, "Publish should create the stable tag via Assert-StableTag.");
        Assert.True(preTagVerify < createTag, "Pre-tag verification must run before the stable tag is created.");
        Assert.True(createTag < finalVerify, "A full verification must run after the tag is created.");
    }

    [Fact]
    public void Publish_preflights_the_stable_tag_before_anything_destructive()
    {
        var publish = Script("scripts/Publish-Stable.ps1");

        // A colliding tag used to surface only at the very end - after the test lane, the build, and
        // a destructive deploy had already replaced the manual-test copy. The check must come first.
        Assert.Contains("Tag preflight (before tests, build, or deploy)", publish);
        Assert.Contains("already exists at $existingTagCommit, but HEAD is $headCommit", publish);

        var preflight = publish.IndexOf("Tag preflight (before tests, build, or deploy)", StringComparison.Ordinal);
        var testGate = publish.IndexOf("Running deterministic test lane (gate)", StringComparison.Ordinal);
        var build = publish.IndexOf("Building + publishing the Stable channel Release", StringComparison.Ordinal);
        var deploy = publish.IndexOf("Invoke-StagedDeploy", StringComparison.Ordinal);

        Assert.True(preflight >= 0, "Publish should preflight the stable tag.");
        Assert.True(preflight < testGate, "Tag preflight must run before the test lane.");
        Assert.True(preflight < build, "Tag preflight must run before the build.");
        Assert.True(preflight < deploy, "Tag preflight must run before the deploy.");
    }

    [Fact]
    public void Publish_serializes_concurrent_runs()
    {
        var publish = Script("scripts/Publish-Stable.ps1");
        var lockScript = Script("scripts/PublishLock.ps1");

        // Two publishes at once would interleave on bin\publish, on the deploy root mid-swap, and on
        // tag creation. Both the repo and the deploy root are locked, before any expensive step.
        Assert.Contains("function New-PublishLock", lockScript);
        Assert.Contains("Another PiPlay publish is already running against", lockScript);
        Assert.Contains("System.Threading.AbandonedMutexException", lockScript);   // a crashed publish leaves no stale lock

        Assert.Contains("New-PublishLock -Key \"repo|$repoRoot\"", publish);
        Assert.Contains("New-PublishLock -Key \"deploy|$DeployRoot\"", publish);

        var lockTaken = publish.IndexOf("New-PublishLock -Key \"repo|$repoRoot\"", StringComparison.Ordinal);
        var build = publish.IndexOf("Building + publishing the Stable channel Release", StringComparison.Ordinal);
        Assert.True(lockTaken >= 0 && lockTaken < build, "The publish lock must be taken before the build.");
    }

    [Fact]
    public void Publish_releases_its_locks_on_every_exit_path()
    {
        var publish = Script("scripts/Publish-Stable.ps1");
        var lockScript = Script("scripts/PublishLock.ps1");

        // A mutex belongs to the THREAD that took it, and PowerShell's console host reuses its pipeline
        // thread. A publish that ended without releasing left the mutex owned by the still-alive prompt
        // thread, so the NEXT publish (another process) was told "another publish is already running"
        // with nothing running - clearing only if the GC happened to finalize the handle. The release
        // must therefore be in a finally, which PowerShell honours on success, throw, AND exit.
        // (scripts\Test-PublishLock.ps1 case 3 is the behavioural guard.)
        Assert.Contains("function Close-PublishLocks", lockScript);
        Assert.Contains("ReleaseMutex()", lockScript);

        var lockTaken = publish.IndexOf("New-PublishLock -Key \"repo|$repoRoot\"", StringComparison.Ordinal);
        var tryOpen = publish.IndexOf("\ntry {", lockTaken, StringComparison.Ordinal);
        var release = publish.IndexOf("Close-PublishLocks", tryOpen < 0 ? lockTaken : tryOpen, StringComparison.Ordinal);
        var finallyBlock = publish.IndexOf("\nfinally {", lockTaken, StringComparison.Ordinal);

        Assert.True(tryOpen > lockTaken, "The publish body must be wrapped in a try after the lock is taken.");
        Assert.True(finallyBlock > tryOpen, "That try must have a finally.");
        Assert.True(release > finallyBlock, "Close-PublishLocks must run from the finally, not the success path.");
    }

    [Fact]
    public void Deploy_stages_and_verifies_before_replacing_the_live_copy()
    {
        var publish = Script("scripts/Publish-Stable.ps1");
        var swap = Script("scripts/DeploySwap.ps1");

        // The deploy must never again delete the live payload and then copy over the top of it: an
        // interrupted copy left the only sanctioned manual-test installation broken with no way back.
        Assert.Contains("Invoke-StagedDeploy", publish);
        Assert.Contains("Repair-InterruptedDeploy", publish);
        Assert.DoesNotContain("Copy-Item -Path (Join-Path $latestDir \"*\") -Destination $DeployRoot", publish);

        // Stage -> verify the staged bytes -> only then swap.
        var stage = swap.IndexOf("Copy-Item -Path (Join-Path $SourceDir \"*\")", StringComparison.Ordinal);
        var verify = swap.IndexOf("Test-StagedPayload -StagingDir $paths.Staging", StringComparison.Ordinal);
        var swapIn = swap.IndexOf("Move-Item -LiteralPath $item.FullName -Destination (Join-Path $paths.Backup", StringComparison.Ordinal);

        Assert.True(stage >= 0 && verify >= 0 && swapIn >= 0, "DeploySwap should stage, verify, then swap.");
        Assert.True(stage < verify, "The staged payload must be copied before it is verified.");
        Assert.True(verify < swapIn, "The staged payload must verify BEFORE the live copy is touched.");
    }

    [Fact]
    public void Deploy_rolls_back_from_what_the_backup_actually_holds()
    {
        var swap = Script("scripts/DeploySwap.ps1");

        // Move-Item half-moves a directory whose child is locked and still throws, so a rollback keyed
        // on "the moves I recorded as successful" drops those children and then deletes the backup
        // holding the only copy. Rollback must walk the backup itself and merge. (scripts\Test-DeploySwap.ps1 case C3.)
        Assert.Contains("function Restore-DeployBackup", swap);
        Assert.Contains("Restore-DeployBackup -BackupDir $BackupDir -DeployRoot $DeployRoot", swap);
        Assert.Contains("rollback could not restore a runnable copy", swap);

        // A rollback that could not put everything back must KEEP the backup and say where it is.
        // Deleting it on the strength of "the restore probably worked" is the same data loss in the
        // other direction (scripts\Test-DeploySwap.ps1 case H).
        Assert.Contains("could not fully restore the previous copy", swap);
        Assert.Contains("is PRESERVED at", swap);

        // The runtime data folder is never moved aside (ADR-0007: login/session survive a redeploy).
        // Anchor the guard inside the SWAP loop specifically: the same line also appears in
        // Repair-InterruptedDeploy, so a bare Contains would stay green with the real guard deleted.
        var guard = "if ($item.Name -ieq $DataFolderName) { continue }";
        var swapFn = swap.IndexOf("function Invoke-StagedDeploy", StringComparison.Ordinal);
        Assert.True(swapFn >= 0, "Invoke-StagedDeploy should exist.");
        Assert.True(swap.IndexOf(guard, swapFn, StringComparison.Ordinal) >= 0,
            "The swap loop must skip the runtime data folder (ADR-0007).");
    }

    [Fact]
    public void Native_command_helper_owns_benign_stderr_policy()
    {
        var helper = Script("scripts/NativeCommand.ps1");

        Assert.Contains("function Invoke-NativeCommandQuiet", helper);
        Assert.Contains("$previousErrorActionPreference = $ErrorActionPreference", helper);
        Assert.Contains("$ErrorActionPreference = \"Continue\"", helper);
        Assert.Contains("$ErrorActionPreference = $previousErrorActionPreference", helper);
    }

    [Fact]
    public void Local_ci_wrapper_fails_closed_and_restores_process_state()
    {
        var script = Script("scripts/Test-LocalCI.ps1");

        Assert.Contains("$global:LASTEXITCODE = 0", script);
        Assert.Contains("$exitCode = $LASTEXITCODE", script);
        Assert.Contains("throw \"Local CI step '", script);
        Assert.Contains("Restore-ProcessEnvironment -Previous $testEnvironment", script);
        Assert.Contains("Restore-ProcessEnvironment -Previous $commonEnvironment", script);
        Assert.Contains("Remove-Item -LiteralPath $testDataRoot -Recurse -Force", script);
        Assert.Contains("Pop-Location", script);
        Assert.Contains("Restore-ProcessEnvironment -Previous $previous", script);

        // Node enforcement must read the value the plan DECLARES and the plan test pins
        // (requirements.nodeMinimumMajor), not a second hardcoded literal that can silently drift green.
        Assert.Contains("[int]$MinimumMajor", script);
        Assert.Contains("-MinimumMajor $localCiPlan.requirements.nodeMinimumMajor", script);
        Assert.Contains("[regex]::Match($nodeVersion, '^v?(?<major>\\d+)\\.')", script);
        Assert.Contains("$nodeMajor -lt $MinimumMajor", script);
        Assert.DoesNotContain("-notmatch '^v?24\\.'", script);
        Assert.DoesNotContain("-notmatch \"^v?$MinimumMajor", script);

        var testBranch = script.IndexOf("elseif ($step.name -eq \"test\")", StringComparison.Ordinal);
        var protectedTry = script.IndexOf("try {", testBranch, StringComparison.Ordinal);
        var createRoot = script.IndexOf("New-Item -ItemType Directory -Path $testDataRoot", testBranch, StringComparison.Ordinal);
        var setTestEnvironment = script.IndexOf("$testEnvironment = Set-ProcessEnvironment", testBranch, StringComparison.Ordinal);
        var cleanupFinally = script.IndexOf("finally {", testBranch, StringComparison.Ordinal);
        Assert.True(testBranch >= 0 && protectedTry > testBranch, "The test-step branch must open a try.");
        Assert.True(createRoot > protectedTry, "Test-root creation must be protected by the cleanup finally.");
        Assert.True(setTestEnvironment > protectedTry, "Test environment setup must be protected by the cleanup finally.");
        Assert.True(cleanupFinally > setTestEnvironment, "Cleanup must run from the test-step finally.");

        // Cleanup is best-effort: a transient lock on the temp root must degrade to a warning, never
        // throw out of the finally - which would either mask a real test failure or flip a fully green
        // run to FAILED. The -ErrorAction Stop remove is wrapped so its failure only warns.
        var cleanupRemove = script.IndexOf("Remove-Item -LiteralPath $testDataRoot", cleanupFinally, StringComparison.Ordinal);
        var cleanupCatch = script.IndexOf("} catch {", cleanupRemove, StringComparison.Ordinal);
        var cleanupWarn = script.IndexOf("Could not remove local CI test data", cleanupRemove, StringComparison.Ordinal);
        Assert.True(cleanupRemove > cleanupFinally, "The cleanup remove must live in the test-step finally.");
        Assert.True(cleanupCatch > cleanupRemove, "The cleanup remove must be wrapped in a try/catch.");
        Assert.True(cleanupWarn > cleanupCatch, "A cleanup failure must degrade to a warning, not throw.");
    }

    [Fact]
    public void Ci_keeps_pull_requests_hosted_and_trusted_events_variable_routed()
    {
        var workflow = Script(".github/workflows/ci.yml");
        var normalized = workflow.Replace("\r\n", "\n");

        Assert.Contains("name: Build and test (Windows)", workflow);
        Assert.Contains("case(github.event_name == 'pull_request', 'windows-latest'", workflow);
        Assert.Contains("vars.PIPLAY_WINDOWS_RUNNER || 'windows-latest'", workflow);
        Assert.Contains("push:\n    branches:\n      - main\n  workflow_dispatch:", normalized);
        Assert.DoesNotContain("\n    tags:", normalized);
        Assert.Contains("run: .\\scripts\\Test-LocalCI.ps1", workflow);
        Assert.Contains("persist-credentials: false", workflow);
        Assert.DoesNotContain("run: dotnet restore", workflow);
        Assert.DoesNotContain("run: dotnet test", workflow);

        var usesLines = normalized.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .Where(line => line.StartsWith("uses: ", StringComparison.Ordinal))
            .ToArray();
        Assert.Equal(4, usesLines.Length);
        Assert.All(usesLines, line => Assert.Matches(
            new Regex(@"^uses: [^@\s]+@[0-9a-f]{40}(?:\s+#\s+.+)?$", RegexOptions.CultureInvariant),
            line));
    }

    [Fact]
    public void Publish_payload_includes_standalone_download_validation()
    {
        var build = Script("scripts/Build-PiPlay.ps1");
        var verifier = Script("scripts/Test-DownloadedPackage.ps1");

        Assert.Contains("scripts\\Test-DownloadedPackage.ps1", build);
        Assert.Contains("scripts\\Test-UiSmoke.ps1", build);
        Assert.Contains("[ValidateSet('Test', 'Release')]", verifier);
        Assert.Contains("[switch]$ValidateOnly", verifier);
        Assert.Contains("[string]$ExpectedCommit", verifier);
        Assert.Contains("[string]$ExpectedTag", verifier);
        Assert.Contains("Resolve-ManifestArtifactPath", verifier);
        Assert.Contains("Get-Sha256Hex", verifier);
        Assert.Contains("AssemblyMetadataAttribute", verifier);
        Assert.Contains("PiPlay.Channel", verifier);
        Assert.Contains("sourceDirty must be false", verifier);
        Assert.Contains("releaseEvidence must be false", verifier);
        Assert.Contains("releaseEvidence must be true", verifier);
        Assert.Contains("source commit, version stamps, and artifact hashes were captured from a clean tree", verifier);
        Assert.Contains("PACKAGE VERIFIED", verifier);
        Assert.Contains("PIPLAY_DATA_ROOT", verifier);
        Assert.Contains("Assert-NoReparsePointComponents -Path $resolvedExternal", verifier);
        Assert.Contains("Test-UiSmoke.ps1", verifier);
    }

    [Fact]
    public void Manual_ci_dispatch_builds_validates_and_uploads_only_a_test_package()
    {
        var workflow = Script(".github/workflows/ci.yml").Replace("\r\n", "\n");

        Assert.Contains("if: github.event_name == 'workflow_dispatch'", workflow);
        Assert.Contains("-Stage Publish", workflow);
        Assert.Contains("-Channel Stable", workflow);
        Assert.Contains("-NoVersionBump", workflow);
        Assert.Contains("-NoBuildNumberBump", workflow);
        Assert.Contains("-NonReleaseReason $nonReleaseReason", workflow);
        Assert.Contains("Test-DownloadedPackage.ps1", workflow);
        Assert.Contains("-Kind Test -ExpectedCommit $env:GITHUB_SHA -ValidateOnly", workflow);
        Assert.Contains("actions/upload-artifact@043fb46d1a93c77aae656e7c1c64a875d1fc6a0a", workflow);
        Assert.Contains("PiPlay-test-${{ github.sha }}", workflow);
        Assert.DoesNotContain("gh release create", workflow);
    }

    [Fact]
    public void Stable_tag_workflow_creates_a_permanent_verified_release_package()
    {
        var workflow = Script(".github/workflows/release.yml").Replace("\r\n", "\n");
        var tagPolicy = Script(".github/scripts/Test-StableTagPolicy.ps1");

        Assert.Contains("tags:\n      - 'stable-v*'", workflow);
        Assert.Contains("contents: write", workflow);
        Assert.Contains("fetch-depth: 0", workflow);
        Assert.Contains("persist-credentials: false", workflow);
        Assert.Contains("Require immutable Stable tags", workflow);
        Assert.Equal(2, Regex.Matches(workflow, @"Test-StableTagPolicy\.ps1").Count);
        Assert.Contains("repos/$Repository/rulesets?includes_parents=true", tagPolicy);
        Assert.Contains("$detail.target -cne 'tag'", tagPolicy);
        Assert.Contains("$detail.enforcement -cne 'active'", tagPolicy);
        Assert.Contains("refs/tags/stable-v*", tagPolicy);
        Assert.Contains("$excludePatterns.Count -ne 0", tagPolicy);
        Assert.Contains("bypass_actors", tagPolicy);
        Assert.Contains("'deletion'", tagPolicy);
        Assert.Contains("'update'", tagPolicy);
        Assert.Contains("secrets.PIPLAY_RELEASE_POLICY_TOKEN", workflow);
        Assert.Contains("stable-v(?<version>", workflow);
        Assert.Contains("-Stage Release", workflow);
        Assert.Contains("-Channel Stable", workflow);
        Assert.Contains("-NoVersionBump", workflow);
        Assert.Contains("-NoBuildNumberBump", workflow);
        Assert.Contains("Test-DownloadedPackage.ps1", workflow);
        Assert.Contains("-Kind Release -ExpectedTag $tag -ExpectedCommit $env:GITHUB_SHA -ValidateOnly", workflow);
        Assert.Contains("Expand-Archive -LiteralPath $archive", workflow);
        Assert.Contains("PiPlay-release-extracted-verification-$env:GITHUB_RUN_ID-$env:GITHUB_RUN_ATTEMPT", workflow);
        Assert.Contains("& .\\scripts\\Test-DownloadedPackage.ps1 -Kind Release -Root $extractRoot", workflow);
        Assert.Contains("git ls-remote --tags origin", workflow);
        Assert.Contains("releaseEvidence", workflow);
        Assert.Contains("sourceCommit", workflow);
        Assert.Contains("gh release create", workflow);
        Assert.Contains("--verify-tag", workflow);

        var usesLines = workflow.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .Where(line => line.StartsWith("uses: ", StringComparison.Ordinal))
            .ToArray();
        Assert.All(usesLines, line => Assert.Matches(
            new Regex(@"^uses: [^@\s]+@[0-9a-f]{40}(?:\s+#\s+.+)?$", RegexOptions.CultureInvariant),
            line));
    }

    [Fact]
    public async Task Stable_tag_policy_rejects_exclusions_bypasses_and_hidden_bypass_state()
    {
        const string valid = """
            {"id":42,"name":"Stable tags","target":"tag","enforcement":"active","bypass_actors":[],"conditions":{"ref_name":{"include":["refs/tags/stable-v*"],"exclude":[]}},"rules":[{"type":"update"},{"type":"deletion"}]}
            """;
        const string broadExclusion = """
            {"id":42,"name":"Stable tags","target":"tag","enforcement":"active","bypass_actors":[],"conditions":{"ref_name":{"include":["refs/tags/stable-v*"],"exclude":["refs/tags/*"]}},"rules":[{"type":"update"},{"type":"deletion"}]}
            """;
        const string alwaysBypass = """
            {"id":42,"name":"Stable tags","target":"tag","enforcement":"active","bypass_actors":[{"actor_type":"RepositoryRole","actor_id":5,"bypass_mode":"always"}],"conditions":{"ref_name":{"include":["refs/tags/stable-v*"],"exclude":[]}},"rules":[{"type":"update"},{"type":"deletion"}]}
            """;
        const string hiddenBypassState = """
            {"id":42,"name":"Stable tags","target":"tag","enforcement":"active","conditions":{"ref_name":{"include":["refs/tags/stable-v*"],"exclude":[]}},"rules":[{"type":"update"},{"type":"deletion"}]}
            """;

        var validResult = await RunStableTagPolicyAsync(valid);
        Assert.True(validResult.ExitCode == 0,
            $"Valid policy failed.{Environment.NewLine}{validResult.Error}{Environment.NewLine}{validResult.Output}");
        Assert.NotEqual(0, (await RunStableTagPolicyAsync(broadExclusion)).ExitCode);
        Assert.NotEqual(0, (await RunStableTagPolicyAsync(alwaysBypass)).ExitCode);
        Assert.NotEqual(0, (await RunStableTagPolicyAsync(hiddenBypassState)).ExitCode);
    }

    private static async Task<(int ExitCode, string Output, string Error)> RunStableTagPolicyAsync(string detailJson)
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "PiPlayTagPolicyTest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        try
        {
            var detailPath = Path.Combine(tempRoot, "detail.json");
            var fakeGh = Path.Combine(tempRoot, "gh.cmd");
            await File.WriteAllTextAsync(detailPath, detailJson);
            await File.WriteAllTextAsync(fakeGh, """
                @echo off
                echo %* | %SystemRoot%\System32\findstr.exe /C:"includes_parents" >nul
                if not errorlevel 1 (
                  echo [{"id":42}]
                  exit /b 0
                )
                type "%PIPLAY_TEST_RULESET_DETAIL%"
                exit /b 0
                """);

            var startInfo = new ProcessStartInfo
            {
                FileName = "pwsh",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            startInfo.Environment["PATH"] = tempRoot + Path.PathSeparator +
                (Environment.GetEnvironmentVariable("PATH") ?? string.Empty);
            startInfo.Environment["PIPLAY_TEST_RULESET_DETAIL"] = detailPath;
            startInfo.Environment["GH_TOKEN"] = "test-token";
            startInfo.ArgumentList.Add("-NoProfile");
            startInfo.ArgumentList.Add("-File");
            startInfo.ArgumentList.Add(Path.Combine(RepoRoot, ".github", "scripts", "Test-StableTagPolicy.ps1"));
            startInfo.ArgumentList.Add("-Repository");
            startInfo.ArgumentList.Add("espensev/PiPlay");

            using var process = new Process { StartInfo = startInfo };
            Assert.True(process.Start(), "Stable tag policy test process did not start.");
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            var exitTask = process.WaitForExitAsync();
            var completed = await Task.WhenAny(exitTask, Task.Delay(TimeSpan.FromSeconds(15)));
            if (completed != exitTask)
            {
                try { process.Kill(entireProcessTree: true); } catch { /* best-effort timeout cleanup */ }
                Assert.Fail("Stable tag policy test exceeded 15 seconds.");
            }
            await exitTask;
            return (process.ExitCode, await output, await error);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Theory]
    [InlineData("scripts/Build-PiPlay.ps1")]
    [InlineData("scripts/Publish-Stable.ps1")]
    [InlineData("scripts/Verify-StableDeploy.ps1")]
    public void Git_helpers_use_shared_native_command_wrapper(string relativePath)
    {
        var script = Script(relativePath);

        Assert.Contains(". (Join-Path $PSScriptRoot \"NativeCommand.ps1\")", script);
        Assert.Contains("Invoke-NativeCommandQuiet", script);
        Assert.DoesNotContain("$previousErrorActionPreference = $ErrorActionPreference", script);
    }
}
