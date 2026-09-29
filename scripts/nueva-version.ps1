<#
.SYNOPSIS
  Prepara una nueva versión de Atril: actualiza el número (Directory.Build.props, usado por la app web y la de Windows),
  el CHANGELOG y el service worker, hace commit y crea la etiqueta.

.EXAMPLE
  ./scripts/nueva-version.ps1 1.1.0
  git push --follow-tags        # sube el commit y la etiqueta; GitHub Actions crea la Release

.NOTES
  Versionado semántico (MAYOR.MENOR.PARCHE):
    PARCHE  corrige errores sin cambiar el uso        1.0.0 -> 1.0.1
    MENOR   agrega funciones compatibles              1.0.1 -> 1.1.0
    MAYOR   cambios que rompen algo existente         1.1.0 -> 2.0.0
  Antes de ejecutarlo, anota los cambios en CHANGELOG.md bajo "## [Sin publicar]".
#>
param([Parameter(Mandatory)][ValidatePattern('^\d+\.\d+\.\d+$')][string]$Version)
$ErrorActionPreference = 'Stop'
Set-Location (git rev-parse --show-toplevel)

if (git status --porcelain) { throw 'Hay cambios sin guardar. Haz commit o descártalos antes de crear la versión.' }
if (git tag --list "v$Version") { throw "La versión v$Version ya existe." }

$props = Get-Content Directory.Build.props -Raw
$anterior = [regex]::Match($props, '<Version>([^<]+)</Version>').Groups[1].Value
if ([version]$Version -le [version]$anterior) { throw "La nueva versión ($Version) debe ser mayor que la actual ($anterior)." }

$log = Get-Content CHANGELOG.md -Raw
if ($log -notmatch '## \[Sin publicar\]\s*\r?\n\s*###') { throw 'Anota los cambios en CHANGELOG.md bajo "## [Sin publicar]" antes de crear la versión.' }

$hoy = Get-Date -Format 'yyyy-MM-dd'
Set-Content Directory.Build.props ($props -replace '<Version>[^<]+</Version>', "<Version>$Version</Version>") -NoNewline
Set-Content CHANGELOG.md ($log -replace '## \[Sin publicar\]', "## [Sin publicar]`n`n## [$Version] - $hoy") -NoNewline
$sw = Get-Content wwwroot/sw.js -Raw
Set-Content wwwroot/sw.js ($sw -replace 'const CACHE = "atril-[^"]+";', "const CACHE = `"atril-$Version`";") -NoNewline

git add Directory.Build.props CHANGELOG.md wwwroot/sw.js
git commit -m "Versión $Version"
git tag -a "v$Version" -m "Atril $Version"
Write-Host "Listo: v$Version. Súbela con:  git push --follow-tags" -ForegroundColor Green
