#Requires -Version 5.1
param([Parameter(Mandatory = $true)][string]$PublishScript)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# Run the release path from the production script while keeping Git and verification inside a
# disposable fixture. No build or deployment runs; the tag side effect is real.
$tokens = $null
$parseErrors = $null
$ast = [System.Management.Automation.Language.Parser]::ParseFile($PublishScript, [ref]$tokens, [ref]$parseErrors)
if ($parseErrors.Count -gt 0) { throw "Could not parse Publish-Stable.ps1: $($parseErrors[0])" }

foreach ($functionName in @('Invoke-Git', 'Assert-StableTag')) {
    $definition = @($ast.FindAll({
        param($node)
        $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and
            $node.Name -eq $functionName
    }, $true))
    if ($definition.Count -ne 1) { throw "Expected one $functionName function in Publish-Stable.ps1." }
    Invoke-Expression $definition[0].Extent.Text
}

$releaseBranches = @($ast.FindAll({
    param($node)
    $node -is [System.Management.Automation.Language.IfStatementAst] -and
        $node.Clauses[0].Item1.Extent.Text -match '\$AllowDirty\s+-or\s+\$AllowVersionBump\s+-or\s+\$SkipTests' -and
        $node.ElseClause -and $node.ElseClause.Extent.Text.Contains('Final verification (full release checks')
}, $true))
if ($releaseBranches.Count -ne 1) { throw 'Expected one release verification branch in Publish-Stable.ps1.' }
$releaseBody = $releaseBranches[0].ElseClause.Extent.Text
$releaseBranch = [scriptblock]::Create($releaseBody.Substring(1, $releaseBody.Length - 2))

. (Join-Path (Split-Path -Parent $PublishScript) 'NativeCommand.ps1')
function Write-Step([int]$n, [string]$message) { }

$tempRoot = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath()).TrimEnd('\') + '\'
$fixtureRoot = [System.IO.Path]::GetFullPath((Join-Path $tempRoot ('PiPlayStableTag-' + [guid]::NewGuid().ToString('N'))))
if (-not $fixtureRoot.StartsWith($tempRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw 'Fixture path escaped the temporary directory.'
}
try {
    $repoRoot = Join-Path $fixtureRoot 'repo'
    New-Item -ItemType Directory -Path $repoRoot -Force | Out-Null
    & git -C $repoRoot init -q
    if ($LASTEXITCODE -ne 0) { throw 'Could not initialize fixture repository.' }
    & git -C $repoRoot -c user.name=PiPlayTest -c user.email=piplay-test@example.invalid commit --allow-empty -qm fixture
    if ($LASTEXITCODE -ne 0) { throw 'Could not commit fixture source.' }
    $commit = (& git -C $repoRoot rev-parse HEAD).Trim()

    $verifyScript = Join-Path $fixtureRoot 'Fail-FinalVerification.ps1'
    Set-Content -LiteralPath $verifyScript -Value 'param([string]$DeployRoot, [switch]$AllowMissingStableTag); if ($AllowMissingStableTag) { $global:LASTEXITCODE = 0 } else { $global:LASTEXITCODE = 1 }'
    $DeployRoot = Join-Path $fixtureRoot 'stable'

    foreach ($case in @(
        @{ Name = 'pre-existing'; Build = 900; ExistsBefore = $true; ExistsAfter = $true },
        @{ Name = 'new'; Build = 901; ExistsBefore = $false; ExistsAfter = $false }
    )) {
        $stableTag = "stable-v0.14.0-b$($case.Build)"
        if ($case.ExistsBefore) {
            & git -C $repoRoot tag $stableTag $commit
            if ($LASTEXITCODE -ne 0) { throw "Could not create fixture tag $stableTag." }
        }
        $buildInfo = [pscustomobject]@{ version = '0.14.0'; buildNumber = $case.Build; sourceCommit = $commit }
        $failure = $null
        try {
            & $releaseBranch
        } catch {
            $failure = $_.Exception.Message
        }
        if (-not $failure -or -not $failure.Contains('failed final verification')) {
            throw "$($case.Name): expected final verification failure; got '$failure'."
        }
        $tagCommit = & git -C $repoRoot rev-list -n 1 $stableTag 2>$null
        $existsAfter = $LASTEXITCODE -eq 0 -and -not [string]::IsNullOrWhiteSpace($tagCommit)
        if ($existsAfter -ne $case.ExistsAfter) {
            throw "$($case.Name): tag should exist after failure=$($case.ExistsAfter), observed=$existsAfter. Error: $failure"
        }
        if ($existsAfter -and $tagCommit.Trim() -ne $commit) {
            throw "$($case.Name): preserved tag no longer points at fixture HEAD."
        }
        Write-Host "PASS $($case.Name): tag present after failed final verification=$existsAfter"
    }
} finally {
    if (Test-Path -LiteralPath $fixtureRoot) { Remove-Item -LiteralPath $fixtureRoot -Recurse -Force }
}
