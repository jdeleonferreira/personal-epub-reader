<#
.SYNOPSIS
  Builds Atril for Windows and creates the Microsoft Store package (.msixbundle).

.DESCRIPTION
  Requires the .NET 8 SDK and the Windows SDK (it includes makeappx.exe and makepri.exe; install it with
  Visual Studio or from https://developer.microsoft.com/windows/downloads/windows-sdk/).

.EXAMPLE
  # Try it on your PC without packaging (requires "Developer Mode" turned on in Windows Settings):
  ./windows/package.ps1 -Try

.EXAMPLE
  # Package to upload to the Store (values from Partner Center -> Product identity):
  ./windows/package.ps1 -IdentityName "12345JaimeDeLeon.Atril" -Publisher "CN=ABCD1234-..." -PublisherName "Jaime De Leon"

.NOTES
  The Store signs the package when it certifies it: no certificate of your own is needed to upload it.
#>
param(
  [string]$IdentityName = $env:MSIX_IDENTITY_NAME,
  [string]$Publisher = $env:MSIX_PUBLISHER,
  [string]$PublisherName = $env:MSIX_PUBLISHER_NAME,
  [string]$DisplayName = $(if ($env:MSIX_DISPLAY_NAME) { $env:MSIX_DISPLAY_NAME } else { 'Atril' }),
  [string[]]$Architectures = @('x64', 'arm64'),
  [string]$Output = 'publish/msix',
  [switch]$Try
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
Set-Location $root

# Test values when the Store ones aren't given (good for installing on your PC, not for publishing)
if (-not $IdentityName) { $IdentityName = 'Atril.Development' }
if (-not $Publisher) { $Publisher = 'CN=Atril Development' }
if (-not $PublisherName) { $PublisherName = 'Atril' }

$version = ([xml](Get-Content Directory.Build.props)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
if ($version -notmatch '^\d+\.\d+\.\d+$') { throw "Invalid version in Directory.Build.props: $version" }
$msixVersion = "$version.0"   # the Store requires the fourth number to be 0

function Find-Tool($name) {
  $kits = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin'
  $found = Get-ChildItem $kits -Recurse -Filter $name -ErrorAction SilentlyContinue |
    Where-Object { $_.FullName -match '\\x64\\' } | Sort-Object FullName -Descending | Select-Object -First 1
  if (-not $found) { throw "$name not found. Install the Windows SDK." }
  $found.FullName
}

$template = Get-Content windows/msix/AppxManifest.template.xml -Raw
function Write-Manifest($destination, $arch) {
  $m = $template.Replace('{{IDENTITY_NAME}}', $IdentityName).Replace('{{PUBLISHER}}', [Security.SecurityElement]::Escape($Publisher)).
    Replace('{{PUBLISHER_NAME}}', [Security.SecurityElement]::Escape($PublisherName)).Replace('{{DISPLAY_NAME}}', [Security.SecurityElement]::Escape($DisplayName)).
    Replace('{{VERSION}}', $msixVersion).Replace('{{ARCH}}', $arch)
  Set-Content -Path (Join-Path $destination 'AppxManifest.xml') -Value $m -Encoding utf8
}

function Publish-Arch($arch) {
  $layout = Join-Path $Output "layout-$arch"
  if (Test-Path $layout) { Remove-Item $layout -Recurse -Force }
  Write-Host "Building Atril $version for $arch..." -ForegroundColor Cyan
  dotnet publish windows/Atril.Windows.csproj -c Release -r "win-$arch" --self-contained true -p:PublishSingleFile=false -o $layout
  if ($LASTEXITCODE) { throw 'dotnet publish failed' }
  Get-ChildItem $layout -Filter *.pdb -Recurse | Remove-Item -Force
  Copy-Item windows/msix/Assets (Join-Path $layout 'Assets') -Recurse
  Write-Manifest $layout $arch
  $layout
}

New-Item -ItemType Directory -Force -Path $Output | Out-Null

if ($Try) {
  $layout = Publish-Arch 'x64'
  Get-AppxPackage -Name $IdentityName | Remove-AppxPackage -ErrorAction SilentlyContinue
  Add-AppxPackage -Register (Join-Path $layout 'AppxManifest.xml')
  Write-Host "Done: look for '$DisplayName' in the Start menu. To remove it: Get-AppxPackage $IdentityName | Remove-AppxPackage" -ForegroundColor Green
  return
}

$makeappx = Find-Tool 'makeappx.exe'
$makepri = Find-Tool 'makepri.exe'
$packages = Join-Path $Output 'packages'
if (Test-Path $packages) { Remove-Item $packages -Recurse -Force }
New-Item -ItemType Directory -Force -Path $packages | Out-Null

foreach ($arch in $Architectures) {
  $layout = Publish-Arch $arch
  # Resource index: lets Windows pick the right logo size
  $priconfig = Join-Path $Output 'priconfig.xml'
  & $makepri createconfig /cf $priconfig /dq en-US /pv 10.0.0 /o | Out-Null
  & $makepri new /pr $layout /cf $priconfig /mn (Join-Path $layout 'AppxManifest.xml') /of (Join-Path $layout 'resources.pri') /o | Out-Null
  if ($LASTEXITCODE) { throw 'makepri failed' }
  & $makeappx pack /d $layout /p (Join-Path $packages "Atril_${msixVersion}_$arch.msix") /o
  if ($LASTEXITCODE) { throw 'makeappx pack failed' }
}

$bundle = Join-Path $Output "Atril_$msixVersion.msixbundle"
& $makeappx bundle /d $packages /p $bundle /bv $msixVersion /o
if ($LASTEXITCODE) { throw 'makeappx bundle failed' }
Write-Host "Package ready: $bundle" -ForegroundColor Green
if ($IdentityName -eq 'Atril.Development') {
  Write-Host 'Note: test values were used. For the Store pass -IdentityName, -Publisher and -PublisherName from Partner Center.' -ForegroundColor Yellow
}
