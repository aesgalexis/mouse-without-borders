# Installs a tagged release on the CURRENT Windows PC.
# Run from a PowerShell window: .\scripts\install.ps1
# This script downloads a release from the owner's GitHub repository, verifies
# the release's published SHA-256 manifest, and runs Inno Setup locally.
[CmdletBinding()]
param(
    [string]$Version = '0.1.11',
    [switch]$Receiver,
    [switch]$NoLaunch
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if ($PSVersionTable.PSEdition -eq 'Core' -and -not [System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform([System.Runtime.InteropServices.OSPlatform]::Windows)) {
    throw 'Run this installer on Windows 10 or 11.'
}
if (-not [Environment]::Is64BitOperatingSystem) {
    throw 'This release requires 64-bit Windows.'
}
if ([Environment]::OSVersion.Version.Major -lt 10) {
    throw 'Windows 10 or 11 is required.'
}
if ($Version -notmatch '^\d+\.\d+\.\d+$') {
    throw 'Version must contain three numeric components.'
}

$repo = 'aesgalexis/mouse-without-borders'
$releaseUrl = "https://api.github.com/repos/$repo/releases/tags/v$Version"
$headers = @{
    'User-Agent' = 'MouseWithOutBorders-Installer'
    'Accept' = 'application/vnd.github+json'
}
Write-Host "Checking release v$Version at $repo..."
$release = Invoke-RestMethod -Uri $releaseUrl -Headers $headers
$assetName = "MouseWithOutBorders-$Version-Setup.exe"
$setupAsset = @($release.assets | Where-Object { $_.name -eq $assetName }) | Select-Object -First 1
$hashAsset = @($release.assets | Where-Object { $_.name -eq 'SHA256SUMS.txt' }) | Select-Object -First 1
if (-not $setupAsset) { throw "Installer $assetName was not found in release v$Version." }
if (-not $hashAsset) { throw "Release v$Version does not contain SHA256SUMS.txt. Refusing an unverified download." }

$folder = Join-Path ([IO.Path]::GetTempPath()) ('MouseWithOutBorders-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $folder | Out-Null
try {
    $setupPath = Join-Path $folder $assetName
    $sumPath = Join-Path $folder 'SHA256SUMS.txt'
    Write-Host 'Downloading release files...'
    Invoke-WebRequest -UseBasicParsing -Uri $setupAsset.browser_download_url -Headers $headers -OutFile $setupPath
    Invoke-WebRequest -UseBasicParsing -Uri $hashAsset.browser_download_url -Headers $headers -OutFile $sumPath
    $manifest = Get-Content -LiteralPath $sumPath
    $match = $manifest | Where-Object { $_ -match ('^[0-9a-fA-F]{64}\s+\*?' + [regex]::Escape($assetName) + '$') } | Select-Object -First 1
    if (-not $match) { throw "Installer not listed in release SHA-256 manifest." }
    $expected = ($match -split '\s+')[0].ToUpperInvariant()
    $actual = (Get-FileHash -LiteralPath $setupPath -Algorithm SHA256).Hash.ToUpperInvariant()
    if ($actual -ne $expected) { throw 'SHA-256 mismatch. Installation cancelled.' }

    Write-Host 'SHA-256 verified. Installing for current Windows user...'
    $install = Start-Process -FilePath $setupPath -ArgumentList '/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP-' -Wait -PassThru
    if ($install.ExitCode -ne 0) { throw "Installer exited with code $($install.ExitCode)." }

    $exe = Join-Path $env:LOCALAPPDATA 'Programs\MouseWithOutBorders\MouseWithOutBorders.exe'
    if (-not (Test-Path -LiteralPath $exe)) {
        throw "Setup finished but $exe was not found. Check the chosen install directory."
    }

    if ($Receiver) {
        Write-Host 'A private-network firewall rule requires Windows administrator approval (UAC).'
        $rule = 'MouseWithOutBorders Private LAN Receiver'
        $existing = Get-NetFirewallRule -DisplayName $rule -ErrorAction SilentlyContinue
        if (-not $existing) {
            $netshArgs = @(
                'advfirewall', 'firewall', 'add', 'rule',
                'name="MouseWithOutBorders Private LAN Receiver"', 'dir=in', 'action=allow', 'profile=private',
                'protocol=TCP', 'localport=42187', "program=$exe"
            )
            try {
                $firewall = Start-Process -FilePath 'netsh.exe' -ArgumentList $netshArgs -Verb RunAs -Wait -PassThru
                if ($firewall.ExitCode -ne 0) {
                    Write-Warning 'The firewall rule failed; the receiving PC may not be reachable.'
                }
            } catch {
                Write-Warning "Firewall permission declined or failed: $($_.Exception.Message)"
            }
        } else {
            Write-Host 'Private LAN firewall rule already exists.'
        }
    }

    if (-not $NoLaunch) {
        Start-Process -FilePath $exe
    }
    Write-Host ''
    Write-Host "Installed: $exe"
    Write-Host "Choose 'Allow connection' on one PC and 'Connect to other PC' on the other."
    Write-Host 'Private LAN only. Never forward port 42187 to the internet.'
} finally {
    Remove-Item -LiteralPath $folder -Recurse -Force -ErrorAction SilentlyContinue
}
