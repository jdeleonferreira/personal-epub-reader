<#
.SYNOPSIS
  Compila Atril para Windows y crea el paquete para la Microsoft Store (.msixbundle).

.DESCRIPTION
  Requiere .NET 8 SDK y el Windows SDK (trae makeappx.exe y makepri.exe; se instala con
  Visual Studio o desde https://developer.microsoft.com/windows/downloads/windows-sdk/).

.EXAMPLE
  # Probar en tu PC sin empaquetar (requiere "Modo de desarrollador" activado en Configuración de Windows):
  ./windows/empaquetar.ps1 -Probar

.EXAMPLE
  # Paquete para subir a la Tienda (valores de Partner Center → Identidad del producto):
  ./windows/empaquetar.ps1 -Identidad "12345JaimeDeLeon.Atril" -Editor "CN=ABCD1234-..." -EditorNombre "Jaime De Leon"

.NOTES
  La Tienda firma el paquete al certificarlo: no hace falta certificado propio para subirlo.
#>
param(
  [string]$Identidad = $env:MSIX_IDENTIDAD,
  [string]$Editor = $env:MSIX_EDITOR,
  [string]$EditorNombre = $env:MSIX_EDITOR_NOMBRE,
  [string]$Nombre = $(if ($env:MSIX_NOMBRE) { $env:MSIX_NOMBRE } else { 'Atril' }),
  [string[]]$Arquitecturas = @('x64', 'arm64'),
  [string]$Salida = 'publicado/msix',
  [switch]$Probar
)
$ErrorActionPreference = 'Stop'
$raiz = Split-Path $PSScriptRoot -Parent
Set-Location $raiz

# Valores de prueba si no se indican los de la Tienda (sirven para instalar en tu PC, no para publicar)
if (-not $Identidad) { $Identidad = 'Atril.Desarrollo' }
if (-not $Editor) { $Editor = 'CN=Atril Desarrollo' }
if (-not $EditorNombre) { $EditorNombre = 'Atril' }

$version = ([xml](Get-Content Directory.Build.props)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
if ($version -notmatch '^\d+\.\d+\.\d+$') { throw "Versión no válida en Directory.Build.props: $version" }
$versionMsix = "$version.0"   # la Tienda exige que el cuarto número sea 0

function Buscar-Herramienta($nombre) {
  $kits = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin'
  $hallada = Get-ChildItem $kits -Recurse -Filter $nombre -ErrorAction SilentlyContinue |
    Where-Object { $_.FullName -match '\\x64\\' } | Sort-Object FullName -Descending | Select-Object -First 1
  if (-not $hallada) { throw "No se encontró $nombre. Instala el Windows SDK." }
  $hallada.FullName
}

$plantilla = Get-Content windows/msix/AppxManifest.plantilla.xml -Raw
function Escribir-Manifiesto($destino, $arq) {
  $m = $plantilla.Replace('{{IDENTIDAD}}', $Identidad).Replace('{{EDITOR}}', [Security.SecurityElement]::Escape($Editor)).
    Replace('{{EDITOR_NOMBRE}}', [Security.SecurityElement]::Escape($EditorNombre)).Replace('{{NOMBRE}}', [Security.SecurityElement]::Escape($Nombre)).
    Replace('{{VERSION}}', $versionMsix).Replace('{{ARQ}}', $arq)
  Set-Content -Path (Join-Path $destino 'AppxManifest.xml') -Value $m -Encoding utf8
}

function Publicar($arq) {
  $capa = Join-Path $Salida "capa-$arq"
  if (Test-Path $capa) { Remove-Item $capa -Recurse -Force }
  Write-Host "Compilando Atril $version para $arq..." -ForegroundColor Cyan
  dotnet publish windows/Atril.Windows.csproj -c Release -r "win-$arq" --self-contained true -p:PublishSingleFile=false -o $capa
  if ($LASTEXITCODE) { throw 'dotnet publish falló' }
  Get-ChildItem $capa -Filter *.pdb -Recurse | Remove-Item -Force
  Copy-Item windows/msix/Assets (Join-Path $capa 'Assets') -Recurse
  Escribir-Manifiesto $capa $arq
  $capa
}

New-Item -ItemType Directory -Force -Path $Salida | Out-Null

if ($Probar) {
  $capa = Publicar 'x64'
  Get-AppxPackage -Name $Identidad | Remove-AppxPackage -ErrorAction SilentlyContinue
  Add-AppxPackage -Register (Join-Path $capa 'AppxManifest.xml')
  Write-Host "Listo: busca '$Nombre' en el menú Inicio. Para quitarlo: Get-AppxPackage $Identidad | Remove-AppxPackage" -ForegroundColor Green
  return
}

$makeappx = Buscar-Herramienta 'makeappx.exe'
$makepri = Buscar-Herramienta 'makepri.exe'
$paquetes = Join-Path $Salida 'paquetes'
if (Test-Path $paquetes) { Remove-Item $paquetes -Recurse -Force }
New-Item -ItemType Directory -Force -Path $paquetes | Out-Null

foreach ($arq in $Arquitecturas) {
  $capa = Publicar $arq
  # Índice de recursos: permite a Windows elegir el logo del tamaño correcto
  $priconfig = Join-Path $Salida 'priconfig.xml'
  & $makepri createconfig /cf $priconfig /dq es-ES /pv 10.0.0 /o | Out-Null
  & $makepri new /pr $capa /cf $priconfig /mn (Join-Path $capa 'AppxManifest.xml') /of (Join-Path $capa 'resources.pri') /o | Out-Null
  if ($LASTEXITCODE) { throw 'makepri falló' }
  & $makeappx pack /d $capa /p (Join-Path $paquetes "Atril_${versionMsix}_$arq.msix") /o
  if ($LASTEXITCODE) { throw 'makeappx pack falló' }
}

$bundle = Join-Path $Salida "Atril_$versionMsix.msixbundle"
& $makeappx bundle /d $paquetes /p $bundle /bv $versionMsix /o
if ($LASTEXITCODE) { throw 'makeappx bundle falló' }
Write-Host "Paquete listo: $bundle" -ForegroundColor Green
if ($Identidad -eq 'Atril.Desarrollo') {
  Write-Host 'Aviso: se usaron valores de prueba. Para la Tienda indica -Identidad, -Editor y -EditorNombre de Partner Center.' -ForegroundColor Yellow
}
