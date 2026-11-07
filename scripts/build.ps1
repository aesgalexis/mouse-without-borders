# Build one complete distribution; replace current only after every step succeeds.
[CmdletBinding()]
param(
    [string]$Version,
    [string]$InnoCompiler
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. "$PSScriptRoot/Artifacts.ps1"
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$dist = Join-Path $repo 'dist'
$stage = Join-Path $dist '.staging'
$current = Join-Path $dist 'current'
$previous = Join-Path $dist '.previous'
if (-not $Version) {
    [xml]$project = Get-Content (Join-Path $repo 'MouseWithOutBorders/MouseWithOutBorders.csproj')
    $Version = [string]$project.Project.PropertyGroup.Version
}
if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw 'Version must be MAJOR.MINOR.PATCH.' }
if (-not $InnoCompiler) {
    $candidates = @(
        "${env:ProgramFiles(x86)}/Inno Setup 6/ISCC.exe",
        (Join-Path $repo '.tools/inno/ISCC.exe')
    )
    $InnoCompiler = $candidates | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Select-Object -First 1
}
if (-not $InnoCompiler -or -not (Test-Path -LiteralPath $InnoCompiler -PathType Leaf)) {
    throw 'Install Inno Setup 6, or pass -InnoCompiler with the path to ISCC.exe.'
}
$InnoCompiler = (Resolve-Path -LiteralPath $InnoCompiler).Path
New-Item -ItemType Directory -Path $dist -Force | Out-Null
# The lock is held until cleanup finishes; concurrent builds must not share staging.
$lock = [IO.File]::Open((Join-Path $dist '.build.lock'), 'OpenOrCreate', 'ReadWrite', 'None')
Push-Location $repo
try {
    # Recover a run interrupted between the two renames.
    if ((Test-Path -LiteralPath $previous) -and -not (Test-Path -LiteralPath $current)) {
        [IO.Directory]::Move($previous, $current)
    }
    Remove-MwbGeneratedDirectory $previous $dist
    Remove-MwbGeneratedDirectory $stage $dist
    New-Item -ItemType Directory -Path "$stage/app", "$stage/installer", "$stage/portable" -Force | Out-Null
    dotnet publish MouseWithOutBorders/MouseWithOutBorders.csproj -c Release -r win-x64 --self-contained true `
        -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true `
        -p:DebugType=none -p:DebugSymbols=false "-p:Version=$Version" -o "$stage/app"
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($LASTEXITCODE)." }
    $app = Join-Path $stage 'app/MouseWithOutBorders.exe'
    if (([Version](Get-Item -LiteralPath $app).VersionInfo.FileVersion).ToString(3) -ne $Version) {
        throw 'Published executable version does not match the requested version.'
    }
    $zip = Join-Path $stage "portable/MouseWithOutBorders-$Version-Portable.zip"
    Compress-Archive -LiteralPath $app, (Join-Path $repo 'LICENSE') -DestinationPath $zip
    & $InnoCompiler "/DAppVersion=$Version" "/DAppSourceDir=$stage/app" "/O$stage/installer" 'installer/MouseWithOutBorders.iss'
    if ($LASTEXITCODE -ne 0) { throw "Inno Setup failed ($LASTEXITCODE)." }
    $setup = Join-Path $stage "installer/MouseWithOutBorders-$Version-Setup.exe"
    $sums = foreach ($asset in @($setup, $zip)) {
        $hash = (Get-FileHash -LiteralPath $asset -Algorithm SHA256).Hash.ToLowerInvariant()
        "$hash *$([IO.Path]::GetFileName($asset))"
    }
    # Overwrite, never append hashes from a previous run.
    $sums | Set-Content -LiteralPath (Join-Path $stage 'SHA256SUMS.txt') -Encoding utf8
    if (Test-Path -LiteralPath $current) { [IO.Directory]::Move($current, $previous) }
    try { [IO.Directory]::Move($stage, $current) }
    catch {
        if (Test-Path -LiteralPath $previous) { [IO.Directory]::Move($previous, $current) }
        throw
    }
    Remove-MwbGeneratedDirectory $previous $dist
    Write-Host "Distribution ready: $current"
} finally {
    try {
        Remove-MwbGeneratedDirectory $stage $dist
        # Publishing copies the runtime into bin/obj as well as the single EXE.
        foreach ($name in @('bin', 'obj')) {
            Remove-MwbGeneratedDirectory (Join-Path $repo "MouseWithOutBorders/$name") (Join-Path $repo 'MouseWithOutBorders')
        }
    } finally {
        Pop-Location
        $lock.Dispose()
    }
}
