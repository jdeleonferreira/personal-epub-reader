<#
.SYNOPSIS
  Builds Atril for Windows and installs it on this computer as a normal app (no Microsoft Store).

.DESCRIPTION
  - Installs into your user folder (%LOCALAPPDATA%\Programs\Atril): no administrator rights needed.
  - Adds Atril to the Start menu (and optionally the desktop).
  - Adds Atril to "Open with" for .epub files.
  - Registers it in Settings → Apps, so it can be uninstalled like any other app.
  Run it again after `git pull` to update Atril. Your books, library and settings are kept.

  Requires the .NET 8 SDK (dotnet --version). Atril uses the Microsoft Edge WebView2 Runtime,
  which comes with Windows 10 and 11.

.EXAMPLE
  ./windows/install.ps1
  ./windows/install.ps1 -DesktopShortcut
  ./windows/install.ps1 -Uninstall
#>
param(
  [string]$InstallDir = (Join-Path $env:LOCALAPPDATA 'Programs\Atril'),
  [switch]$DesktopShortcut,
  [switch]$Uninstall
)
$ErrorActionPreference = 'Stop'

$startMenuLink = Join-Path ([Environment]::GetFolderPath('Programs')) 'Atril.lnk'
$desktopLink = Join-Path ([Environment]::GetFolderPath('Desktop')) 'Atril.lnk'
$uninstallKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\Atril'
$progId = 'Atril.epub'
$exe = Join-Path $InstallDir 'Atril.exe'

function Stop-Atril {
  Get-Process -Name 'Atril' -ErrorAction SilentlyContinue |
    Where-Object { $_.Path -and $_.Path.StartsWith($InstallDir, [StringComparison]::OrdinalIgnoreCase) } |
    ForEach-Object { Write-Host 'Closing Atril...'; $_.CloseMainWindow() | Out-Null; if (-not $_.WaitForExit(5000)) { $_.Kill() } }
}

function Remove-Atril {
  Stop-Atril
  foreach ($link in @($startMenuLink, $desktopLink)) { if (Test-Path $link) { Remove-Item $link -Force } }
  foreach ($key in @($uninstallKey, "HKCU:\Software\Classes\$progId", 'HKCU:\Software\Classes\Applications\Atril.exe')) {
    if (Test-Path $key) { Remove-Item $key -Recurse -Force }
  }
  $openWith = 'HKCU:\Software\Classes\.epub\OpenWithProgids'
  if (Test-Path $openWith) { Remove-ItemProperty -Path $openWith -Name $progId -ErrorAction SilentlyContinue }
  if (Test-Path $InstallDir) { Remove-Item $InstallDir -Recurse -Force }
}

if ($Uninstall) {
  Remove-Atril
  Write-Host 'Atril was uninstalled. Your books and library data were kept:' -ForegroundColor Green
  Write-Host "  books:          the folder you chose (default: Documents\Atril)"
  Write-Host "  library, data:  $(Join-Path $env:LOCALAPPDATA 'Atril')  (delete it to remove everything)"
  return
}

# ---------- Build ----------
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { throw 'The .NET 8 SDK is required: https://dotnet.microsoft.com/download' }
$root = Split-Path $PSScriptRoot -Parent
$arch = if ($env:PROCESSOR_ARCHITECTURE -eq 'ARM64') { 'arm64' } else { 'x64' }
$version = ([xml](Get-Content (Join-Path $root 'Directory.Build.props'))).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
$staging = Join-Path ([IO.Path]::GetTempPath()) ('atril-build-' + [Guid]::NewGuid().ToString('N'))

Write-Host "Building Atril $version for Windows $arch..." -ForegroundColor Cyan
& dotnet publish (Join-Path $root 'windows\Atril.Windows.csproj') -c Release -r "win-$arch" --self-contained true -p:PublishSingleFile=false -o $staging
if ($LASTEXITCODE) { throw 'The build failed (see the messages above).' }
Get-ChildItem $staging -Filter *.pdb -Recurse | Remove-Item -Force

# ---------- Install (replaces the previous version) ----------
Stop-Atril
if (Test-Path $InstallDir) { Remove-Item $InstallDir -Recurse -Force }
New-Item -ItemType Directory -Force -Path (Split-Path $InstallDir -Parent) | Out-Null
Copy-Item $staging $InstallDir -Recurse
Remove-Item $staging -Recurse -Force
Copy-Item $PSCommandPath (Join-Path $InstallDir 'install.ps1')   # used by "Uninstall" in Settings → Apps

# Shortcuts
$shell = New-Object -ComObject WScript.Shell
$links = @($startMenuLink); if ($DesktopShortcut) { $links += $desktopLink }
foreach ($path in $links) {
  $s = $shell.CreateShortcut($path)
  $s.TargetPath = $exe; $s.WorkingDirectory = $InstallDir; $s.IconLocation = "$exe,0"; $s.Description = 'Atril - EPUB reader'
  $s.Save()
}

# "Open with" for .epub files (doesn't change your default app; choose it in "Open with → Choose another app")
$cls = 'HKCU:\Software\Classes'
New-Item -Force -Path "$cls\$progId\DefaultIcon" | Out-Null
New-Item -Force -Path "$cls\$progId\shell\open\command" | Out-Null
Set-Item -Path "$cls\$progId" -Value 'EPUB book'
Set-Item -Path "$cls\$progId\DefaultIcon" -Value "`"$exe`",0"
Set-Item -Path "$cls\$progId\shell\open\command" -Value "`"$exe`" `"%1`""
New-Item -Force -Path "$cls\.epub\OpenWithProgids" | Out-Null
New-ItemProperty -Force -Path "$cls\.epub\OpenWithProgids" -Name $progId -Value ([byte[]]@()) -PropertyType Binary | Out-Null
New-Item -Force -Path "$cls\Applications\Atril.exe\shell\open\command" | Out-Null
Set-Item -Path "$cls\Applications\Atril.exe\shell\open\command" -Value "`"$exe`" `"%1`""
New-Item -Force -Path "$cls\Applications\Atril.exe\SupportedTypes" | Out-Null
New-ItemProperty -Force -Path "$cls\Applications\Atril.exe\SupportedTypes" -Name '.epub' -Value '' | Out-Null

# Settings → Apps entry
New-Item -Force -Path $uninstallKey | Out-Null
$size = [int]((Get-ChildItem $InstallDir -Recurse | Measure-Object Length -Sum).Sum / 1KB)
$values = @{
  DisplayName = 'Atril'; DisplayVersion = $version; Publisher = 'Jaime De Leon'; DisplayIcon = "$exe,0"
  InstallLocation = $InstallDir; URLInfoAbout = 'https://github.com/jdeleonferreira/personal-epub-reader'
  UninstallString = "powershell.exe -NoProfile -ExecutionPolicy Bypass -File `"$InstallDir\install.ps1`" -Uninstall -InstallDir `"$InstallDir`""
}
foreach ($k in $values.Keys) { New-ItemProperty -Force -Path $uninstallKey -Name $k -Value $values[$k] | Out-Null }
New-ItemProperty -Force -Path $uninstallKey -Name EstimatedSize -Value $size -PropertyType DWord | Out-Null
New-ItemProperty -Force -Path $uninstallKey -Name NoModify -Value 1 -PropertyType DWord | Out-Null
New-ItemProperty -Force -Path $uninstallKey -Name NoRepair -Value 1 -PropertyType DWord | Out-Null

Write-Host ''
Write-Host "Atril $version installed in $InstallDir" -ForegroundColor Green
Write-Host 'Open it from the Start menu. To update it later: git pull, then run this script again.'
