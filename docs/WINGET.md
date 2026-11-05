# WinGet preparation

The **aesgalexis.MouseWithOutBorders 0.1.18** package was submitted on 2026-11-05:
[WinGet PR #471495](https://github.com/microsoft/winget-pkgs/pull/471495).
The request is open for community review; catalog availability is pending acceptance.

The [public 0.1.18 release](https://github.com/aesgalexis/mouse-without-borders/releases/tag/v0.1.18)
uses .NET 10 LTS without changing application logic and includes the MIT license
in installer and portable downloads. Existing versioned downloads are preserved.

## Package identity

| Field | Value |
| --- | --- |
| PackageIdentifier | `aesgalexis.MouseWithOutBorders` |
| PackageName / Publisher | `Mouse, with out borders` |
| License | `MIT` |
| Installer | Inno Setup, Windows x64, self-contained |
| Scope | Current user, no elevation required |
| Default location | `%LOCALAPPDATA%\Programs\MouseWithOutBorders` |
| ProductCode / uninstall key | `{BAA10551-2B65-4E83-B102-1F184AE6C673}_is1` |
| AppsAndFeatures DisplayName | `Mouse, with out borders <version>` |
| DisplayVersion | Three-component release version |

The identifier uses the repository owner's namespace and does not claim to be
Microsoft's PowerToys Mouse Without Borders. Keep the existing installer AppId,
publisher, install location and executable name stable so upgrades preserve the
same installation. The license does not need an acceptance dialog.

The manifest uses `InstallerType: inno`, `Scope: user` and `UpgradeBehavior: install`.
Inno's standard silent switches suppress the first-launch checkbox. Do not add
`/MWBUPDATE=1` to the WinGet manifest: that parameter explicitly reopens the app
and belongs to the existing in-app updater. The package is self-contained and
does not need a separate .NET dependency in WinGet. Firewall setup remains the
existing explicit private-LAN action described in [INSTALLATION.md](INSTALLATION.md).

## Generate a candidate from published bytes

Run from the repository root in Windows PowerShell 5.1 or PowerShell 7:

```powershell
./scripts/New-WinGetManifest.ps1 -Version 0.1.18
```

The generator reads the exact public release, downloads its Setup.exe and
SHA256SUMS.txt, and verifies the installer before producing three YAML files.
It does not run the installer, change WinGet settings or submit a pull request.
Do not calculate a submission hash from a local rebuild: a build of the same
source can have different bytes from the published download.

The checked-in candidate is under
`packaging/winget/manifests/a/aesgalexis/MouseWithOutBorders/0.1.18`.
To regenerate the checked-in location, pass
`-OutputRoot ./packaging/winget/manifests`; the default is
`dist/winget/manifests`.
The published Setup.exe hash is:

```text
4c60c4a44edf520bf40db448c39e459597eac79764d9d457d1985a467e4dc3a9
```

Release URLs must remain version-specific. Both main and tag publication now
use `gh release create` and skip existing releases. Never delete and recreate an
existing release or replace its assets to ship these packaging changes. Use a
new application version for the next release, and regenerate that version's
manifests after publication. The public 0.1.18 release is currently marked as a
preview; changing that status is a separate release decision.

## Validate before submission

The submitted 0.1.18 manifests passed `winget validate` with WinGet
**1.29.380** on **2026-11-05**. Published installer bytes were downloaded and
verified against both SHA256SUMS.txt and the GitHub asset digest. This does not
replace the installation checks below. See the [review record](REVIEW-2026-11-05.md).

1. Publish the repository changes so the manifest's `LicenseUrl` resolves to the
   MIT license. Use a new release version with the license bundled for subsequent updates.
2. Run the full Windows workflow for the final release. It includes installer
   checks in a disposable account; do not run installer tests against your daily
   account or a working MWB installation.
3. Run `winget validate --manifest <version-folder>` using a current WinGet client.
   A schema check alone does not prove installation or upgrade behavior.
4. In Windows Sandbox or a disposable Windows account, enable local manifests
   (`winget settings --enable LocalManifestFiles`, with elevation if requested),
   then run the following with the final version's folder:

   ```powershell
   winget install --manifest <version-folder> --silent --disable-interactivity
   winget show --manifest <version-folder>
   winget list --name "Mouse, with out borders 0.1.18" --exact
   winget uninstall --manifest <version-folder> --silent --disable-interactivity
   ```

   Before the package is available in the public catalog, a local manifest does
   not register its PackageIdentifier in that catalog. Detection uses the
   installer's Apps and Features identity; after catalog publication, use
   `winget list --id aesgalexis.MouseWithOutBorders --exact`.

   Confirm silent installation does not launch MWB, the correct per-user entry
   is detected, and uninstall removes program files and registration while
   retaining personal settings. Also test an upgrade from an older installed
   version, both with its window open and hidden in the tray, and verify that
   preferences are retained. Run the two-PC acceptance checks for the release
   you intend to distribute; do not infer hardware results from CI.
5. Check the proposed identifier is still available in `microsoft/winget-pkgs`
   and confirm the metadata against the final installer's actual registry values.
6. Copy the three YAML files into the same `manifests/a/aesgalexis/...` path in a
   fork of `microsoft/winget-pkgs`, then open a pull request for review. Community
   validation includes security scans and installation checks; acceptance is
   determined by that repository. The initial 0.1.18 submission is linked above.

The existing unsigned installer can be prepared as an Inno package; WinGet
validation and security scans still apply. Code signing is a distribution
improvement to plan, and should not be described as completed.

## Runtime support

Version **0.1.18** targets **.NET 10 LTS**, replacing .NET 8 before its support
ends on 2026-11-10. Release packages include their own runtime; users do not need
to install .NET separately. New runtime patches require new MWB releases because
updating .NET on a user's PC does not update an existing self-contained download.
The migration preserves application logic and uses the regression suites,
real Windows clipboard-format checks and disposable native installer checks.
Those checks do not replace physical two-PC acceptance.

## Official references

- [Manifest structure and creation](https://learn.microsoft.com/en-us/windows/package-manager/package/manifest)
- [Community repository policies](https://github.com/microsoft/winget-pkgs/blob/master/doc/Policies.md)
- [Community validation pipeline](https://github.com/microsoft/winget-pkgs/blob/master/doc/Validation.md)
- [.NET support policy](https://dotnet.microsoft.com/en-us/platform/support/policy)
