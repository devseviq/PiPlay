using System.Diagnostics;
using System.IO;

namespace PiPlay.Tests;

[Trait(TestCategories.Key, TestCategories.Markup)]
public class TestPrereleasePublisherTests
{
    [Fact]
    public async Task Publisher_creates_exact_tag_before_draft_and_fails_closed_on_collision_or_wrong_ref()
    {
        var scriptPath = Path.Combine(RepoRoot, ".github", "scripts", "Publish-TestPrerelease.ps1");
        Assert.True(File.Exists(scriptPath), "The test-prerelease publisher script is missing.");

        var valid = await RunPublisherAsync(scriptPath, "valid");
        Assert.True(valid.ExitCode == 0, valid.Error + Environment.NewLine + valid.Output);
        Assert.True(valid.Calls.SequenceEqual(new[] { "release-create", "release-edit" }),
            "Unexpected publication calls: " + string.Join(" | ", valid.AllCalls));

        var collision = await RunPublisherAsync(scriptPath, "collision");
        Assert.NotEqual(0, collision.ExitCode);
        Assert.Empty(collision.Calls);

        var wrongRef = await RunPublisherAsync(scriptPath, "wrong-ref");
        Assert.NotEqual(0, wrongRef.ExitCode);
        Assert.Empty(wrongRef.Calls);
    }

    private static string RepoRoot
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "PiPlay.sln")))
                dir = dir.Parent;
            return dir?.FullName ?? throw new InvalidOperationException("Could not locate repository root.");
        }
    }

    private static async Task<(int ExitCode, string Output, string Error, string[] Calls, string[] AllCalls)> RunPublisherAsync(
        string scriptPath,
        string scenario)
    {
        const string commit = "1234567890abcdef1234567890abcdef12345678";
        var tempRoot = Path.Combine(Path.GetTempPath(), "PiPlayPrereleasePublisherTest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        try
        {
            var fakeGh = Path.Combine(tempRoot, "gh.cmd");
            var archive = Path.Combine(tempRoot, "PiPlay-test.zip");
            var checksum = archive + ".sha256";
            var callLog = Path.Combine(tempRoot, "calls.txt");
            var allCallLog = Path.Combine(tempRoot, "all-calls.txt");
            await File.WriteAllTextAsync(archive, "zip fixture");
            await File.WriteAllTextAsync(checksum, "checksum fixture");
            await File.WriteAllTextAsync(fakeGh, """
                @echo off
                if "%~1 %~2"=="release create" (
                  echo release-create>>"%PIPLAY_FAKE_CALL_LOG%"
                  exit /b 0
                )
                if "%~1 %~2"=="release edit" (
                  echo release-edit>>"%PIPLAY_FAKE_CALL_LOG%"
                  exit /b 0
                )
                if "%~1 %~2"=="release view" (
                  echo {"isDraft":false,"isPrerelease":true,"tagName":"test-%PIPLAY_EXPECTED_SHA%-r7-a1"}
                  exit /b 0
                )
                echo %*>>"%PIPLAY_FAKE_ALL_CALL_LOG%"
                echo %* | %SystemRoot%\System32\findstr.exe /C:"rulesets?includes_parents" >nul
                if not errorlevel 1 (
                  echo [{"id":42}]
                  exit /b 0
                )
                echo %* | %SystemRoot%\System32\findstr.exe /C:"rulesets/42" >nul
                if not errorlevel 1 (
                  echo {"id":42,"name":"Test tags","target":"tag","enforcement":"active","bypass_actors":[],"conditions":{"ref_name":{"include":["refs/tags/test-*"],"exclude":[]}},"rules":[{"type":"update"},{"type":"deletion"}]}
                  exit /b 0
                )
                echo %* | %SystemRoot%\System32\findstr.exe /C:"--method POST" /C:"/git/refs" >nul
                if not errorlevel 1 (
                  if "%PIPLAY_FAKE_SCENARIO%"=="collision" exit /b 1
                  if "%PIPLAY_FAKE_SCENARIO%"=="wrong-ref" (
                    echo {"ref":"refs/tags/%PIPLAY_EXPECTED_TAG%","object":{"sha":"0000000000000000000000000000000000000000"}}
                  ) else (
                    echo {"ref":"refs/tags/%PIPLAY_EXPECTED_TAG%","object":{"sha":"%PIPLAY_EXPECTED_SHA%"}}
                  )
                  exit /b 0
                )
                echo %* | %SystemRoot%\System32\findstr.exe /C:"/git/ref/tags/" >nul
                if not errorlevel 1 (
                  echo {"ref":"refs/tags/%PIPLAY_EXPECTED_TAG%","object":{"sha":"%PIPLAY_EXPECTED_SHA%"}}
                  exit /b 0
                )
                exit /b 1
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
            startInfo.Environment["GH_TOKEN"] = "policy-token";
            startInfo.Environment["PIPLAY_RELEASE_TOKEN"] = "release-token";
            startInfo.Environment["PIPLAY_FAKE_SCENARIO"] = scenario;
            startInfo.Environment["PIPLAY_EXPECTED_SHA"] = commit;
            startInfo.Environment["PIPLAY_EXPECTED_TAG"] = $"test-{commit}-r7-a1";
            startInfo.Environment["PIPLAY_FAKE_CALL_LOG"] = callLog;
            startInfo.Environment["PIPLAY_FAKE_ALL_CALL_LOG"] = allCallLog;
            startInfo.ArgumentList.Add("-NoProfile");
            startInfo.ArgumentList.Add("-File");
            startInfo.ArgumentList.Add(scriptPath);
            startInfo.ArgumentList.Add("-Tag");
            startInfo.ArgumentList.Add($"test-{commit}-r7-a1");
            startInfo.ArgumentList.Add("-Commit");
            startInfo.ArgumentList.Add(commit);
            startInfo.ArgumentList.Add("-Archive");
            startInfo.ArgumentList.Add(archive);
            startInfo.ArgumentList.Add("-Checksum");
            startInfo.ArgumentList.Add(checksum);
            startInfo.ArgumentList.Add("-Repository");
            startInfo.ArgumentList.Add("espensev/PiPlay");
            startInfo.ArgumentList.Add("-Title");
            startInfo.ArgumentList.Add("PiPlay test 1234567890ab");

            using var process = new Process { StartInfo = startInfo };
            Assert.True(process.Start(), "Publisher test process did not start.");
            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask = process.StandardError.ReadToEndAsync();
            var exitTask = process.WaitForExitAsync();
            var completed = await Task.WhenAny(exitTask, Task.Delay(TimeSpan.FromSeconds(15)));
            if (completed != exitTask)
            {
                try { process.Kill(entireProcessTree: true); } catch { /* best-effort timeout cleanup */ }
                Assert.Fail("Publisher test exceeded 15 seconds.");
            }
            await exitTask;
            var calls = File.Exists(callLog) ? await File.ReadAllLinesAsync(callLog) : [];
            var allCalls = File.Exists(allCallLog) ? await File.ReadAllLinesAsync(allCallLog) : [];
            return (process.ExitCode, await outputTask, await errorTask, calls, allCalls);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }
}
