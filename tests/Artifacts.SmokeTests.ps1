# Exercise lifecycle failures without compiling, downloading or installing software.
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$fixtureRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../.cache'))
$fixture = Join-Path $fixtureRoot ('artifact-tests-' + [Guid]::NewGuid().ToString('N'))
$global:MwbArtifactTestFailPublish = $false
$global:MwbArtifactTestFailCompiler = $false
$global:MwbArtifactTestVersion = '0.1.19'
function Assert([bool]$Condition, [string]$Message) { if (-not $Condition) { throw $Message } }
function dotnet {
    if ($global:MwbArtifactTestFailPublish) { $global:LASTEXITCODE = 17; return }
    $output = $args[[Array]::IndexOf($args, '-o') + 1]
    $global:MwbArtifactTestVersion = ($args | Where-Object { $_ -like '-p:Version=*' }).Substring(11)
    Set-Content -LiteralPath (Join-Path $output 'MouseWithOutBorders.exe') -Value $global:MwbArtifactTestVersion
    foreach ($name in @('bin', 'obj')) {
        New-Item -ItemType Directory -Path "MouseWithOutBorders/$name" -Force | Out-Null
        Set-Content "MouseWithOutBorders/$name/generated.txt" 'runtime copy'
    }
    $global:LASTEXITCODE = 0
}
function Get-Item {
    param([string]$LiteralPath, [switch]$Force)
    if ([IO.Path]::GetFileName($LiteralPath) -eq 'MouseWithOutBorders.exe') {
        return [pscustomobject]@{ VersionInfo = [pscustomobject]@{ FileVersion = "$global:MwbArtifactTestVersion.0" } }
    }
    Microsoft.PowerShell.Management\Get-Item -LiteralPath $LiteralPath -Force:$Force
}
try {
    New-Item -ItemType Directory -Path "$fixture/scripts", "$fixture/installer", "$fixture/MouseWithOutBorders" -Force | Out-Null
    Copy-Item "$PSScriptRoot/../scripts/build.ps1", "$PSScriptRoot/../scripts/Artifacts.ps1" "$fixture/scripts"
    Set-Content "$fixture/LICENSE" 'fixture license'
    Set-Content "$fixture/installer/MouseWithOutBorders.iss" 'fixture installer'
    Set-Content "$fixture/MouseWithOutBorders/MouseWithOutBorders.csproj" '<Project><PropertyGroup><Version>0.1.19</Version></PropertyGroup></Project>'
    @'
if ($global:MwbArtifactTestFailCompiler) { $global:LASTEXITCODE = 19; return }
$output = ($args | Where-Object { $_ -like '/O*' }).Substring(2)
$version = ($args | Where-Object { $_ -like '/DAppVersion=*' }).Substring(13)
Set-Content -LiteralPath (Join-Path $output "MouseWithOutBorders-$version-Setup.exe") -Value $version
$global:LASTEXITCODE = 0
'@ | Set-Content "$fixture/compiler.ps1"
    $build = Join-Path $fixture 'scripts/build.ps1'
    $compiler = Join-Path $fixture 'compiler.ps1'
    $current = Join-Path $fixture 'dist/current'
    & $build -InnoCompiler $compiler
    Set-Content "$current/portable/obsolete.zip" 'old package'
    & $build -Version '0.1.20' -InnoCompiler $compiler
    Assert (@(Get-ChildItem $current -Recurse -File).Count -eq 4) 'Repeated builds accumulated files.'
    Assert (@(Get-Content "$current/SHA256SUMS.txt").Count -eq 2) 'Checksums accumulated.'
    Assert (Test-Path "$current/installer/MouseWithOutBorders-0.1.20-Setup.exe") 'New version is missing.'
    Assert (-not (Test-Path "$fixture/MouseWithOutBorders/bin")) 'Duplicate runtime output remains.'
    $expectedHash = (Get-FileHash "$current/SHA256SUMS.txt").Hash
    foreach ($failure in @('Publish', 'Compiler')) {
        Set-Variable -Scope Global -Name "MwbArtifactTestFail$failure" -Value $true
        $caught = $false
        try { & $build -InnoCompiler $compiler } catch { $caught = $true }
        Set-Variable -Scope Global -Name "MwbArtifactTestFail$failure" -Value $false
        Assert $caught "Expected $failure to fail."
        Assert ((Get-FileHash "$current/SHA256SUMS.txt").Hash -eq $expectedHash) "$failure replaced the valid build."
        Assert (-not (Test-Path "$fixture/dist/.staging")) "$failure left staging behind."
    }
    # Simulate interruption between current -> previous and staging -> current.
    Move-Item -LiteralPath $current -Destination "$fixture/dist/.previous"
    $global:MwbArtifactTestFailPublish = $true
    try { & $build -InnoCompiler $compiler } catch { }
    $global:MwbArtifactTestFailPublish = $false
    Assert ((Get-FileHash "$current/SHA256SUMS.txt").Hash -eq $expectedHash) 'Interrupted replacement was not recovered.'
    $held = [IO.File]::Open("$fixture/dist/.build.lock", 'Open', 'ReadWrite', 'None')
    try {
        $caught = $false
        try { & $build -InnoCompiler $compiler } catch { $caught = $true }
        Assert $caught 'Concurrent build was not rejected.'
    } finally { $held.Dispose() }
    . "$fixture/scripts/Artifacts.ps1"
    $caught = $false
    try { Remove-MwbGeneratedDirectory $fixture "$fixture/dist" } catch { $caught = $true }
    Assert $caught 'Cleanup allowed a path outside dist.'
    Write-Output 'PASS: repeated builds replace packages; publish/compiler failure, interruption and concurrent builds preserve current; cleanup stays inside its root.'
} finally {
    # This fixture was created above, inside the ignored local cache.
    . "$PSScriptRoot/../scripts/Artifacts.ps1"
    Remove-MwbGeneratedDirectory $fixture $fixtureRoot
    Remove-Variable MwbArtifactTestFailPublish, MwbArtifactTestFailCompiler, MwbArtifactTestVersion -Scope Global
}
