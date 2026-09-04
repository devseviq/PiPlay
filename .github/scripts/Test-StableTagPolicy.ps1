[CmdletBinding()]
param(
    [string]$Repository = $env:GITHUB_REPOSITORY
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if ([string]::IsNullOrWhiteSpace($Repository) -or
    $Repository -notmatch '^[^/\s]+/[^/\s]+$') {
    throw "Repository must be an owner/name value; received '$Repository'."
}

$summariesJson = & gh api "repos/$Repository/rulesets?includes_parents=true&per_page=100"
if ($LASTEXITCODE -ne 0) {
    throw "Could not list GitHub rulesets for '$Repository'."
}
$summaries = @($summariesJson | ConvertFrom-Json)
$requiredPattern = 'refs/tags/stable-v*'

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
        $excludePatterns -ccontains $requiredPattern) {
        continue
    }

    $ruleTypes = @($detail.rules | ForEach-Object { [string]$_.type })
    if ($ruleTypes -ccontains 'deletion' -and $ruleTypes -ccontains 'update') {
        Write-Host "IMMUTABLE STABLE TAG POLICY VERIFIED: $($detail.name) ($($detail.id))"
        exit 0
    }
}

throw "GitHub Releases are blocked: '$Repository' needs an active tag ruleset for '$requiredPattern' with restrict-updates and restrict-deletions rules."
