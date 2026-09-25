#Requires -Version 5.1
<#
.SYNOPSIS
  Runs PiPlay's deterministic CI gate from one local command.

.DESCRIPTION
  Owns the command sequence shared by local development and GitHub Actions:
  SDK diagnostics, solution restore, the Debug test suite, the PowerShell harnesses for the
  destructive release scripts (staged deploy swap, cross-process publish lock), and the
  non-mutating Release build gate. Test data is redirected to a unique temporary root and
  cleaned even when a command fails.

.EXAMPLE
  pwsh -NoProfile -File .\scripts\Test-LocalCI.ps1

.EXAMPLE
  pwsh -NoProfile -File .\scripts\Test-LocalCI.ps1 -Plan -AsJson
#>
[CmdletBinding()]
param(
    [switch]$Plan,
    [switch]$AsJson,
    [switch]$KeepTestDataRoot
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

if ($AsJson -and -not $Plan) {
    throw "-AsJson is valid only with -Plan."
}

$repoRoot = Split-Path -Parent $PSScriptRoot
$testDataRoot = Join-Path ([System.IO.Path]::GetTempPath()) (
    "PiPlayLocalCI-" + [Guid]::NewGuid().ToString("N"))

function New-LocalCiPlan {
    $buildScript = Join-Path $repoRoot "Build-PiPlay.ps1"
    $deploySwapTest = Join-Path $repoRoot "scripts\Test-DeploySwap.ps1"
    $publishLockTest = Join-Path $repoRoot "scripts\Test-PublishLock.ps1"

    return [ordered]@{
        schemaVersion = 3
        repoRoot = $repoRoot
        workingDirectory = $repoRoot
        testDataRoot = $testDataRoot
        cleanupTestDataRoot = -not $KeepTestDataRoot
        requirements = [ordered]@{
            nodeMinimumMajor = 24
            dotnetGlobalJson = "global.json"
            powerShell = "pwsh"
        }
        environment = [ordered]@{
            DOTNET_CLI_TELEMETRY_OPTOUT = "1"
            DOTNET_NOLOGO = "true"
            DOTNET_SKIP_FIRST_TIME_EXPERIENCE = "1"
        }
        steps = @(
            [ordered]@{
                name = "node-version"
                filePath = "node"
                arguments = @("--version")
                environment = [ordered]@{}
            },
            [ordered]@{
                name = "dotnet-info"
                filePath = "dotnet"
                arguments = @("--info")
                environment = [ordered]@{}
            },
            [ordered]@{
                name = "restore"
                filePath = "dotnet"
                arguments = @("restore", "PiPlay.sln", "-p:BuildInParallel=false")
                environment = [ordered]@{}
            },
            [ordered]@{
                name = "test"
                filePath = "dotnet"
                arguments = @("test", "PiPlay.sln", "--configuration", "Debug", "--no-restore")
                environment = [ordered]@{
                    PIPLAY_DATA_ROOT = $testDataRoot
                }
            },
            [ordered]@{
                name = "build"
                filePath = "pwsh"
                arguments = @(
                    "-NoProfile", "-File", $buildScript,
                    "-Stage", "Build", "-NoVersionBump", "-NoBuildNumberBump")
                environment = [ordered]@{}
            },
            [ordered]@{
                name = "deploy-swap"
                filePath = "pwsh"
                arguments = @("-NoProfile", "-File", $deploySwapTest)
                environment = [ordered]@{}
            },
            [ordered]@{
                name = "publish-lock"
                filePath = "pwsh"
                arguments = @("-NoProfile", "-File", $publishLockTest)
                environment = [ordered]@{}
            }
        )
    }
}

function Write-LocalCiPlan {
    param([Parameter(Mandatory = $true)]$Value)

    Write-Host "PiPlay local CI plan" -ForegroundColor Cyan
    Write-Host "  Repository : $($Value.repoRoot)"
    Write-Host "  Test data  : $($Value.testDataRoot)"
    Write-Host "  Cleanup    : $($Value.cleanupTestDataRoot)"
    foreach ($step in $Value.steps) {
        $display = @($step.filePath) + @($step.arguments)
        Write-Host ("  [{0}] {1}" -f $step.name, ($display -join " "))
    }
}

function Invoke-NativeStep {
    param([Parameter(Mandatory = $true)]$Step)

    $filePath = [string]$Step.filePath
    $arguments = @($Step.arguments | ForEach-Object { [string]$_ })
    $display = @($filePath) + $arguments

    Write-Host ""
    Write-Host ("[{0}] {1}" -f $Step.name, ($display -join " ")) -ForegroundColor Yellow

    $global:LASTEXITCODE = 0
    & $filePath @arguments
    $exitCode = $LASTEXITCODE
    if ($exitCode -ne 0) {
        throw "Local CI step '$($Step.name)' failed with exit code $exitCode."
    }
}

function Restore-ProcessEnvironment {
    param([Parameter(Mandatory = $true)][hashtable]$Previous)

    foreach ($entry in $Previous.GetEnumerator()) {
        if ($entry.Value.Existed) {
            [Environment]::SetEnvironmentVariable(
                [string]$entry.Key, [string]$entry.Value.Value, "Process")
        } else {
            [Environment]::SetEnvironmentVariable([string]$entry.Key, $null, "Process")
        }
    }
}

function Set-ProcessEnvironment {
    param([Parameter(Mandatory = $true)]$Values)

    $previous = @{}
    try {
        foreach ($entry in $Values.GetEnumerator()) {
            $name = [string]$entry.Key
            $previous[$name] = [pscustomobject]@{
                Existed = Test-Path -LiteralPath "Env:$name"
                Value = [Environment]::GetEnvironmentVariable($name, "Process")
            }
            [Environment]::SetEnvironmentVariable($name, [string]$entry.Value, "Process")
        }
    } catch {
        Restore-ProcessEnvironment -Previous $previous
        throw
    }
    return $previous
}

function Invoke-NodeVersionStep {
    param(
        [Parameter(Mandatory = $true)]$Step,
        [Parameter(Mandatory = $true)][int]$MinimumMajor
    )

    $filePath = [string]$Step.filePath
    $arguments = @($Step.arguments | ForEach-Object { [string]$_ })
    Write-Host ""
    Write-Host ("[{0}] {1} {2}" -f $Step.name, $filePath, ($arguments -join " ")) -ForegroundColor Yellow

    $global:LASTEXITCODE = 0
    $output = @(& $filePath @arguments)
    $exitCode = $LASTEXITCODE
    $output | ForEach-Object { Write-Host $_ }
    $nodeVersion = [string]($output | Select-Object -Last 1)
    if ($exitCode -ne 0) {
        throw "Local CI step '$($Step.name)' failed with exit code $exitCode."
    }
    $versionMatch = [regex]::Match($nodeVersion, '^v?(?<major>\d+)\.')
    if (-not $versionMatch.Success) {
        throw "PiPlay local CI could not parse the Node version '$nodeVersion'."
    }
    $nodeMajor = [int]$versionMatch.Groups['major'].Value
    if ($nodeMajor -lt $MinimumMajor) {
        throw "PiPlay local CI requires Node $MinimumMajor or newer; found '$nodeVersion'."
    }
}

$localCiPlan = New-LocalCiPlan

if ($Plan) {
    if ($AsJson) {
        $localCiPlan | ConvertTo-Json -Depth 8
    } else {
        Write-LocalCiPlan -Value $localCiPlan
    }
    exit 0
}

foreach ($relativePath in @("PiPlay.sln", "global.json", "Build-PiPlay.ps1")) {
    $requiredPath = Join-Path $repoRoot $relativePath
    if (-not (Test-Path -LiteralPath $requiredPath -PathType Leaf)) {
        throw "Required local CI input not found: $requiredPath"
    }
}

foreach ($commandName in @("dotnet", "node", "pwsh")) {
    if (-not (Get-Command $commandName -ErrorAction SilentlyContinue)) {
        throw "Required local CI command '$commandName' is not available on PATH."
    }
}

$commonEnvironment = $null
$testEnvironment = $null
$locationPushed = $false
try {
    $commonEnvironment = Set-ProcessEnvironment -Values $localCiPlan.environment
    Push-Location -LiteralPath $repoRoot
    $locationPushed = $true

    foreach ($step in $localCiPlan.steps) {
        if ($step.name -eq "node-version") {
            Invoke-NodeVersionStep -Step $step -MinimumMajor $localCiPlan.requirements.nodeMinimumMajor
        } elseif ($step.name -eq "test") {
            try {
                New-Item -ItemType Directory -Path $testDataRoot -Force | Out-Null
                $testEnvironment = Set-ProcessEnvironment -Values $step.environment
                Invoke-NativeStep -Step $step
            } finally {
                try {
                    if ($null -ne $testEnvironment) {
                        Restore-ProcessEnvironment -Previous $testEnvironment
                        $testEnvironment = $null
                    }
                } finally {
                    if ($KeepTestDataRoot -and (Test-Path -LiteralPath $testDataRoot)) {
                        Write-Warning "Preserved local CI test data at '$testDataRoot'."
                    } elseif (Test-Path -LiteralPath $testDataRoot) {
                        try {
                            Remove-Item -LiteralPath $testDataRoot -Recurse -Force -ErrorAction Stop
                        } catch {
                            Write-Warning "Could not remove local CI test data at '$testDataRoot': $($_.Exception.Message)"
                        }
                    }
                }
            }
        } else {
            Invoke-NativeStep -Step $step
        }
    }

    Write-Host ""
    Write-Host "LOCAL CI: PASS" -ForegroundColor Green
} finally {
    try {
        if ($locationPushed) {
            Pop-Location
        }
    } finally {
        if ($null -ne $commonEnvironment) {
            Restore-ProcessEnvironment -Previous $commonEnvironment
        }
    }
}
