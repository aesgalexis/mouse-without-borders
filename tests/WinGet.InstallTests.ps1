# Destructive integration checks ONLY for a disposable GitHub-hosted Windows runner.
# Loopback URLs and locally calculated hashes are test fixtures, never submission manifests.
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$InstallerPath,
    [Parameter(Mandatory = $true)][ValidatePattern('^\d+\.\d+\.\d+$')][string]$Version
)
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
Set-StrictMode -Version Latest
if ($env:GITHUB_ACTIONS -ne 'true' -or $env:RUNNER_ENVIRONMENT -ne 'github-hosted' -or $env:RUNNER_OS -ne 'Windows') {
    throw 'WinGet installation tests require a disposable GitHub-hosted Windows runner; never run on a personal PC.'
}
$productCode = '{BAA10551-2B65-4E83-B102-1F184AE6C673}_is1'
$registrationPath = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\$productCode"
$testDir = Join-Path $env:LOCALAPPDATA 'Programs\MouseWithOutBorders-WinGetCI'
$app = Join-Path $testDir 'MouseWithOutBorders.exe'
if ((Test-Path -LiteralPath $registrationPath) -or (Test-Path -LiteralPath $testDir) -or
    (Get-Process -Name MouseWithOutBorders -ErrorAction SilentlyContinue)) {
    throw 'WinGet tests require no existing MWB installation, test directory or running instance.'
}
if ([Version]$Version -le [Version]'0.1.17') { throw 'The upgrade test requires a version newer than 0.1.17.' }
$InstallerPath = (Resolve-Path -LiteralPath $InstallerPath).Path
$work = Join-Path $PSScriptRoot '..\dist\winget-install-tests'
New-Item -ItemType Directory -Path $work -Force | Out-Null
$work = (Resolve-Path -LiteralPath $work).Path
$settingsPath = Join-Path $env:LOCALAPPDATA 'MouseWithOutBorders\settings.json'
$originalSettings = if (Test-Path -LiteralPath $settingsPath) { [IO.File]::ReadAllBytes($settingsPath) } else { $null }
$settings = @{ Role = 'Controller'; Host = ''; PairingKey = 'ABCDEFGH'; RemoteKey = 'JKLMNPQR'
    ExitEdge = 'Left'; AutoConnect = $false; StartWithWindows = $false; MinimizeToTray = $true
    ShareClipboard = $false; KeyRemappingEnabled = $false; KeyMappings = @() }
$server = $null
$localManifestsEnabled = $false

function Invoke-WinGet([string]$Case, [string[]]$Arguments) {
    $output = @(& $script:winget @Arguments --disable-interactivity --verbose-logs 2>&1)
    $code = $LASTEXITCODE
    $output | Out-File -LiteralPath (Join-Path $work "$Case.log") -Encoding utf8
    $output | ForEach-Object { Write-Host $_ }
    if ($code -ne 0) { throw "WinGet $Case failed with exit code $code. See dist/winget-install-tests/$Case.log." }
    return ($output -join "`n")
}
function New-TestManifest([string]$PackageVersion, [string]$Url, [string]$Hash) {
    $path = Join-Path $work "fixture-$PackageVersion.yaml"
    @"
# CI fixture only: loopback URL; never submit this file to winget-pkgs.
# yaml-language-server: `$schema=https://aka.ms/winget-manifest.singleton.1.28.0.schema.json
PackageIdentifier: aesgalexis.MouseWithOutBorders
PackageVersion: $PackageVersion
PackageLocale: en-US
Publisher: Mouse, with out borders
PackageName: Mouse, with out borders
License: MIT
ShortDescription: CI fixture for the MWB installer.
InstallerType: inno
Scope: user
InstallerSwitches:
  Silent: /VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP-
  SilentWithProgress: /SILENT /SUPPRESSMSGBOXES /NORESTART /SP-
UpgradeBehavior: install
ProductCode: '$productCode'
AppsAndFeaturesEntries:
- DisplayName: Mouse, with out borders $PackageVersion
  DisplayVersion: $PackageVersion
  Publisher: Mouse, with out borders
  ProductCode: '$productCode'
Installers:
- Architecture: x64
  InstallerUrl: $Url
  InstallerSha256: $Hash
ManifestType: singleton
ManifestVersion: 1.28.0
"@ | Set-Content -LiteralPath $path -Encoding utf8
    return $path
}
function Assert-Installed([string]$ExpectedVersion, [switch]$CheckLicense) {
    $entry = Get-ItemProperty -LiteralPath $registrationPath
    if ($entry.DisplayName -cne "Mouse, with out borders $ExpectedVersion" -or
        $entry.DisplayVersion -cne $ExpectedVersion -or $entry.Publisher -cne 'Mouse, with out borders' -or
        $entry.InstallLocation.TrimEnd('\') -ine $testDir.TrimEnd('\') -or
        ([Version](Get-Item -LiteralPath $app).VersionInfo.FileVersion).ToString(3) -cne $ExpectedVersion) {
        throw 'Installed version, per-user ARP registration or installation path is incorrect.'
    }
    if (Get-Process -Name MouseWithOutBorders -ErrorAction SilentlyContinue) { throw 'Silent WinGet installation unexpectedly launched MWB.' }
    if ($CheckLicense) {
        if ((Get-FileHash -LiteralPath (Join-Path $testDir 'LICENSE')).Hash -cne
            (Get-FileHash -LiteralPath (Join-Path $PSScriptRoot '..\LICENSE')).Hash) { throw 'Installed MIT license differs from the repository license.' }
    }
    $actualSettings = Get-Content -LiteralPath $settingsPath -Raw | ConvertFrom-Json
    foreach ($key in @('Role', 'Host', 'PairingKey', 'RemoteKey', 'ExitEdge', 'AutoConnect', 'StartWithWindows', 'MinimizeToTray', 'ShareClipboard', 'KeyRemappingEnabled')) {
        if ($actualSettings.$key -cne $settings[$key]) { throw "WinGet installation changed saved setting: $key" }
    }
    # Local --manifest installs have no source catalog ID. Check the exact ARP name;
    # upgrade/uninstall below correlate the exact stable ProductCode in the manifest.
    [void](Invoke-WinGet "list-$ExpectedVersion" @('list', '--name', $entry.DisplayName, '--exact', '--scope', 'user', '--accept-source-agreements'))
}
function Uninstall-TestPackage([string]$Manifest, [string]$Case) {
    [void](Invoke-WinGet $Case @('uninstall', '--manifest', $Manifest, '--silent', '--scope', 'user', '--accept-source-agreements'))
    if ((Test-Path -LiteralPath $registrationPath) -or (Test-Path -LiteralPath $app) -or
        (Get-Process -Name MouseWithOutBorders -ErrorAction SilentlyContinue)) { throw 'WinGet uninstall left MWB registered, installed or running.' }
    if (-not (Test-Path -LiteralPath $settingsPath)) { throw 'WinGet uninstall removed personal settings.' }
}
function Start-TestApp([bool]$Tray) {
    $process = Start-Process -FilePath $app -WorkingDirectory $testDir -WindowStyle Hidden -PassThru
    for ($attempt = 0; $attempt -lt 100; $attempt++) {
        Start-Sleep -Milliseconds 100
        $process.Refresh()
        if ($process.HasExited) { throw 'The installed test application exited during startup.' }
        # Windows can honor Hidden before WinForms creates the form. Reveal only
        # this test process's own window so the visible/tray cases are deterministic.
        $window = [MwbWinGetNative]::FindWindow($null, 'Mouse, with out borders')
        [uint32]$windowProcess = 0
        if ($window -ne [IntPtr]::Zero) {
            [void][MwbWinGetNative]::GetWindowThreadProcessId($window, [ref]$windowProcess)
            if ($windowProcess -eq $process.Id) { [void][MwbWinGetNative]::ShowWindow($window, 5) }
        }
        $process.Refresh()
        if ($process.MainWindowHandle -ne 0) { break }
    }
    if ($process.MainWindowHandle -eq 0) { throw 'The installed test application did not create its main window.' }
    if ($Tray) {
        if (-not [MwbWinGetNative]::PostMessage($process.MainWindowHandle, 0x0112, [IntPtr]0xF060, [IntPtr]::Zero)) { throw 'Cannot hide the test app in its tray.' }
        for ($attempt = 0; $attempt -lt 50; $attempt++) {
            Start-Sleep -Milliseconds 100
            $process.Refresh()
            if ($process.MainWindowHandle -eq 0) { break }
        }
        if ($process.HasExited -or $process.MainWindowHandle -ne 0) { throw 'The test application did not remain running in its tray.' }
    } else { [void][MwbWinGetNative]::ShowWindow($process.MainWindowHandle, 5) }
    return $process
}

try {
    # Official bootstrap, pinned for repeatable CI. No bootstrap or settings changes run outside the guard above.
    Install-Module -Name Microsoft.WinGet.Client -RequiredVersion 1.29.380 -Scope CurrentUser -Repository PSGallery -Force -AllowClobber
    Import-Module Microsoft.WinGet.Client -RequiredVersion 1.29.380
    Repair-WinGetPackageManager -Version 'v1.29.380' -AllUsers -Force
    $script:winget = (Get-Command winget.exe -ErrorAction Stop).Source
    [void](Invoke-WinGet 'client-version' @('--version'))
    [void](Invoke-WinGet 'enable-local-manifests' @('settings', '--enable', 'LocalManifestFiles'))
    $localManifestsEnabled = $true

    $legacy = Join-Path $work 'MouseWithOutBorders-0.1.17-Setup.exe'
    Invoke-WebRequest -Uri 'https://github.com/aesgalexis/mouse-without-borders/releases/download/v0.1.17/MouseWithOutBorders-0.1.17-Setup.exe' -OutFile $legacy -TimeoutSec 120
    $legacyHash = '753e04db3f757ce41047ac500e71fc3fbf7b1eb156603c4a747276ccf0ab28a8'
    if ((Get-FileHash -LiteralPath $legacy -Algorithm SHA256).Hash -ine $legacyHash) { throw 'Published 0.1.17 installer fixture checksum mismatch.' }
    $currentHash = (Get-FileHash -LiteralPath $InstallerPath -Algorithm SHA256).Hash.ToLowerInvariant()
    $probe = New-Object System.Net.Sockets.TcpListener([System.Net.IPAddress]::Loopback, 0)
    $probe.Start()
    $port = $probe.LocalEndpoint.Port
    $probe.Stop()
    $files = @{ '/current.exe' = $InstallerPath; '/legacy.exe' = $legacy }
    $server = Start-Job -ArgumentList $port, $files -ScriptBlock {
        param($Port, $Files)
        $listener = New-Object System.Net.HttpListener
        $listener.Prefixes.Add("http://127.0.0.1:$Port/")
        $listener.Start()
        try {
            while ($listener.IsListening) {
                $context = $listener.GetContext()
                try {
                    $file = $Files[$context.Request.Url.AbsolutePath]
                    if ($file -and $context.Request.HttpMethod -in @('GET', 'HEAD')) {
                        $context.Response.ContentLength64 = (Get-Item -LiteralPath $file).Length
                        $context.Response.ContentType = 'application/octet-stream'
                        if ($context.Request.HttpMethod -eq 'GET') {
                            $stream = [IO.File]::OpenRead($file)
                            try { $stream.CopyTo($context.Response.OutputStream) } finally { $stream.Dispose() }
                        }
                    } else { $context.Response.StatusCode = 404 }
                } finally { $context.Response.Close() }
            }
        } finally { $listener.Close() }
    }
    $baseUrl = "http://127.0.0.1:$port"
    $ready = $false
    for ($attempt = 0; $attempt -lt 50; $attempt++) {
        try { [void](Invoke-WebRequest -Uri "$baseUrl/current.exe" -Method Head -TimeoutSec 2); $ready = $true; break }
        catch { Start-Sleep -Milliseconds 100 }
    }
    if (-not $ready) { Receive-Job $server; throw 'Loopback fixture server failed to start.' }
    $currentManifest = New-TestManifest $Version "$baseUrl/current.exe" $currentHash
    $legacyManifest = New-TestManifest '0.1.17' "$baseUrl/legacy.exe" $legacyHash
    [void](Invoke-WinGet 'validate-current' @('validate', '--manifest', $currentManifest))
    $identity = Invoke-WinGet 'show-current' @('show', '--manifest', $currentManifest)
    if (-not $identity.Contains('aesgalexis.MouseWithOutBorders')) { throw 'WinGet did not read the exact manifest package identifier.' }
    New-Item -ItemType Directory -Path (Split-Path $settingsPath) -Force | Out-Null
    $settings | ConvertTo-Json | Set-Content -LiteralPath $settingsPath -Encoding utf8
    Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class MwbWinGetNative {
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr window, int command);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern IntPtr FindWindow(string className, string title);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
}
'@
    [void](Invoke-WinGet 'fresh-install' @('install', '--manifest', $currentManifest, '--silent', '--scope', 'user', '--location', $testDir, '--accept-package-agreements', '--accept-source-agreements'))
    Assert-Installed $Version -CheckLicense
    Uninstall-TestPackage $currentManifest 'fresh-uninstall'
    Write-Output 'PASS: WinGet silent installation, exact manifest identity, ARP detection, MIT license and uninstall.'
    foreach ($mode in @('window', 'tray')) {
        [void](Invoke-WinGet "legacy-install-$mode" @('install', '--manifest', $legacyManifest, '--silent', '--scope', 'user', '--location', $testDir, '--accept-package-agreements', '--accept-source-agreements'))
        Assert-Installed '0.1.17'
        $oldApp = Start-TestApp ($mode -eq 'tray')
        [void](Invoke-WinGet "upgrade-$mode" @('upgrade', '--manifest', $currentManifest, '--silent', '--scope', 'user', '--accept-package-agreements', '--accept-source-agreements'))
        if (-not $oldApp.WaitForExit(5000)) { throw "WinGet upgrade did not close the old $mode instance." }
        Assert-Installed $Version -CheckLicense
        Uninstall-TestPackage $currentManifest "uninstall-$mode"
        Write-Output "PASS: WinGet upgrades published 0.1.17 while running ($mode), preserves settings and uninstalls."
    }
} finally {
    if ($server) { Stop-Job $server; Remove-Job $server -Force }
    Get-Process -Name MouseWithOutBorders -ErrorAction SilentlyContinue |
        Where-Object { $_.Path -ieq $app } | Stop-Process -Force
    if ($localManifestsEnabled) { & $script:winget settings --disable LocalManifestFiles --disable-interactivity | Out-Null }
    if ($null -ne $originalSettings) { [IO.File]::WriteAllBytes($settingsPath, $originalSettings) }
    elseif (Test-Path -LiteralPath $settingsPath) { Remove-Item -LiteralPath $settingsPath -Force }
    # Keep fixture manifests and command logs as CI diagnostics. The runner is discarded after the job.
}
