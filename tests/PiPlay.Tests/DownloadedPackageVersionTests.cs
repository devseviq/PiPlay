using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace PiPlay.Tests;

public class DownloadedPackageVersionTests
{
    [Theory]
    [InlineData("0.14.0-beta.1", "0.14.0.40", "0.14.0-beta.1", null)]
    [InlineData("0.14.0", "0.14.0.40", "0.14.0", null)]
    [InlineData("0.14.0-beta.1", "0.13.0.40", "0.14.0-beta.1", "FileVersion")]
    [InlineData("0.14.0-beta.1", "0.14.0.39", "0.14.0-beta.1", "FileVersion")]
    [InlineData("0.14.0-beta.1", "0.14.0.40", "0.14.0", "ProductVersion")]
    [InlineData("0.14.0-beta.1", "0.14.0.40", "0.14.0-beta.2", "ProductVersion")]
    [InlineData("0.14.0-beta.1", "0.14.0.40", "0.14.0-BETA.1", "ProductVersion")]
    [InlineData("0.14.0-beta..1", "0.14.0.40", "0.14.0-beta..1", "version must")]
    [InlineData("0.14.0-beta.01", "0.14.0.40", "0.14.0-beta.01", "version must")]
    [InlineData("00.14.0-beta.1", "0.14.0.40", "00.14.0-beta.1", "version must")]
    [InlineData("0.14.0-beta.1\n", "0.14.0.40", "0.14.0-beta.1", "version must")]
    public async Task Verifier_binds_numeric_file_version_and_complete_semantic_product_version(
        string version, string fileVersion, string productVersion, string? expectedError)
    {
        var result = await VerifyFixtureAsync(version, 40, fileVersion, productVersion);
        if (expectedError is null)
        {
            Assert.True(result.ExitCode == 0, result.Output + result.Error);
            Assert.Contains("PACKAGE VERIFIED: Test", result.Output);
        }
        else
        {
            Assert.NotEqual(0, result.ExitCode);
            Assert.Contains(expectedError, result.Error);
        }
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(65536)]
    [InlineData(40.5)]
    [InlineData("40")]
    public async Task Verifier_rejects_non_integer_or_out_of_range_build_numbers(object buildNumber)
    {
        var result = await VerifyFixtureAsync("0.14.0-beta.1", buildNumber, "0.14.0.40", "0.14.0-beta.1");
        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("buildNumber must", result.Error);
    }

    private static async Task<(int ExitCode, string Output, string Error)> VerifyFixtureAsync(
        string version, object buildNumber, string fileVersion, string productVersion)
    {
        var repoRoot = new DirectoryInfo(AppContext.BaseDirectory);
        while (repoRoot is not null && !File.Exists(Path.Combine(repoRoot.FullName, "PiPlay.sln")))
            repoRoot = repoRoot.Parent;
        Assert.NotNull(repoRoot);

        var tempRoot = Path.Combine(Path.GetTempPath(), "PiPlayPackageVersionTest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        try
        {
            // A tiny SDK-built app supplies real apphost version resources and assembly metadata.
            // ValidateOnly inspects it; neither the fixture nor UI smoke is started.
            var fixtureScript = Path.Combine(tempRoot, "fixture.ps1");
            await File.WriteAllTextAsync(fixtureScript, """
                $ErrorActionPreference = 'Stop'
                $packageRoot = Join-Path $PSScriptRoot 'package'
                $scriptsRoot = Join-Path $packageRoot 'scripts'
                New-Item -ItemType Directory -Path $scriptsRoot -Force | Out-Null
                $projectRoot = Join-Path $PSScriptRoot 'fixture-project'
                New-Item -ItemType Directory -Path $projectRoot | Out-Null
                $project = Join-Path $projectRoot 'PiPlay.csproj'
                @'
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                    <OutputType>Exe</OutputType>
                    <IncludeSourceRevisionInInformationalVersion>false</IncludeSourceRevisionInInformationalVersion>
                  </PropertyGroup>
                  <ItemGroup><AssemblyMetadata Include="PiPlay.Channel" Value="Stable" /></ItemGroup>
                </Project>
                '@ | Set-Content -LiteralPath $project
                'System.Console.WriteLine("Fixture must never run.");' |
                    Set-Content -LiteralPath (Join-Path $projectRoot 'Program.cs')
                & dotnet build $project --output $packageRoot --nologo --verbosity quiet `
                    "-p:RestoreSources=$projectRoot" '-p:UseSharedCompilation=false' `
                    "-p:FileVersion=$env:PIPLAY_FIXTURE_FILE_VERSION" `
                    "-p:InformationalVersion=$env:PIPLAY_FIXTURE_PRODUCT_VERSION"
                if ($LASTEXITCODE -ne 0) { throw 'Fixture build failed.' }
                foreach ($name in @('Test-DownloadedPackage.ps1', 'Test-UiSmoke.ps1')) {
                    Copy-Item -LiteralPath (Join-Path $env:PIPLAY_FIXTURE_SCRIPTS $name) -Destination $scriptsRoot
                }
                $commit = '1111111111111111111111111111111111111111'
                $entries = @(Get-ChildItem -LiteralPath $packageRoot -Recurse -File | ForEach-Object {
                    [ordered]@{
                        path = [IO.Path]::GetRelativePath($packageRoot, $_.FullName).Replace('\', '/')
                        size = $_.Length
                        sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
                    }
                })
                $manifest = [ordered]@{
                    project = 'PiPlay'
                    version = $env:PIPLAY_FIXTURE_VERSION
                    buildNumber = ($env:PIPLAY_FIXTURE_BUILD | ConvertFrom-Json)
                    publishLabel = "test-$commit"
                    channel = 'Stable'
                    configuration = 'Release'
                    sourceCommit = $commit
                    publishedArtifacts = @('PiPlay.exe')
                    primaryArtifact = 'PiPlay.exe'
                    artifactCount = $entries.Count
                    artifactHashes = $entries
                    releaseEvidence = $false
                    releaseEvidenceReason = 'GitHub test prerelease; interactive verification pending on SND-DESK'
                    sourceDirty = $false
                    sourceDirtyEntries = @()
                    fileVersion = $env:PIPLAY_FIXTURE_FILE_VERSION
                    productVersion = $env:PIPLAY_FIXTURE_PRODUCT_VERSION
                } | ConvertTo-Json -Depth 8
                foreach ($name in @('build-info.json', 'BUILDINFO.json')) {
                    Set-Content -LiteralPath (Join-Path $packageRoot $name) -Value $manifest -Encoding utf8NoBOM
                }
                & (Join-Path $scriptsRoot 'Test-DownloadedPackage.ps1') -Kind Test -ExpectedCommit $commit -ValidateOnly
                """);

            var startInfo = new ProcessStartInfo("pwsh")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            startInfo.ArgumentList.Add("-NoProfile");
            startInfo.ArgumentList.Add("-File");
            startInfo.ArgumentList.Add(fixtureScript);
            startInfo.Environment["PIPLAY_FIXTURE_SCRIPTS"] = Path.Combine(repoRoot.FullName, "scripts");
            startInfo.Environment["PIPLAY_FIXTURE_VERSION"] = version;
            startInfo.Environment["PIPLAY_FIXTURE_BUILD"] = JsonSerializer.Serialize(buildNumber);
            startInfo.Environment["PIPLAY_FIXTURE_FILE_VERSION"] = fileVersion;
            startInfo.Environment["PIPLAY_FIXTURE_PRODUCT_VERSION"] = productVersion;
            using var process = Process.Start(startInfo);
            Assert.NotNull(process);
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            var exit = process.WaitForExitAsync();
            if (await Task.WhenAny(exit, Task.Delay(TimeSpan.FromSeconds(60))) != exit)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
                Assert.Fail("Package verifier fixture exceeded 60 seconds.");
            }
            await exit;
            return (process.ExitCode, await output, await error);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }
}
