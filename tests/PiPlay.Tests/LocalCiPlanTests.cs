using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace PiPlay.Tests;

[Trait(TestCategories.Key, TestCategories.Logic)]
public class LocalCiPlanTests
{
    [Fact]
    public async Task Plan_is_side_effect_free_and_pins_the_shared_command_contract()
    {
        var first = await RunPlanAsync();
        var second = await RunPlanAsync();

        Assert.Equal(3, first.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(24, first.GetProperty("requirements").GetProperty("nodeMinimumMajor").GetInt32());
        Assert.Equal("global.json",
            first.GetProperty("requirements").GetProperty("dotnetGlobalJson").GetString());
        Assert.Equal("pwsh",
            first.GetProperty("requirements").GetProperty("powerShell").GetString());
        Assert.True(first.GetProperty("cleanupTestDataRoot").GetBoolean());

        var firstRoot = first.GetProperty("testDataRoot").GetString();
        var secondRoot = second.GetProperty("testDataRoot").GetString();
        Assert.False(string.IsNullOrWhiteSpace(firstRoot));
        Assert.False(string.IsNullOrWhiteSpace(secondRoot));
        Assert.NotEqual(firstRoot, secondRoot);
        Assert.False(Directory.Exists(firstRoot));
        Assert.False(Directory.Exists(secondRoot));

        var steps = first.GetProperty("steps").EnumerateArray().ToArray();
        Assert.Equal(new[] { "node-version", "dotnet-info", "restore", "test", "build",
            "deploy-swap", "publish-lock" },
            steps.Select(step => step.GetProperty("name").GetString()).ToArray());

        AssertStep(steps[0], "node", "--version");
        AssertStep(steps[1], "dotnet", "--info");
        AssertStep(steps[2], "dotnet", "restore", "PiPlay.sln", "-p:BuildInParallel=false");
        AssertStep(steps[3], "dotnet", "test", "PiPlay.sln", "--configuration", "Debug", "--no-restore");
        Assert.Equal(firstRoot,
            steps[3].GetProperty("environment").GetProperty("PIPLAY_DATA_ROOT").GetString());

        Assert.Equal("pwsh", steps[4].GetProperty("filePath").GetString());
        var buildArguments = Arguments(steps[4]);
        Assert.Equal("-NoProfile", buildArguments[0]);
        Assert.Equal("-File", buildArguments[1]);
        Assert.Equal("Build-PiPlay.ps1", Path.GetFileName(buildArguments[2]));
        Assert.Equal(new[] { "-Stage", "Build", "-NoVersionBump", "-NoBuildNumberBump" },
            buildArguments[3..]);

        // The two PowerShell harnesses for the destructive release scripts must run inside this
        // same gate — CI runs only Test-LocalCI.ps1, and a text-shape policy test cannot tell
        // whether a rollback actually restores or a lock actually lets go (readiness review F-8).
        AssertHarnessStep(steps[5], "Test-DeploySwap.ps1");
        AssertHarnessStep(steps[6], "Test-PublishLock.ps1");
    }

    private static void AssertHarnessStep(JsonElement step, string scriptFileName)
    {
        Assert.Equal("pwsh", step.GetProperty("filePath").GetString());
        var arguments = Arguments(step);
        Assert.Equal("-NoProfile", arguments[0]);
        Assert.Equal("-File", arguments[1]);
        Assert.Equal(scriptFileName, Path.GetFileName(arguments[2]));
    }

    [Theory]
    [InlineData("v23.11.0", false)]
    [InlineData("v24.0.0", true)]
    [InlineData("26.7.0", true)]
    [InlineData("not-a-version", false)]
    public async Task Node_version_gate_enforces_the_minimum_declared_by_the_plan(
        string reportedVersion, bool expectedSuccess)
    {
        var plan = await RunPlanAsync();
        var minimumMajor = plan.GetProperty("requirements").GetProperty("nodeMinimumMajor").GetInt32();
        var result = await RunNodeVersionFunctionAsync(reportedVersion, minimumMajor);

        Assert.Equal(expectedSuccess, result.ExitCode == 0);
        if (!expectedSuccess)
            Assert.Contains("Node", result.Error + result.Output, StringComparison.OrdinalIgnoreCase);
    }

    private static void AssertStep(JsonElement step, string filePath, params string[] arguments)
    {
        Assert.Equal(filePath, step.GetProperty("filePath").GetString());
        Assert.Equal(arguments, Arguments(step));
    }

    private static string[] Arguments(JsonElement step) =>
        step.GetProperty("arguments").EnumerateArray()
            .Select(argument => argument.GetString() ?? string.Empty)
            .ToArray();

    private static async Task<JsonElement> RunPlanAsync()
    {
        var repoRoot = FindRepoRoot();
        var scriptPath = Path.Combine(repoRoot, "scripts", "Test-LocalCI.ps1");
        Assert.True(File.Exists(scriptPath), $"Local CI script was not found at {scriptPath}.");

        var startInfo = new ProcessStartInfo
        {
            FileName = "pwsh",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-File");
        startInfo.ArgumentList.Add(scriptPath);
        startInfo.ArgumentList.Add("-Plan");
        startInfo.ArgumentList.Add("-AsJson");

        using var process = new Process { StartInfo = startInfo };
        Assert.True(process.Start(), "Local CI plan process did not start.");
        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();

        var exitTask = process.WaitForExitAsync();
        var completed = await Task.WhenAny(exitTask, Task.Delay(TimeSpan.FromSeconds(15)));
        if (completed != exitTask)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* best-effort timeout cleanup */ }
            Assert.Fail("Local CI plan exceeded the 15-second deterministic-test budget.");
        }

        await exitTask;
        var output = await standardOutput;
        var error = await standardError;
        Assert.True(process.ExitCode == 0,
            $"Local CI plan exited with code {process.ExitCode}.{Environment.NewLine}{error}{Environment.NewLine}{output}");

        using var document = JsonDocument.Parse(output);
        return document.RootElement.Clone();
    }

    private static async Task<(int ExitCode, string Output, string Error)> RunNodeVersionFunctionAsync(
        string reportedVersion, int minimumMajor)
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "PiPlayNodeVersionTest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        try
        {
            var fakeNode = Path.Combine(tempRoot, "node.cmd");
            await File.WriteAllTextAsync(fakeNode, $"@echo off\r\necho {reportedVersion}\r\nexit /b 0\r\n");

            var startInfo = new ProcessStartInfo
            {
                FileName = "pwsh",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            startInfo.Environment["PIPLAY_TEST_LOCAL_CI_SCRIPT"] =
                Path.Combine(FindRepoRoot(), "scripts", "Test-LocalCI.ps1");
            startInfo.Environment["PIPLAY_TEST_FAKE_NODE"] = fakeNode;
            startInfo.Environment["PIPLAY_TEST_NODE_MINIMUM"] = minimumMajor.ToString();
            startInfo.ArgumentList.Add("-NoProfile");
            startInfo.ArgumentList.Add("-Command");
            startInfo.ArgumentList.Add("""
                $tokens = $null
                $errors = $null
                $ast = [System.Management.Automation.Language.Parser]::ParseFile(
                    $env:PIPLAY_TEST_LOCAL_CI_SCRIPT, [ref]$tokens, [ref]$errors)
                if ($errors.Count -gt 0) { throw ($errors | Out-String) }
                $functionAst = $ast.Find({
                    param($node)
                    $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and
                        $node.Name -eq 'Invoke-NodeVersionStep'
                }, $true)
                if (-not $functionAst) { throw 'Invoke-NodeVersionStep was not found.' }
                . ([scriptblock]::Create($functionAst.Extent.Text))
                $step = [pscustomobject]@{
                    name = 'node-version'
                    filePath = $env:PIPLAY_TEST_FAKE_NODE
                    arguments = @()
                }
                try {
                    Invoke-NodeVersionStep -Step $step -MinimumMajor ([int]$env:PIPLAY_TEST_NODE_MINIMUM)
                    exit 0
                } catch {
                    [Console]::Error.WriteLine($_.Exception.Message)
                    exit 1
                }
                """);

            using var process = new Process { StartInfo = startInfo };
            Assert.True(process.Start(), "Node-version function process did not start.");
            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
            return (process.ExitCode, await outputTask, await errorTask);
        }
        finally
        {
            try { Directory.Delete(tempRoot, recursive: true); } catch { /* best-effort fixture cleanup */ }
        }
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "PiPlay.sln")))
            directory = directory.Parent;

        return directory?.FullName
            ?? throw new InvalidOperationException("Could not locate the PiPlay repository root.");
    }
}
