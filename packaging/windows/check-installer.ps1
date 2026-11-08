# Installs the Windows installer on this machine, starts the app from its Start menu shortcut,
# uninstalls it, and checks each step. CI runs it on windows-latest after build-installer.ps1:
#
#   pwsh packaging/windows/check-installer.ps1 out/installer
#
# It is meant for CI and throwaway machines: it refuses to run where the app is installed already.
# The data folder, %LOCALAPPDATA%\ReelsMagician, is the one a zip user already has. The check makes
# one if there is none and requires the installer and the uninstaller to leave every file in it as
# it was.
param([Parameter(Mandatory)] [string] $InstallerDir)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true
if (-not $IsWindows) { throw 'check-installer.ps1 installs the app, so it runs on Windows only' }

$failures = [System.Collections.Generic.List[string]]::new()
function Check([bool] $ok, [string] $what) {
    if ($ok) { Write-Host "  ok    $what" } else { Write-Host "  FAIL  $what"; $failures.Add($what) }
}

# Every file in a folder with its size and time, so a change to any of them shows.
function Snapshot([string] $dir) {
    if (-not (Test-Path $dir)) { return '' }
    (Get-ChildItem $dir -Recurse -Force -File | Sort-Object FullName |
        ForEach-Object { '{0}|{1}|{2:o}' -f $_.FullName.Substring($dir.Length), $_.Length, $_.LastWriteTimeUtc }) -join "`n"
}

function Wait-Until([scriptblock] $condition, [int] $seconds) {
    $deadline = (Get-Date).AddSeconds($seconds)
    while ((Get-Date) -lt $deadline) {
        if (& $condition) { return $true }
        Start-Sleep -Milliseconds 500
    }
    return [bool](& $condition)
}

function Show-VelopackLogs {
    Get-ChildItem (Join-Path $local 'velopack'), $env:TEMP -Filter 'velopack*.log' -File -ErrorAction SilentlyContinue |
        ForEach-Object { Write-Host "--- $($_.FullName)"; Get-Content $_.FullName -Tail 60 }
}

$InstallerDir = (Resolve-Path $InstallerDir).Path
$setup = Join-Path $InstallerDir 'ReelsMagician-win-x64-Setup.exe'
$release = (Get-Content (Join-Path $InstallerDir 'releases.win.json') -Raw | ConvertFrom-Json).Assets |
    Where-Object Type -eq 'Full' | Select-Object -First 1
$packId = $release.PackageId
$version = $release.Version
$root = (Resolve-Path (Join-Path $PSScriptRoot '..' '..')).Path
$authors = ([xml](Get-Content (Join-Path $root 'Directory.Build.props') -Raw)).Project.PropertyGroup.Authors |
    Where-Object { $_ } | Select-Object -First 1

$local = [Environment]::GetFolderPath('LocalApplicationData')
$installDir = Join-Path $local $packId
$dataDir = Join-Path $local 'ReelsMagician'
$app = Join-Path $installDir 'current' 'ReelsMagician.exe'
$updater = Join-Path $installDir 'Update.exe'
$startMenuLink = Join-Path ([Environment]::GetFolderPath('Programs')) 'ReelsMagician.lnk'
$desktopLink = Join-Path ([Environment]::GetFolderPath('DesktopDirectory')) 'ReelsMagician.lnk'
$uninstallKey = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\$packId"

Write-Host "Checking $setup ($packId $version)"
if (Test-Path $installDir) { throw "$installDir exists: the app is installed here already" }
if ($installDir -eq $dataDir) { throw "The pack id $packId would install into the data folder" }

# A machine that used the zip before: a data folder with settings, a tool and a model in it.
foreach ($file in 'settings.json', 'tools\installer-check\tool.txt', 'models\installer-check.bin') {
    $path = Join-Path $dataDir $file
    if (-not (Test-Path $path)) {
        New-Item -ItemType Directory -Force (Split-Path $path) | Out-Null
        Set-Content -Path $path -Value $(if ($file -eq 'settings.json') { '{}' } else { "kept by $file" })
    }
}
$beforeInstall = Snapshot $dataDir

Write-Host 'Installing (Setup.exe --silent)'
$run = Start-Process -FilePath $setup -ArgumentList '--silent' -Wait -PassThru
Check ($run.ExitCode -eq 0) "Setup.exe exits with 0 (got $($run.ExitCode))"
Check (Test-Path $app) "the app is installed in $installDir\current"
Check (Test-Path $updater) 'Update.exe is installed beside it'
if (Test-Path $installDir) {
    $size = (Get-ChildItem $installDir -Recurse -Force -File | Measure-Object Length -Sum).Sum
    Write-Host ('  ({0:N0} MB installed)' -f ($size / 1MB))
}
Check ((Snapshot $dataDir) -eq $beforeInstall) 'the data folder is untouched by the install'
Check (-not (Test-Path (Join-Path $dataDir 'current'))) 'nothing is installed into the data folder'

$hookLog = Get-ChildItem (Join-Path $local 'velopack') -Filter "velopack_$packId.log" -File -ErrorAction SilentlyContinue |
    Select-Object -First 1
Check ($null -ne $hookLog -and (Select-String -Path $hookLog.FullName -SimpleMatch 'Found fast exit hook: --veloapp-install' -Quiet)) `
    'the app answered the install hook (VelopackApp.Build().Run() comes first in Main)'

$shell = New-Object -ComObject WScript.Shell
foreach ($link in $startMenuLink, $desktopLink) {
    $exists = Test-Path $link
    Check $exists "a shortcut is in $(Split-Path $link)"
    if ($exists) { Check ($shell.CreateShortcut($link).TargetPath -eq $app) "  and it starts $app" }
}

$entry = Get-ItemProperty -Path $uninstallKey -ErrorAction SilentlyContinue
Check ($null -ne $entry) 'Apps & features lists the app'
if ($entry) {
    Check ($entry.DisplayName -eq 'ReelsMagician') "  named ReelsMagician (got '$($entry.DisplayName)')"
    Check ($entry.Publisher -eq $authors) "  published by $authors (got '$($entry.Publisher)')"
    Check ($entry.DisplayVersion -eq $version) "  at version $version (got '$($entry.DisplayVersion)')"
    Check ($entry.DisplayIcon -eq $app) "  with the app's own icon (got '$($entry.DisplayIcon)')"
    Check ($entry.UninstallString -like "*Update.exe*--uninstall*") '  with an uninstaller'
}

Write-Host 'Starting the app from the Start menu shortcut'
$beforeStart = @(Get-Process -Name ReelsMagician -ErrorAction SilentlyContinue).Id
Start-Process -FilePath $startMenuLink
$window = $null
$shown = Wait-Until {
    $script:window = Get-Process -Name ReelsMagician -ErrorAction SilentlyContinue |
        Where-Object { $_.Id -notin $beforeStart -and $_.MainWindowTitle -like 'ReelsMagician*' } | Select-Object -First 1
    $null -ne $script:window
} 90
Check $shown "its window opens (title '$($window.MainWindowTitle)')"
Check ($null -eq $window -or $window.Path -eq $app) "  running from $app (got '$($window.Path)')"
Get-Process -Name ReelsMagician -ErrorAction SilentlyContinue | Where-Object { $_.Id -notin $beforeStart } | ForEach-Object {
    [void]$_.CloseMainWindow()
    if (-not $_.WaitForExit(15000)) { Stop-Process -Id $_.Id -Force }
}
$beforeUninstall = Snapshot $dataDir

Write-Host 'Uninstalling (Update.exe --uninstall --silent)'
$run = Start-Process -FilePath $updater -ArgumentList '--uninstall', '--silent' -Wait -PassThru
Check ($run.ExitCode -eq 0) "Update.exe --uninstall exits with 0 (got $($run.ExitCode))"
Check (Wait-Until { -not (Test-Path $app) } 30) 'the app is removed'
Check (-not (Test-Path $startMenuLink) -and -not (Test-Path $desktopLink)) 'its shortcuts are removed'
Check (-not (Test-Path $uninstallKey)) 'Apps & features no longer lists it'
Check ((Snapshot $dataDir) -eq $beforeUninstall) 'the data folder is untouched by the uninstall'
foreach ($file in 'settings.json', 'tools\installer-check\tool.txt', 'models\installer-check.bin') {
    Check (Test-Path (Join-Path $dataDir $file)) "  $file is still there"
}
$hookLog = Get-ChildItem (Join-Path $local 'velopack') -Filter "velopack_$packId.log" -File -ErrorAction SilentlyContinue |
    Select-Object -First 1
Check ($null -ne $hookLog -and (Select-String -Path $hookLog.FullName -SimpleMatch 'Found fast exit hook: --veloapp-uninstall' -Quiet)) `
    'the app answered the uninstall hook'

# Remove the stand-in tool and model; what the app itself wrote while it ran stays.
Remove-Item -Recurse -Force (Join-Path $dataDir 'tools\installer-check'), (Join-Path $dataDir 'models\installer-check.bin') -ErrorAction SilentlyContinue

if ($failures.Count -gt 0) {
    Show-VelopackLogs
    throw "The installer check failed: $($failures -join '; ')"
}
Write-Host 'The installer check passed.'
