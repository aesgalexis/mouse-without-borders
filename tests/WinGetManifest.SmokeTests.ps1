# Offline generator tests: all HTTP commands are replaced with isolated fixtures.
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$generator = Join-Path $PSScriptRoot '..\scripts\New-WinGetManifest.ps1'
$testRoot = Join-Path ([IO.Path]::GetTempPath()) ('MWB-WinGet-Tests-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $testRoot | Out-Null
$script:payload = [Text.Encoding]::UTF8.GetBytes('Offline fixture; never an executable.')
$sha = [Security.Cryptography.SHA256]::Create()
try { $script:hash = ([BitConverter]::ToString($sha.ComputeHash($script:payload))).Replace('-', '').ToLowerInvariant() }
finally { $sha.Dispose() }
$script:assetName = 'MouseWithOutBorders-0.1.17-Setup.exe'
$script:baseUrl = 'https://github.com/aesgalexis/mouse-without-borders/releases/download/v0.1.17/'

$mockRestMethod = {
    param($Uri, $Headers, $TimeoutSec)
    if ($Uri -cne 'https://api.github.com/repos/aesgalexis/mouse-without-borders/releases/tags/v0.1.17') {
        throw 'Unexpected API request in offline test.'
    }
    return $fixture.Release
}
$mockWebRequest = {
    param([switch]$UseBasicParsing, $Uri, $Headers, $OutFile, $TimeoutSec)
    if ($Uri -ceq ($fixture.BaseUrl + $fixture.AssetName)) {
        [IO.File]::WriteAllBytes($OutFile, $fixture.Payload)
    } elseif ($Uri -ceq ($fixture.BaseUrl + 'SHA256SUMS.txt')) {
        [IO.File]::WriteAllBytes($OutFile, $fixture.ChecksumBytes)
    } else { throw 'Unexpected download in offline test.' }
}
$fixture = @{ Release = $null; ChecksumBytes = $null; Payload = $script:payload
    BaseUrl = $script:baseUrl; AssetName = $script:assetName }
Set-Item -Path function:Invoke-RestMethod -Value $mockRestMethod.GetNewClosure()
Set-Item -Path function:Invoke-WebRequest -Value $mockWebRequest.GetNewClosure()
function Reset-Fixture {
    $script:checksumBytes = [Text.Encoding]::UTF8.GetBytes("$script:hash *$script:assetName`n")
    $script:release = [pscustomobject]@{
        tag_name = 'v0.1.17'; draft = $false; prerelease = $true
        assets = @(
            [pscustomobject]@{ name = $script:assetName; state = 'uploaded'; size = $script:payload.Length
                browser_download_url = $script:baseUrl + $script:assetName; digest = "sha256:$script:hash" },
            [pscustomobject]@{ name = 'SHA256SUMS.txt'; state = 'uploaded'; size = $script:checksumBytes.Length
                browser_download_url = $script:baseUrl + 'SHA256SUMS.txt' }
        )
    }
    $fixture.Release = $script:release
    $fixture.ChecksumBytes = $script:checksumBytes
}
function Assert-Rejected([string]$Case, [string]$Message) {
    $outputRoot = Join-Path $testRoot $Case
    $failure = $null
    try { & $generator -Version '0.1.17' -OutputRoot $outputRoot | Out-Null }
    catch { $failure = $_.Exception.Message }
    if (-not $failure -or $failure -notlike "*$Message*") { throw "Expected rejection for $Case, got: $failure" }
    if (Test-Path -LiteralPath $outputRoot) { throw "Rejected $Case created output files." }
    Write-Output "PASS: $Case rejected before generating YAML."
}
try {
    Reset-Fixture
    $outputRoot = Join-Path $testRoot 'valid'
    & $generator -Version '0.1.17' -OutputRoot $outputRoot | Out-Null
    $folder = Join-Path $outputRoot 'a\aesgalexis\MouseWithOutBorders\0.1.17'
    $files = @(Get-ChildItem -LiteralPath $folder -Filter '*.yaml')
    if ($files.Count -ne 3) { throw 'Expected exactly three multi-file manifests.' }
    $installer = Get-Content -LiteralPath (Join-Path $folder 'aesgalexis.MouseWithOutBorders.installer.yaml') -Raw
    $locale = Get-Content -LiteralPath (Join-Path $folder 'aesgalexis.MouseWithOutBorders.locale.en-US.yaml') -Raw
    if (-not $installer.Contains("InstallerSha256: $script:hash") -or
        -not $installer.Contains("InstallerUrl: $script:baseUrl$script:assetName") -or
        -not $installer.Contains('DisplayName: Mouse, with out borders 0.1.17') -or
        -not $installer.Contains('Scope: user') -or
        -not $locale.Contains('License: MIT') -or
        -not $locale.Contains('LicenseUrl: https://github.com/aesgalexis/mouse-without-borders/blob/main/LICENSE')) {
        throw 'Generated metadata does not match the verified release and license.'
    }
    Write-Output 'PASS: verified fixture generates three manifests with the published hash, user scope, ARP metadata and MIT license.'

    Reset-Fixture
    $script:checksumBytes = [Text.Encoding]::UTF8.GetBytes(('0' * 64) + " *$script:assetName`n")
    $fixture.ChecksumBytes = $script:checksumBytes
    Assert-Rejected 'wrong-checksum' 'SHA-256'
    Reset-Fixture
    $script:release.assets[0].digest = 'sha256:' + ('0' * 64)
    Assert-Rejected 'wrong-github-digest' 'digest mismatch'
    Reset-Fixture
    $script:release.draft = $true
    Assert-Rejected 'draft-release' 'published release'
    Reset-Fixture
    $script:release.assets[0].browser_download_url = 'https://example.com/untrusted.exe'
    Assert-Rejected 'unofficial-asset-url' 'official asset'
    Write-Output 'All offline WinGet manifest smoke tests passed. No network access or installers executed.'
} finally {
    # Delete only the explicitly created test directory after checking its full path.
    $resolvedRoot = [IO.Path]::GetFullPath($testRoot)
    $expectedParent = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
    if ($resolvedRoot.StartsWith($expectedParent, [StringComparison]::OrdinalIgnoreCase) -and
        [IO.Path]::GetFileName($resolvedRoot) -like 'MWB-WinGet-Tests-*') {
        Remove-Item -LiteralPath $resolvedRoot -Recurse -Force
    }
}
