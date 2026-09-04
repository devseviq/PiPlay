[CmdletBinding()]
param(
    [string]$Repository = $env:GITHUB_REPOSITORY,
    [string]$RequiredPattern = 'refs/tags/stable-v*'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if ([string]::IsNullOrWhiteSpace($Repository) -or
    $Repository -notmatch '^[^/\s]+/[^/\s]+$') {
    throw "Repository must be an owner/name value; received '$Repository'."
}
if ([string]::IsNullOrWhiteSpace($env:GH_TOKEN)) {
    throw 'GH_TOKEN must expose ruleset bypass actors; use a policy credential with ruleset write access scoped to this repository.'
}
if ($RequiredPattern -notmatch '^refs/tags/[^\s]+$') {
    throw "RequiredPattern must identify tags; received '$RequiredPattern'."
}

$summariesJson = & gh api --paginate "repos/$Repository/rulesets?includes_parents=true"
if ($LASTEXITCODE -ne 0) {
    throw "Could not list GitHub rulesets for '$Repository'."
}
$summaries = @($summariesJson | ConvertFrom-Json)
$requiredPattern = $RequiredPattern

foreach ($summary in $summaries) {
    $detailJson = & gh api "repos/$Repository/rulesets/$($summary.id)"
    if ($LASTEXITCODE -ne 0) {
        throw "Could not inspect GitHub ruleset '$($summary.id)' for '$Repository'."
    }
    $detail = $detailJson | ConvertFrom-Json
    if ($detail.target -cne 'tag' -or $detail.enforcement -cne 'active') {
        continue
    }

    $includePatterns = @($detail.conditions.ref_name.include | ForEach-Object { [string]$_ })
    $excludePatterns = @($detail.conditions.ref_name.exclude | ForEach-Object { [string]$_ })
    if ($includePatterns -cnotcontains $requiredPattern -or
        $excludePatterns.Count -ne 0) {
        continue
    }

    $bypassProperty = $detail.PSObject.Properties['bypass_actors']
    if ($null -eq $bypassProperty -or
        $null -eq $bypassProperty.Value -or
        $bypassProperty.Value -isnot [System.Array]) {
        continue
    }
    $bypassActors = @($bypassProperty.Value)
    if ($bypassActors.Count -ne 0) {
        continue
    }

    $ruleTypes = @($detail.rules | ForEach-Object { [string]$_.type })
    if ($ruleTypes -ccontains 'deletion' -and $ruleTypes -ccontains 'update') {
        Write-Host "IMMUTABLE STABLE TAG POLICY VERIFIED: $($detail.name) ($($detail.id))"
        exit 0
    }
}

throw "GitHub Releases are blocked: '$Repository' needs an active, exclusion-free tag ruleset for '$requiredPattern' with restrict-updates, restrict-deletions, and no bypass actors. GH_TOKEN must expose the bypass list; use a policy credential with ruleset write access scoped to this repository."
