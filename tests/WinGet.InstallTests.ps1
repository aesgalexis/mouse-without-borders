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
$originalWinGetSettings = $null
$winGetSettingsChanged = $false
$testStarted = Get-Date
$smartScreenSubkey = 'Software\Policies\Microsoft\Windows\System'
$smartScreenValueName = 'EnableSmartScreen'
$smartScreenChanged = $false
$smartScreenExisted = $false
$originalSmartScreenKind = $null
$originalSmartScreenValue = $null

function Save-NativeDiagnostics([string]$Case) {
    try {
        Get-CimInstance Win32_Process | Where-Object { $_.Name -match '(?i)winget|current|setup|smartscreen|consent|MouseWithOutBorders|\.tmp$' } |
            Select-Object Name, ProcessId, ParentProcessId, ExecutablePath, CommandLine |
            Format-List | Out-File -LiteralPath (Join-Path $work "$Case-processes.log") -Encoding utf8
        Get-Process -ErrorAction SilentlyContinue | Where-Object { $_.ProcessName -match '(?i)winget|current|setup|smartscreen|consent|MouseWithOutBorders|\.tmp$' } |
            Select-Object ProcessName, Id, Path, MainWindowHandle, MainWindowTitle |
            Format-List | Out-File -LiteralPath (Join-Path $work "$Case-windows.log") -Encoding utf8
        foreach ($directory in @(
            (Join-Path $env:LOCALAPPDATA 'Packages\Microsoft.DesktopAppInstaller_8wekyb3d8bbwe\LocalState\DiagOutputDir'),
            (Join-Path $env:LOCALAPPDATA 'Microsoft\WinGet\Logs')
        )) {
            if (Test-Path -LiteralPath $directory) {
                Get-ChildItem -LiteralPath $directory -Filter '*.log' -File |
                    Where-Object { $_.LastWriteTime -ge $testStarted } | Select-Object -First 50 |
                    ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $work ("native-" + $_.Name)) -Force }
            }
        }
        Get-ChildItem -LiteralPath $env:TEMP -Filter 'Setup Log*.txt' -File |
            Where-Object { $_.LastWriteTime -ge $testStarted } | Select-Object -First 30 |
            ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $work ("inno-" + $_.BaseName + '.log')) -Force }
    } catch { Write-Warning "Cannot collect native diagnostics: $($_.Exception.Message)" }
}

function Invoke-WinGet([string]$Case, [string[]]$Arguments, [string]$VerifiedInstallerPath = '', [string]$VerifiedInstallerHash = '') {
    $log = Join-Path $work "$Case.log"
    $errorLog = Join-Path $work "$Case-stderr.log"
    if ($Arguments[0] -in @('install', 'upgrade')) { $Arguments += @('--log', (Join-Path $work "$Case-setup.log")) }
    Write-Host "Starting WinGet case: $Case"
    $process = New-Object Diagnostics.Process
    $process.StartInfo.FileName = $script:winget
    $process.StartInfo.UseShellExecute = $false
    $process.StartInfo.CreateNoWindow = $true
    $process.StartInfo.RedirectStandardOutput = $true
    $process.StartInfo.RedirectStandardError = $true
    foreach ($argument in ($Arguments + @('--disable-interactivity', '--verbose-logs'))) { $process.StartInfo.ArgumentList.Add($argument) }
    $started = $false
    $launchConfirmed = $false
    $pendingLaunchWindow = [IntPtr]::Zero
    $launchPostedAt = 0.0
    $sinks = @()
    $readers = @()
    try {
        # Buffer size 1 keeps diagnostic bytes visible even if the process hangs.
        $sinks = @([IO.FileStream]::new($log, 'Create', 'Write', 'ReadWrite', 1, $true),
            [IO.FileStream]::new($errorLog, 'Create', 'Write', 'ReadWrite', 1, $true))
        $started = $process.Start()
        $copies = @($process.StandardOutput.BaseStream.CopyToAsync($sinks[0]), $process.StandardError.BaseStream.CopyToAsync($sinks[1]))
        foreach ($path in @($log, $errorLog)) {
            $readers += [IO.StreamReader]::new([IO.File]::Open($path, 'Open', 'Read', 'ReadWrite'), [Text.Encoding]::UTF8)
        }
        $timer = [Diagnostics.Stopwatch]::StartNew()
        while (-not $process.WaitForExit(100)) {
            foreach ($reader in $readers) { while ($reader.Peek() -ge 0) { Write-Host $reader.ReadLine() } }
            if ($pendingLaunchWindow -ne [IntPtr]::Zero) {
                if (-not [MwbWinGetNative]::IsWindow($pendingLaunchWindow)) {
                    $launchConfirmed = $true
                    $pendingLaunchWindow = [IntPtr]::Zero
                    Write-Host "Windows closed the launch warning for the verified CI fixture: $([IO.Path]::GetFileName($VerifiedInstallerPath))"
                } elseif ($timer.Elapsed.TotalSeconds - $launchPostedAt -ge 5) {
                    Save-NativeDiagnostics "$Case-launch-warning"
                    throw 'The verified Windows launch dialog did not acknowledge its Run command.'
                }
            }
            if (-not $launchConfirmed -and $pendingLaunchWindow -eq [IntPtr]::Zero -and $VerifiedInstallerPath -and $Arguments[0] -in @('install', 'upgrade')) {
                $process.Refresh()
                $warningWindow = $process.MainWindowHandle
                $button = [MwbWinGetNative]::FindFixtureRunButton($warningWindow, [uint32]$process.Id, [IO.Path]::GetFileName($VerifiedInstallerPath))
                if ($button -ne [IntPtr]::Zero) {
                    $downloadRoot = [IO.Path]::GetFullPath((Join-Path $env:TEMP 'WinGet')).TrimEnd('\') + '\'
                    if (-not [IO.Path]::GetFullPath($VerifiedInstallerPath).StartsWith($downloadRoot, [StringComparison]::OrdinalIgnoreCase) -or
                        $VerifiedInstallerHash -notmatch '^[0-9a-fA-F]{64}$' -or
                        (Get-FileHash -LiteralPath $VerifiedInstallerPath -Algorithm SHA256).Hash -ine $VerifiedInstallerHash) {
                        throw 'Refusing the CI launch confirmation: the expected WinGet installer path/hash does not match.'
                    }
                    $buttonId = [MwbWinGetNative]::GetDlgCtrlID($button)
                    # Notify the dialog directly: BM_CLICK can be ignored when
                    # the hosted runner's desktop does not activate the dialog.
                    if ($buttonId -le 0 -or $buttonId -gt 65535 -or
                        -not [MwbWinGetNative]::PostMessage($warningWindow, 0x0111, [IntPtr]$buttonId, $button)) {
                        throw 'Cannot confirm the verified CI fixture launch.'
                    }
                    $pendingLaunchWindow = $warningWindow
                    $launchPostedAt = $timer.Elapsed.TotalSeconds
                    Write-Host "Requested Run for the verified CI fixture; waiting for the launch warning to close: $([IO.Path]::GetFileName($VerifiedInstallerPath))"
                }
            }
            if ($timer.Elapsed.TotalSeconds -ge 150) {
                Save-NativeDiagnostics "$Case-timeout"
                $process.Kill($true)
                [void]$process.WaitForExit(5000)
                throw "WinGet $Case exceeded 150 seconds; process, window, native WinGet and Setup diagnostics were captured before termination."
            }
        }
        foreach ($copy in $copies) { if (-not $copy.Wait(5000)) { throw "WinGet $Case output stream did not close." } }
        foreach ($reader in $readers) { while ($reader.Peek() -ge 0) { Write-Host $reader.ReadLine() } }
        $code = $process.ExitCode
    } finally {
        if ($started -and -not $process.HasExited) {
            Save-NativeDiagnostics "$Case-aborted"
            $process.Kill($true)
            [void]$process.WaitForExit(5000)
        }
        foreach ($reader in $readers) { $reader.Dispose() }
        foreach ($sink in $sinks) { $sink.Dispose() }
        $process.Dispose()
    }
    $output = (Get-Content -LiteralPath $log -Raw -Encoding utf8) + (Get-Content -LiteralPath $errorLog -Raw -Encoding utf8)
    if ($code -ne 0) {
        if ($server) {
            try {
                $server | Format-List State, HasMoreData | Out-File -LiteralPath (Join-Path $work 'server.log') -Encoding utf8
                $server.ChildJobs | ForEach-Object { $_.JobStateInfo.Reason } | Out-File -LiteralPath (Join-Path $work 'server.log') -Append -Encoding utf8
                Receive-Job $server -Keep -ErrorAction Continue 2>&1 | Out-File -LiteralPath (Join-Path $work 'server.log') -Append -Encoding utf8
            } catch { Write-Warning "Cannot collect server diagnostics: $($_.Exception.Message)" }
        }
        throw "WinGet $Case failed with exit code $code. See dist/winget-install-tests/$Case.log."
    }
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
    Install-Module -Name Microsoft.WinGet.Client -RequiredVersion 1.29.380 -Scope CurrentUser -Repository PSGallery -Force -AllowClobber
    Import-Module Microsoft.WinGet.Client -RequiredVersion 1.29.380
    # Prefer the runner's working client. Repair only the CI user's registration;
    # -AllUsers can select an incomplete provisioned bundle on hosted Server images.
    $wingetCandidate = Get-Command winget.exe -ErrorAction SilentlyContinue
    $usableClient = $false
    if ($wingetCandidate) {
        try {
            $clientVersion = (& $wingetCandidate.Source --version 2>$null | Out-String).Trim()
            $usableClient = $LASTEXITCODE -eq 0 -and $clientVersion -match '^v?(\d+\.\d+\.\d+)$' -and
                [Version]$Matches[1] -ge [Version]'1.29.380'
        } catch { $usableClient = $false }
    }
    if (-not $usableClient) {
        Repair-WinGetPackageManager -Version 'v1.29.380' -Force
    }
    $script:winget = (Get-Command winget.exe -ErrorAction Stop).Source
    [void](Invoke-WinGet 'client-version' @('--version'))
    $originalWinGetSettings = Get-WinGetUserSetting
    $winGetSettingsChanged = $true
    # Delivery Optimization is unsuitable for this simple CI loopback fixture.
    # Use the documented WinINet downloader only in the disposable CI account.
    Set-WinGetUserSetting -UserSettings @{ network = @{ downloader = 'wininet' } } -Merge | Out-Null
    Write-Host 'CI fixture downloads use WinINet; original WinGet user settings will be restored.'
    [void](Invoke-WinGet 'enable-local-manifests' @('settings', '--enable', 'LocalManifestFiles'))
    $localManifestsEnabled = $true

    $legacy = Join-Path $work 'MouseWithOutBorders-0.1.17-Setup.exe'
    $legacyUrl = 'https://github.com/aesgalexis/mouse-without-borders/releases/download/v0.1.17/MouseWithOutBorders-0.1.17-Setup.exe'
    Invoke-WebRequest -Uri $legacyUrl -OutFile $legacy -TimeoutSec 120
    $legacyHash = '753e04db3f757ce41047ac500e71fc3fbf7b1eb156603c4a747276ccf0ab28a8'
    if ((Get-FileHash -LiteralPath $legacy -Algorithm SHA256).Hash -ine $legacyHash) { throw 'Published 0.1.17 installer fixture checksum mismatch.' }
    $currentHash = (Get-FileHash -LiteralPath $InstallerPath -Algorithm SHA256).Hash.ToLowerInvariant()
    # Local manifests lack the public catalog's trust. After verifying fixture
    # hashes, suppress only the shell reputation prompt in this disposable runner.
    # Policy: https://learn.microsoft.com/windows/client-management/mdm/policy-csp-admx-windowsexplorer#enablesmartscreen
    $policyKey = [Microsoft.Win32.Registry]::LocalMachine.CreateSubKey($smartScreenSubkey)
    try {
        $smartScreenExisted = $policyKey.GetValueNames() -contains $smartScreenValueName
        if ($smartScreenExisted) {
            $originalSmartScreenKind = $policyKey.GetValueKind($smartScreenValueName)
            $originalSmartScreenValue = $policyKey.GetValue($smartScreenValueName, $null, [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)
        }
        $smartScreenChanged = $true
        $policyKey.SetValue($smartScreenValueName, 0, [Microsoft.Win32.RegistryValueKind]::DWord)
    } finally { $policyKey.Dispose() }
    Write-Host 'CI-only SmartScreen shell reputation policy temporarily disabled for verified local fixtures; original value and type will be restored.'
    $probe = New-Object System.Net.Sockets.TcpListener([System.Net.IPAddress]::Loopback, 0)
    $probe.Start()
    $port = $probe.LocalEndpoint.Port
    $probe.Stop()
    $files = @{ '/current.exe' = $InstallerPath }
    $server = Start-Job -ArgumentList $port, $files -ScriptBlock {
        param($Port, $Files)
        $listener = New-Object System.Net.HttpListener
        $listener.Prefixes.Add("http://127.0.0.1:$Port/")
        $listener.Start()
        try {
            while ($listener.IsListening) {
                # Keep PowerShell responsive to Stop-Job while no client is connected.
                $pendingContext = $listener.GetContextAsync()
                while (-not $pendingContext.IsCompleted) { Start-Sleep -Milliseconds 100 }
                $context = $pendingContext.GetAwaiter().GetResult()
                try {
                    Write-Output "$($context.Request.HttpMethod) $($context.Request.RawUrl)"
                    $file = $Files[$context.Request.Url.AbsolutePath]
                    if ($file -and $context.Request.HttpMethod -in @('GET', 'HEAD')) {
                        $context.Response.ContentLength64 = (Get-Item -LiteralPath $file).Length
                        $context.Response.ContentType = 'application/octet-stream'
                        if ($context.Request.HttpMethod -eq 'GET') {
                            $stream = [IO.File]::OpenRead($file)
                            try { $stream.CopyTo($context.Response.OutputStream) } finally { $stream.Dispose() }
                        }
                    } else { $context.Response.StatusCode = 404 }
                } catch [System.IO.IOException], [System.Net.HttpListenerException] {
                    # Delivery Optimization can cancel a probe/download. Keep
                    # serving so its WinINet fallback can open a new connection.
                    Write-Output "Client disconnected: $($_.Exception.Message)"
                } finally {
                    try { $context.Response.Close() }
                    catch [System.IO.IOException], [System.Net.HttpListenerException] { Write-Output 'Response closed after client disconnected.' }
                }
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
    $legacyManifest = New-TestManifest '0.1.17' $legacyUrl $legacyHash
    $currentDownloaded = Join-Path $env:TEMP "WinGet\aesgalexis.MouseWithOutBorders.$Version\current.exe"
    $legacyDownloaded = Join-Path $env:TEMP 'WinGet\aesgalexis.MouseWithOutBorders.0.1.17\MouseWithOutBorders-0.1.17-Setup.exe'
    [void](Invoke-WinGet 'validate-current' @('validate', '--manifest', $currentManifest))
    $identity = Invoke-WinGet 'show-current' @('show', '--manifest', $currentManifest)
    if (-not $identity.Contains('aesgalexis.MouseWithOutBorders')) { throw 'WinGet did not read the exact manifest package identifier.' }
    New-Item -ItemType Directory -Path (Split-Path $settingsPath) -Force | Out-Null
    $settings | ConvertTo-Json | Set-Content -LiteralPath $settingsPath -Encoding utf8
    Add-Type @'
using System;
using System.Runtime.InteropServices;
using System.Text;
public static class MwbWinGetNative {
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr window, int command);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern IntPtr FindWindow(string className, string title);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("user32.dll")] public static extern int GetDlgCtrlID(IntPtr window);
    [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr window);
    private delegate bool EnumChildCallback(IntPtr window, IntPtr parameter);
    [DllImport("user32.dll")] private static extern bool EnumChildWindows(IntPtr parent, EnumChildCallback callback, IntPtr parameter);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] private static extern int GetWindowText(IntPtr window, StringBuilder text, int count);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] private static extern int GetClassName(IntPtr window, StringBuilder text, int count);
    private static string TextOf(IntPtr window) {
        var text = new StringBuilder(32768);
        GetWindowText(window, text, text.Capacity);
        return text.ToString();
    }
    private static string ClassOf(IntPtr window) {
        var text = new StringBuilder(256);
        GetClassName(window, text, text.Capacity);
        return text.ToString();
    }
    public static IntPtr FindFixtureRunButton(IntPtr window, uint processId, string fileName) {
        if (window == IntPtr.Zero || ClassOf(window) != "#32770" || TextOf(window) != "Open File - Security Warning") return IntPtr.Zero;
        uint owner;
        GetWindowThreadProcessId(window, out owner);
        if (owner != processId) return IntPtr.Zero;
        bool expectedFileShown = false;
        IntPtr run = IntPtr.Zero;
        EnumChildWindows(window, (child, parameter) => {
            string text = TextOf(child);
            if (text.IndexOf(fileName, StringComparison.OrdinalIgnoreCase) >= 0) expectedFileShown = true;
            if (ClassOf(child) == "Button" && text.Replace("&", "") == "Run") run = child;
            return true;
        }, IntPtr.Zero);
        return expectedFileShown ? run : IntPtr.Zero;
    }
}
'@
    [void](Invoke-WinGet 'fresh-install' @('install', '--manifest', $currentManifest, '--silent', '--scope', 'user', '--location', $testDir, '--accept-package-agreements', '--accept-source-agreements') $currentDownloaded $currentHash)
    Assert-Installed $Version -CheckLicense
    Uninstall-TestPackage $currentManifest 'fresh-uninstall'
    Write-Output 'PASS: WinGet silent installation, exact manifest identity, ARP detection, MIT license and uninstall.'
    foreach ($mode in @('window', 'tray')) {
        [void](Invoke-WinGet "legacy-install-$mode" @('install', '--manifest', $legacyManifest, '--silent', '--scope', 'user', '--location', $testDir, '--accept-package-agreements', '--accept-source-agreements') $legacyDownloaded $legacyHash)
        Assert-Installed '0.1.17'
        $oldApp = Start-TestApp ($mode -eq 'tray')
        [void](Invoke-WinGet "upgrade-$mode" @('upgrade', '--manifest', $currentManifest, '--silent', '--scope', 'user', '--accept-package-agreements', '--accept-source-agreements') $currentDownloaded $currentHash)
        if (-not $oldApp.WaitForExit(5000)) { throw "WinGet upgrade did not close the old $mode instance." }
        Assert-Installed $Version -CheckLicense
        Uninstall-TestPackage $currentManifest "uninstall-$mode"
        Write-Output "PASS: WinGet upgrades published 0.1.17 while running ($mode), preserves settings and uninstalls."
    }
} finally {
    if ($smartScreenChanged) {
        try {
            $policyKey = [Microsoft.Win32.Registry]::LocalMachine.CreateSubKey($smartScreenSubkey)
            try {
                if ($smartScreenExisted) { $policyKey.SetValue($smartScreenValueName, $originalSmartScreenValue, $originalSmartScreenKind) }
                else { $policyKey.DeleteValue($smartScreenValueName, $false) }
            } finally { $policyKey.Dispose() }
            Write-Host 'Restored the original CI SmartScreen policy value and type (or its original absence).'
        } catch { Write-Warning "Cannot restore CI SmartScreen policy: $($_.Exception.Message)" }
    }
    Save-NativeDiagnostics 'final'
    if ($server) {
        try {
            $server | Format-List State, HasMoreData | Out-File -LiteralPath (Join-Path $work 'server-final.log') -Encoding utf8
            $server.ChildJobs | ForEach-Object { $_.JobStateInfo.Reason } | Out-File -LiteralPath (Join-Path $work 'server-final.log') -Append -Encoding utf8
            Stop-Job $server
            Receive-Job $server -Keep -ErrorAction Continue 2>&1 | Out-File -LiteralPath (Join-Path $work 'server-final.log') -Append -Encoding utf8
        } catch { Write-Warning "Cannot collect final server diagnostics: $($_.Exception.Message)" }
        finally { Stop-Job $server -ErrorAction SilentlyContinue; Remove-Job $server -Force -ErrorAction SilentlyContinue }
    }
    Get-Process -Name MouseWithOutBorders -ErrorAction SilentlyContinue |
        Where-Object { $_.Path -ieq $app } | Stop-Process -Force
    if ($localManifestsEnabled) { & $script:winget settings --disable LocalManifestFiles --disable-interactivity | Out-Null }
    if ($winGetSettingsChanged) {
        try { Set-WinGetUserSetting -UserSettings $originalWinGetSettings | Out-Null }
        catch { Write-Warning "Cannot restore CI WinGet user settings: $($_.Exception.Message)" }
    }
    if ($null -ne $originalSettings) { [IO.File]::WriteAllBytes($settingsPath, $originalSettings) }
    elseif (Test-Path -LiteralPath $settingsPath) { Remove-Item -LiteralPath $settingsPath -Force }
    # Keep fixture manifests and command logs as CI diagnostics. The runner is discarded after the job.
}
