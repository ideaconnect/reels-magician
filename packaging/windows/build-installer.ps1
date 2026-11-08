# Builds the Windows installer from a self-contained win-x64 publish of the app, with Velopack:
#
#   pwsh packaging/windows/build-installer.ps1 out/ReelsMagician 0.3.0 out/installer
#
# It writes ReelsMagician-win-x64-Setup.exe, plus the full .nupkg and releases.win.json that
# updates need, into the output folder. Velopack's own portable zip is left out: the release keeps
# its plain zip. vpk comes from .config/dotnet-tools.json, at the Velopack package's version.
#
# The pack id decides where Setup.exe installs, %LOCALAPPDATA%\<pack id>, and uninstalling deletes
# that whole folder. So it must never be ReelsMagician, the data folder's name, which holds the
# tools, models and settings: IDCT.ReelsMagician installs beside the data folder instead.
param(
    [Parameter(Mandatory)] [string] $PublishDir,
    [Parameter(Mandatory)] [string] $Version,
    [Parameter(Mandatory)] [string] $OutputDir
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true

$PackId = 'IDCT.ReelsMagician'
$SetupName = 'ReelsMagician-win-x64-Setup.exe'

# vpk refuses versions below 0.0.1, and a build not made from a release tag is 0.0.0-dev
# (Directory.Build.props). Its installer is 0.0.1-dev; the app inside still calls itself a
# development build.
$packVersion = if ($Version -eq '0.0.0-dev') { '0.0.1-dev' } else { $Version }

$root = (Resolve-Path (Join-Path $PSScriptRoot '..' '..')).Path
$PublishDir = (Resolve-Path $PublishDir).Path
if (-not (Test-Path (Join-Path $PublishDir 'ReelsMagician.exe'))) {
    throw "$PublishDir is not a win-x64 publish of the app: it has no ReelsMagician.exe"
}
New-Item -ItemType Directory -Force $OutputDir | Out-Null
$OutputDir = (Resolve-Path $OutputDir).Path

# The publisher Apps & features shows is the one the .exe already names.
$authors = ([xml](Get-Content (Join-Path $root 'Directory.Build.props') -Raw)).Project.PropertyGroup.Authors |
    Where-Object { $_ } | Select-Object -First 1
if (-not $authors) { throw 'Directory.Build.props names no Authors' }
$icon = Join-Path $root 'src' 'ReelsMagician.App' 'Assets' 'reelsmagician.ico'

Push-Location $root
try {
    dotnet tool restore
    # On Windows vpk builds a Windows package by itself; elsewhere it needs the [win] directive.
    [string[]] $directive = if ($IsWindows) { @() } else { @('[win]') }
    dotnet vpk @directive pack `
        --runtime win-x64 `
        --packId $PackId `
        --packVersion $packVersion `
        --packDir $PublishDir `
        --mainExe ReelsMagician.exe `
        --packTitle ReelsMagician `
        --packAuthors $authors `
        --icon $icon `
        --shortcuts 'StartMenuRoot,Desktop' `
        --noPortable `
        --outputDir $OutputDir `
        --skip-updates --legacyConsole
} finally {
    Pop-Location
}

$setup = Join-Path $OutputDir "$PackId-win-Setup.exe"
if (-not (Test-Path $setup)) { throw "vpk made no $PackId-win-Setup.exe in $OutputDir" }
Move-Item -Force $setup (Join-Path $OutputDir $SetupName)

foreach ($file in $SetupName, "$PackId-$packVersion-full.nupkg", 'releases.win.json') {
    if (-not (Test-Path (Join-Path $OutputDir $file))) { throw "The installer build lacks $file" }
}
Get-ChildItem $OutputDir | ForEach-Object { '{0,12:N0}  {1}' -f $_.Length, $_.Name }
