# Run in a disposable Windows account, as in GitHub Actions.
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$InstallerPath,
    [Parameter(Mandatory = $true)][string]$LegacyPortablePath
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$InstallerPath = (Resolve-Path -LiteralPath $InstallerPath).Path
$LegacyPortablePath = (Resolve-Path -LiteralPath $LegacyPortablePath).Path
$testDir = Join-Path $env:LOCALAPPDATA 'Programs\MouseWithOutBorders-InstallerCI'
$app = Join-Path $testDir 'MouseWithOutBorders.exe'
$uninstallRegistryPath = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\{BAA10551-2B65-4E83-B102-1F184AE6C673}_is1'
$desktopDir = [Environment]::GetFolderPath('DesktopDirectory')
$shortcutPath = Join-Path $desktopDir 'MWB Installer CI.lnk'
$defaultShortcutPath = Join-Path $desktopDir 'Mouse, with out borders.lnk'
$startShortcutPath = Join-Path ([Environment]::GetFolderPath('Programs')) 'Mouse, with out borders.lnk'
$taskbarLinks = Join-Path ([Environment]::GetFolderPath('ApplicationData')) 'Microsoft\Internet Explorer\Quick Launch\User Pinned\TaskBar'
$taskbarShortcutPath = Join-Path $taskbarLinks 'MWB Installer CI.lnk'
$logDir = Join-Path $PSScriptRoot '..\dist\installer-tests'

# Do not overwrite an existing user's app registration or settings during tests.
$registration = Get-ItemProperty -LiteralPath $uninstallRegistryPath -ErrorAction SilentlyContinue
if ($registration -and $registration.InstallLocation.TrimEnd('\') -ine $testDir.TrimEnd('\')) {
    throw 'Use a disposable Windows account: MWB is already installed elsewhere.'
}
if (Get-Process -Name MouseWithOutBorders -ErrorAction SilentlyContinue) {
    throw 'Use a disposable Windows account: MWB is already running.'
}
if ((Test-Path -LiteralPath $shortcutPath) -or (Test-Path -LiteralPath $defaultShortcutPath) -or
    (Test-Path -LiteralPath $startShortcutPath) -or (Test-Path -LiteralPath $taskbarShortcutPath)) {
    throw 'Use a disposable Windows account: an MWB shortcut already exists.'
}
. (Join-Path $PSScriptRoot '..\scripts\Artifacts.ps1')
Remove-MwbGeneratedDirectory $logDir (Join-Path $PSScriptRoot '..\dist')
New-Item -ItemType Directory -Path $logDir -Force | Out-Null
$logDir = (Resolve-Path -LiteralPath $logDir).Path
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class MwbInstallerSmokeNative {
    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
}
'@

function Invoke-Setup([string]$CaseName, [switch]$AppUpdate, [switch]$DesktopShortcut) {
    Write-Host "Starting installer case: $CaseName"
    $log = Join-Path $logDir ($CaseName + '.log')
    $setupArgs = @(
        '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/SP-',
        ('/DIR="' + $testDir + '"'), ('/LOG="' + $log + '"')
    )
    if ($AppUpdate) { $setupArgs += '/MWBUPDATE=1' }
    if ($DesktopShortcut) { $setupArgs += '/TASKS=desktopicon' }
    $setup = Start-Process -FilePath $InstallerPath -ArgumentList $setupArgs -WindowStyle Hidden -PassThru
    if (-not $setup.WaitForExit(45000)) {
        $setup.Kill()
        throw "Setup stalled during $CaseName."
    }
    if ($setup.ExitCode -ne 0) { throw "Setup exited $($setup.ExitCode) during $CaseName. See $log" }
    if (-not (Test-Path -LiteralPath $app)) { throw 'Installed EXE not found.' }
    return $log
}

function Assert-DefaultShortcut([bool]$Expected) {
    if ((Test-Path -LiteralPath $defaultShortcutPath) -ne $Expected) {
        throw "Unexpected desktop shortcut state: expected present=$Expected."
    }
}

function Assert-PackageRegistration([Version]$InstalledVersion) {
    if (-not (Test-Path -LiteralPath $uninstallRegistryPath)) {
        throw 'WinGet package detection failed: the stable per-user uninstall registry key is missing.'
    }
    $key = Get-Item -LiteralPath $uninstallRegistryPath
    try {
        $expectedVersion = $InstalledVersion.ToString(3)
        $expectedValues = @{
            DisplayName = 'Mouse, with out borders ' + $expectedVersion
            Publisher = 'Mouse, with out borders'
            DisplayVersion = $expectedVersion
        }
        foreach ($name in $expectedValues.Keys) {
            $actual = [string]$key.GetValue($name)
            if ($actual -cne $expectedValues[$name]) {
                throw "WinGet package detection failed: $name is '$actual', expected '$($expectedValues[$name])'."
            }
        }
        $location = [string]$key.GetValue('InstallLocation')
        if ($location.TrimEnd('\') -ine $testDir.TrimEnd('\')) {
            throw "WinGet package detection failed: InstallLocation is '$location', expected '$testDir'."
        }
        $quietUninstall = [string]$key.GetValue('QuietUninstallString')
        $uninstallerPath = Join-Path $testDir 'unins000.exe'
        if ($quietUninstall -notmatch ('^(?i)"' + [regex]::Escape($uninstallerPath) + '"\s+') -or
            $quietUninstall -notmatch '(?i)(?:^|\s)/(?:VERYSILENT|SILENT)(?:\s|$)') {
            throw 'WinGet silent uninstall failed: QuietUninstallString must reference this installation and contain a silent switch.'
        }
    } finally {
        $key.Dispose()
    }
    $licensePath = Join-Path $testDir 'LICENSE'
    if (-not (Test-Path -LiteralPath $licensePath -PathType Leaf) -or
        (Get-Item -LiteralPath $licensePath).Length -eq 0) {
        throw 'The installed package must include a nonempty LICENSE file.'
    }
    if (Get-Process -Name MouseWithOutBorders -ErrorAction SilentlyContinue) {
        throw 'A silent installation must not start MWB. Only the explicit in-app update may reopen it.'
    }
}

function Assert-ShortcutIcon([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path)) { throw "Shortcut was removed: $Path" }
    $link = $shell.CreateShortcut($Path)
    if ($link.TargetPath -ine $app -or
        $link.IconLocation.Replace('"', '') -ine ($expectedIconPath + ',0') -or
        -not (Test-Path -LiteralPath $expectedIconPath)) {
        throw "Setup did not preserve the shortcut and refresh its icon: $Path"
    }
}

function Start-TestApp([switch]$Tray) {
    $process = Start-Process -FilePath $app -WorkingDirectory $testDir -PassThru
    for ($attempt = 0; $attempt -lt 80; $attempt++) {
        Start-Sleep -Milliseconds 100
        $process.Refresh()
        if ($process.HasExited) { throw 'The test app exited before setup.' }
        if ($process.MainWindowHandle -ne 0) { break }
    }
    if ($process.MainWindowHandle -eq 0) { throw 'The test app did not open its window.' }
    if ($Tray) {
        # Reproduce the window's Close command. CloseMainWindow sends WM_CLOSE,
        # which WinForms can interpret as a task-manager exit instead of a click.
        if (-not [MwbInstallerSmokeNative]::PostMessage($process.MainWindowHandle, 0x0112, [IntPtr]0xF060, [IntPtr]::Zero)) {
            throw 'Could not close the test app to the tray.'
        }
        for ($attempt = 0; $attempt -lt 50; $attempt++) {
            Start-Sleep -Milliseconds 100
            $process.Refresh()
            if ($process.HasExited) { throw 'Closing the test window quit the app instead of hiding it.' }
            if ($process.MainWindowHandle -eq 0) { break }
        }
        if ($process.MainWindowHandle -ne 0) { throw 'The test app did not hide in the tray.' }
        Start-Sleep -Milliseconds 250
        if ($process.HasExited) { throw 'The test app exited after closing its window.' }
    }
    return $process
}

try {
    [void](Invoke-Setup 'fresh-install')
    Assert-DefaultShortcut $false
    Write-Output 'PASS: the desktop shortcut is unchecked by default on a fresh installation.'
    $installedVersion = [Version](Get-Item -LiteralPath $app).VersionInfo.FileVersion
    Assert-PackageRegistration $installedVersion
    Write-Output 'PASS: silent installation preserves the WinGet detection/uninstall contract, includes LICENSE and does not launch MWB.'
    $expectedIconPath = Join-Path $testDir ('app-' + $installedVersion.ToString(3) + '.ico')
    $shell = New-Object -ComObject WScript.Shell
    Assert-ShortcutIcon $startShortcutPath
    $shortcut = $shell.CreateShortcut($shortcutPath)
    $shortcut.TargetPath = $app
    $shortcut.IconLocation = $app + ',0'
    $shortcut.Save()
    New-Item -ItemType Directory -Path $taskbarLinks -Force | Out-Null
    $pinnedShortcut = $shell.CreateShortcut($taskbarShortcutPath)
    $pinnedShortcut.TargetPath = $app
    $pinnedShortcut.IconLocation = $app + ',0'
    $pinnedShortcut.Arguments = '--autostart'
    $pinnedShortcut.Save()

    # Persist a previous selection, then delete its shortcut as a user would.
    # An ordinary upgrade must not restore that historical selection.
    [void](Invoke-Setup 'desktop-shortcut-opt-in' -DesktopShortcut)
    Assert-DefaultShortcut $true
    Remove-Item -LiteralPath $defaultShortcutPath -Force
    [void](Invoke-Setup 'upgrade-deleted-desktop-shortcut')
    Assert-DefaultShortcut $false
    Assert-ShortcutIcon $shortcutPath
    Assert-ShortcutIcon $taskbarShortcutPath
    if ($shell.CreateShortcut($taskbarShortcutPath).Arguments -cne '--autostart') {
        throw 'Setup changed the arguments of an existing taskbar shortcut.'
    }
    Write-Output 'PASS: existing desktop and taskbar shortcuts use the same cursor icon as the Start menu shortcut.'
    Write-Output 'PASS: an upgrade does not recreate a desktop shortcut deleted after explicit opt-in.'

    [void](Invoke-Setup 'desktop-shortcut-opt-in-again' -DesktopShortcut)
    Assert-DefaultShortcut $true
    foreach ($mode in @('window', 'tray', 'legacy-tray')) {
        if ($mode -eq 'legacy-tray') {
            $legacyDir = Join-Path $logDir 'legacy'
            Expand-Archive -LiteralPath $LegacyPortablePath -DestinationPath $legacyDir -Force
            Copy-Item -LiteralPath (Join-Path $legacyDir 'MouseWithOutBorders.exe') -Destination $app -Force
        }
        $runningApp = Start-TestApp -Tray:($mode -ne 'window')
        $log = Invoke-Setup ('upgrade-' + $mode)
        Assert-ShortcutIcon $shortcutPath
        Assert-ShortcutIcon $defaultShortcutPath
        if (-not $runningApp.WaitForExit(5000)) { throw "Setup left the $mode app running." }
        $logText = Get-Content -LiteralPath $log -Raw
        if (-not $logText.Contains('Requesting MWB exit before replacing its files.')) {
            throw "Setup did not request an exit during $mode."
        }
        $forced = $logText.Contains('Closing an older or unresponsive MWB process.')
        if ($forced -ne ($mode -eq 'legacy-tray')) {
            throw "Unexpected shutdown method during $mode."
        }
        Write-Output "PASS: automatic upgrade while MWB is running ($mode)."
    }

    # Re-select immediately before deleting, so the in-app updater also has a
    # saved desktopicon selection to ignore rather than an already-empty one.
    [void](Invoke-Setup 'desktop-shortcut-before-in-app-update' -DesktopShortcut)
    Assert-DefaultShortcut $true
    Remove-Item -LiteralPath $defaultShortcutPath -Force
    $updatingApp = Start-TestApp -Tray
    [void](Invoke-Setup 'in-app-update' -AppUpdate)
    Assert-DefaultShortcut $false
    Assert-ShortcutIcon $shortcutPath
    if (-not $updatingApp.WaitForExit(5000)) { throw 'In-app update left the old app running.' }
    $restartedApp = $null
    for ($attempt = 0; $attempt -lt 80; $attempt++) {
        $restartedApp = Get-Process -Name MouseWithOutBorders -ErrorAction SilentlyContinue |
            Where-Object { $_.Path -ieq $app -and $_.Id -ne $updatingApp.Id } | Select-Object -First 1
        if ($restartedApp -and $restartedApp.MainWindowHandle -ne 0) { break }
        Start-Sleep -Milliseconds 100
    }
    if (-not $restartedApp -or $restartedApp.MainWindowHandle -eq 0) {
        throw 'In-app update did not reopen the updated application.'
    }
    if (-not [MwbInstallerSmokeNative]::PostMessage($restartedApp.MainWindowHandle, 0x0010, [IntPtr]::Zero, [IntPtr]::Zero)) {
        throw 'Could not close the restarted test application.'
    }
    if (-not $restartedApp.WaitForExit(5000)) { throw 'The restarted test application did not exit.' }
    Write-Output 'PASS: in-app update reopens MWB without recreating the deleted desktop shortcut.'

    $runningApp = Start-TestApp -Tray
    $uninstaller = Join-Path $testDir 'unins000.exe'
    $uninstall = Start-Process -FilePath $uninstaller -ArgumentList @(
        '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART'
    ) -WindowStyle Hidden -PassThru
    if (-not $uninstall.WaitForExit(45000)) {
        $uninstall.Kill()
        throw 'Uninstall stalled while MWB was running.'
    }
    if ($uninstall.ExitCode -ne 0) { throw "Uninstall exited $($uninstall.ExitCode)." }
    if (-not $runningApp.WaitForExit(5000)) { throw 'Uninstall left MWB running.' }
    if (Test-Path -LiteralPath $app) { throw 'EXE still present after uninstall.' }
    if (Test-Path -LiteralPath $uninstallRegistryPath) {
        throw 'WinGet package detection failed: the uninstall registry key remains after uninstall.'
    }
    Write-Output 'PASS: uninstall while MWB is running in the tray.'
} finally {
    Remove-Item -LiteralPath $shortcutPath -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $defaultShortcutPath -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $taskbarShortcutPath -Force -ErrorAction SilentlyContinue
    Get-Process -Name MouseWithOutBorders -ErrorAction SilentlyContinue |
        Where-Object { $_.Path -ieq $app } | Stop-Process -Force
    Remove-MwbGeneratedDirectory (Join-Path $logDir 'legacy') $logDir
}
