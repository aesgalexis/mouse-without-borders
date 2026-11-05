# Generate reviewable manifests from a published release. Never runs Setup or submits a PR.
[CmdletBinding()]
param(
    [ValidatePattern('^\d+\.\d+\.\d+$')][string]$Version,
    [string]$OutputRoot
)
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
Set-StrictMode -Version Latest
if (-not $OutputRoot) { $OutputRoot = Join-Path $PSScriptRoot '..\dist\winget\manifests' }
if (-not $Version) {
    [xml]$project = Get-Content -LiteralPath (Join-Path $PSScriptRoot '..\MouseWithOutBorders\MouseWithOutBorders.csproj')
    $Version = [string]$project.Project.PropertyGroup.Version
}
if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw 'Version must contain three numeric components.' }

$repository = 'aesgalexis/mouse-without-borders'
$packageId = 'aesgalexis.MouseWithOutBorders'
$packageUrl = "https://github.com/$repository"
$tag = "v$Version"
$assetName = "MouseWithOutBorders-$Version-Setup.exe"
$downloadBase = "$packageUrl/releases/download/$tag/"
$headers = @{ 'User-Agent' = 'MouseWithOutBorders-WinGet'; 'Accept' = 'application/vnd.github+json' }
$release = Invoke-RestMethod -Uri "https://api.github.com/repos/$repository/releases/tags/$tag" -Headers $headers -TimeoutSec 60
if ($release.draft -or $release.tag_name -cne $tag) { throw 'Expected a published release with the requested version tag.' }

$selected = @()
foreach ($name in @($assetName, 'SHA256SUMS.txt')) {
    $matchingAssets = @($release.assets | Where-Object { $_.name -ceq $name })
    if ($matchingAssets.Count -ne 1 -or $matchingAssets[0].state -cne 'uploaded' -or
        $matchingAssets[0].browser_download_url -cne ($downloadBase + $name)) {
        throw "Expected one uploaded, version-specific official asset: $name"
    }
    $selected += $matchingAssets[0]
}

$temporary = Join-Path ([IO.Path]::GetTempPath()) ('MWB-WinGet-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $temporary | Out-Null
try {
    foreach ($asset in $selected) {
        $path = Join-Path $temporary $asset.name
        Invoke-WebRequest -UseBasicParsing -Uri $asset.browser_download_url -Headers $headers -OutFile $path -TimeoutSec 60
        if ((Get-Item -LiteralPath $path).Length -ne $asset.size) { throw "Release asset size mismatch: $($asset.name)" }
        $hash = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
        $digest = $asset.PSObject.Properties['digest']
        if ($null -ne $digest -and $digest.Value -and $digest.Value -cne "sha256:$hash") {
            throw "GitHub asset digest mismatch: $($asset.name)"
        }
    }
    $installerHash = (Get-FileHash -LiteralPath (Join-Path $temporary $assetName) -Algorithm SHA256).Hash.ToLowerInvariant()
    $pattern = '^([0-9a-fA-F]{64})\s+\*?' + [regex]::Escape($assetName) + '$'
    $checksums = @(Get-Content -LiteralPath (Join-Path $temporary 'SHA256SUMS.txt') | Where-Object { $_ -match $pattern })
    if ($checksums.Count -ne 1 -or ($checksums[0] -split '\s+')[0] -ine $installerHash) {
        throw 'Installer SHA-256 does not match the published SHA256SUMS.txt.'
    }

    $versionYaml = @"
# yaml-language-server: `$schema=https://aka.ms/winget-manifest.version.1.28.0.schema.json
PackageIdentifier: $packageId
PackageVersion: $Version
DefaultLocale: en-US
ManifestType: version
ManifestVersion: 1.28.0
"@
    $localeYaml = @"
# yaml-language-server: `$schema=https://aka.ms/winget-manifest.defaultLocale.1.28.0.schema.json
PackageIdentifier: $packageId
PackageVersion: $Version
PackageLocale: en-US
Publisher: Mouse, with out borders
PublisherUrl: $packageUrl
PublisherSupportUrl: $packageUrl/issues
PackageName: Mouse, with out borders
PackageUrl: $packageUrl
License: MIT
LicenseUrl: $packageUrl/blob/main/LICENSE
ShortDescription: Share a mouse and an optional clipboard between two Windows PCs on a private LAN.
Description: |-
  Windows x64 preview for two PCs arranged to the left and right. Shares mouse
  input and an optional text/file clipboard, with local key remapping.
  Requires active, unlocked sessions and a trusted private network. Mouse traffic
  is authenticated but unencrypted; clipboard payloads are encrypted.
Tags:
- mouse
- clipboard
- lan
- key-remapping
ReleaseNotesUrl: $packageUrl/releases/tag/$tag
InstallationNotes: Pair both PCs on a trusted private LAN. Never expose TCP port 42187 to the internet.
ManifestType: defaultLocale
ManifestVersion: 1.28.0
"@
    $installerYaml = @"
# yaml-language-server: `$schema=https://aka.ms/winget-manifest.installer.1.28.0.schema.json
PackageIdentifier: $packageId
PackageVersion: $Version
InstallerType: inno
Scope: user
InstallModes:
- interactive
- silent
- silentWithProgress
InstallerSwitches:
  Silent: /VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP-
  SilentWithProgress: /SILENT /SUPPRESSMSGBOXES /NORESTART /SP-
UpgradeBehavior: install
ProductCode: '{BAA10551-2B65-4E83-B102-1F184AE6C673}_is1'
AppsAndFeaturesEntries:
- DisplayName: Mouse, with out borders $Version
  DisplayVersion: $Version
  Publisher: Mouse, with out borders
  ProductCode: '{BAA10551-2B65-4E83-B102-1F184AE6C673}_is1'
Installers:
- Architecture: x64
  InstallerUrl: $downloadBase$assetName
  InstallerSha256: $installerHash
ManifestType: installer
ManifestVersion: 1.28.0
"@
    $destination = [IO.Path]::GetFullPath((Join-Path $OutputRoot "a\aesgalexis\MouseWithOutBorders\$Version"))
    New-Item -ItemType Directory -Path $destination -Force | Out-Null
    $encoding = New-Object System.Text.UTF8Encoding($false)
    [IO.File]::WriteAllText((Join-Path $destination "$packageId.yaml"), $versionYaml + "`n", $encoding)
    [IO.File]::WriteAllText((Join-Path $destination "$packageId.locale.en-US.yaml"), $localeYaml + "`n", $encoding)
    [IO.File]::WriteAllText((Join-Path $destination "$packageId.installer.yaml"), $installerYaml + "`n", $encoding)
    Write-Output "Verified published installer SHA-256: $installerHash"
    Write-Output "Manifests generated for review: $destination"
} finally {
    # Only these two known files can be created here; no recursive deletion.
    foreach ($name in @($assetName, 'SHA256SUMS.txt')) {
        Remove-Item -LiteralPath (Join-Path $temporary $name) -Force -ErrorAction SilentlyContinue
    }
    Remove-Item -LiteralPath $temporary -Force -ErrorAction SilentlyContinue
}
