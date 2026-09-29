<#
.SYNOPSIS
  Prepares a new Atril version: updates the number (Directory.Build.props, used by the web and Windows apps),
  the CHANGELOG and the service worker, commits and creates the tag.

.EXAMPLE
  ./scripts/new-version.ps1 1.2.0
  git push --follow-tags        # pushes the commit and the tag; GitHub Actions creates the Release

.NOTES
  Semantic versioning (MAJOR.MINOR.PATCH):
    PATCH  fixes bugs without changing how it's used    1.0.0 -> 1.0.1
    MINOR  adds compatible features                     1.0.1 -> 1.1.0
    MAJOR  changes that break something existing        1.1.0 -> 2.0.0
  Before running it, write the changes in CHANGELOG.md under "## [Unreleased]".
#>
param([Parameter(Mandatory)][ValidatePattern('^\d+\.\d+\.\d+$')][string]$Version)
$ErrorActionPreference = 'Stop'
Set-Location (git rev-parse --show-toplevel)

if (git status --porcelain) { throw 'There are uncommitted changes. Commit or discard them before creating the version.' }
if (git tag --list "v$Version") { throw "Version v$Version already exists." }

$props = Get-Content Directory.Build.props -Raw
$previous = [regex]::Match($props, '<Version>([^<]+)</Version>').Groups[1].Value
if ([version]$Version -le [version]$previous) { throw "The new version ($Version) must be greater than the current one ($previous)." }

$log = Get-Content CHANGELOG.md -Raw
if ($log -notmatch '## \[Unreleased\]\s*\r?\n\s*###') { throw 'Write the changes in CHANGELOG.md under "## [Unreleased]" before creating the version.' }

$today = Get-Date -Format 'yyyy-MM-dd'
Set-Content Directory.Build.props ($props -replace '<Version>[^<]+</Version>', "<Version>$Version</Version>") -NoNewline
Set-Content CHANGELOG.md ($log -replace '## \[Unreleased\]', "## [Unreleased]`n`n## [$Version] - $today") -NoNewline
$sw = Get-Content wwwroot/sw.js -Raw
Set-Content wwwroot/sw.js ($sw -replace 'const CACHE = "atril-[^"]+";', "const CACHE = `"atril-$Version`";") -NoNewline

git add Directory.Build.props CHANGELOG.md wwwroot/sw.js
git commit -m "Version $Version"
git tag -a "v$Version" -m "Atril $Version"
Write-Host "Done: v$Version. Push it with:  git push --follow-tags" -ForegroundColor Green
