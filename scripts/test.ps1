# Isolated checks suitable for a developer PC. Native clipboard/installer tests
# remain in disposable CI because they touch the Windows clipboard/registration.
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. "$PSScriptRoot/Artifacts.ps1"
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$suites = @('MousePeer.SmokeTests', 'Updater.SmokeTests', 'Keyboard.SmokeTests',
    'Clipboard.SmokeTests', 'Clipboard.ServiceTests', 'Pairing.UiTests')
Push-Location $repo
try {
    & "$repo/tests/Artifacts.SmokeTests.ps1"
    & "$repo/tests/WinGetManifest.SmokeTests.ps1"
    & "$PSScriptRoot/generate-icon.ps1" "$repo/MouseWithOutBorders/assets/app.ico"
    # Sequential: socket suites reserve the same port.
    foreach ($suite in $suites) {
        try {
            dotnet run --project "tests/$suite/$suite.csproj" -c Release
            if ($LASTEXITCODE -ne 0) { throw "$suite failed ($LASTEXITCODE)." }
        } finally {
            foreach ($name in @('bin', 'obj')) {
                Remove-MwbGeneratedDirectory "$repo/tests/$suite/$name" "$repo/tests/$suite"
            }
        }
    }
} finally { Pop-Location }
