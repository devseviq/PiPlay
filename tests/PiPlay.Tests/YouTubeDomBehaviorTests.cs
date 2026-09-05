using System.Diagnostics;
using System.IO;
using System.Text.Json;
using PiPlay.Services;

namespace PiPlay.Tests;

[Trait(TestCategories.Key, TestCategories.Logic)]
public class YouTubeDomBehaviorTests
{
    private const string Nonce = "0123456789abcdef0123456789abcdef";
    private const string DocumentToken = "fedcba9876543210fedcba9876543210";
    private const string ReplacementDocumentToken = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    [Fact]
    public async Task Generated_surface_scripts_enforce_executable_dom_compliance_Q3_Q5_Q8()
    {
        var repoRoot = Path.GetFullPath(Path.Combine(XamlTestFiles.SrcDir, "..", ".."));
        var harnessPath = Path.Combine(
            repoRoot, "tests", "PiPlay.Tests", "Infrastructure", "YouTubeDomBehaviorHarness.cjs");
        Assert.True(File.Exists(harnessPath), $"DOM behavior harness was not found at {harnessPath}.");

        var input = JsonSerializer.Serialize(new
        {
            nonce = Nonce,
            documentToken = DocumentToken,
            replacementDocumentToken = ReplacementDocumentToken,
            passiveScript = YouTubeDomBridge.BuildPassiveSurfaceDragScript(Nonce, 4, 4),
            passiveAuthorizeScript = YouTubeDomBridge.BuildPassiveSurfaceDocumentTokenScript(DocumentToken),
            passiveReauthorizeScript = YouTubeDomBridge.BuildPassiveSurfaceDocumentTokenScript(ReplacementDocumentToken),
            focusedScript = YouTubeDomBridge.BuildPlayerFirstSurfaceScript(
                Nonce, "#2BAED0", fadeEnabled: true, fadeDelayMs: 2500),
            focusedAuthorizeScript = YouTubeDomBridge.BuildPlayerFirstDocumentTokenScript(DocumentToken),
            focusedStateRequestScript = YouTubeDomBridge.BuildPlayerFirstStateRequestScript(),
            adStateProbeScript = YouTubeDomBridge.BuildAdStateProbeScript(),
            seekScript = YouTubeDomBridge.BuildSeekScript(95),
            seekAndPauseScript = YouTubeDomBridge.BuildSeekAndPauseScript(95),
            seekAndPlayScript = YouTubeDomBridge.BuildSeekAndPlayScript(95),
            playbackSettingsScript = YouTubeDomBridge.BuildPlaybackSettingsScript(0.4, muted: true, playbackRate: 1.75),
        });

        var startInfo = new ProcessStartInfo
        {
            FileName = "node",
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add(harnessPath);

        using var process = new Process { StartInfo = startInfo };
        Assert.True(process.Start(), "Node DOM behavior harness did not start.");

        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        await process.StandardInput.WriteAsync(input);
        process.StandardInput.Close();

        var exitTask = process.WaitForExitAsync();
        var completed = await Task.WhenAny(exitTask, Task.Delay(TimeSpan.FromSeconds(15)));
        if (completed != exitTask)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* best-effort timeout cleanup */ }
            Assert.Fail("Node DOM behavior harness exceeded the 15-second deterministic-test budget.");
        }

        await exitTask;
        var output = await standardOutput;
        var error = await standardError;
        var diagnostics = $"{output}{Environment.NewLine}{error}".Trim();

        Assert.True(process.ExitCode == 0,
            $"Node DOM behavior harness exited with code {process.ExitCode}.{Environment.NewLine}{diagnostics}");
        Assert.Contains("DOM HARNESS PASS", output, StringComparison.Ordinal);
    }
}
