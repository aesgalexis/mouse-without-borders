# Build outputs and automatic cleanup

On Windows with .NET 10 SDK and Inno Setup 6:

```powershell
./scripts/test.ps1
./scripts/build.ps1
```

`build.ps1` accepts `-Version 0.1.19` (defaults to the project version) and
`-InnoCompiler C:/path/to/ISCC.exe`. It also finds a single local compiler at
`.tools/inno/ISCC.exe`. It never downloads a new tool copy for each build.
GitHub Actions uses this same packaging entry point.

## Directory ownership

| Path | Producer and purpose | Retention |
| --- | --- | --- |
| `MouseWithOutBorders/`, `tests/`, `scripts/`, `installer/`, `.github/` | Application, test and build sources/configuration | Keep in Git |
| `docs/`, `packaging/winget/` | Documentation and reviewed manifests, including published versions | Keep in Git; not build artifacts |
| `dist/current/app/` | Single self-contained Windows x64 executable | Latest complete build |
| `dist/current/portable/`, `dist/current/installer/` | One ZIP with EXE + LICENSE and one Setup.exe | Latest complete build |
| `dist/current/SHA256SUMS.txt` | Hashes of exactly those two packages | Overwritten with each build |
| `dist/.staging/`, `dist/.previous/` | Temporary new/old distribution during replacement | Removed after success; staging also removed on failure |
| `dist/installer-tests/` | Disposable CI installer logs and legacy extraction | Logs replaced on next test; extraction removed in `finally` |
| `dist/winget/manifests/` | Verified WinGet manifest generator output | Default output replaced on next generation; explicit `-OutputRoot` is caller-owned |
| `**/bin/`, `**/obj/` | .NET compilation and intermediate runtime copies | Fixed SDK paths; packaging removes app outputs; test wrapper removes each suite's outputs even on failure |
| `MouseWithOutBorders/assets/app.ico` | Generated from the SVG by MSBuild | Reusable, ignored by Git |
| `.tools/` | One reusable local Inno compiler when not installed system-wide | Keep for later builds, ignored by Git |
| `.cache/` | Optional shared local SDK/NuGet cache for restricted environments | Reused across builds; no per-version release copies |
| `.local/legacy-dist/` | Small original scripts, probe sources, screenshots, diagnostics and settings rescued during cleanup | Preserved local files; never automatically deleted or committed |

NuGet normally uses its shared user cache. If a restricted environment needs a
workspace cache, set `DOTNET_CLI_HOME` to `.cache/dotnet-cli` and `NUGET_PACKAGES`
to `.cache/nuget` once in that shell. Reuse these paths instead of creating
`dotnet-home` directories under each release. Do not place source or settings in
generated directories.

## Replacement and failure behavior

The current executable, installer and ZIP are built once in `dist/.staging`.
Only after publish, version validation, ZIP creation, Inno compilation and
checksums succeed does the script replace `dist/current`. The old directory is
briefly renamed to `.previous` and removed after replacement. A failed publish
or packaging step leaves `current` intact. If interrupted between the renames,
the next invocation restores `.previous` when `current` is missing. At most one
temporary previous output exists; no historical backup is retained.

A file lock prevents concurrent packaging runs from sharing staging. Cleanup
validates absolute paths and refuses directory links. Stop an executable running
from `dist/current` before replacing it; an installed copy lives elsewhere.
Do not invoke ISCC directly for repeated releases: it bypasses replacement and
cleanup. Use `build.ps1` for all distribution builds.

`test.ps1` runs the offline WinGet, artifact lifecycle, transport, updater,
keyboard, clipboard and UI checks sequentially. Clipboard-service fixtures reuse
one directory inside the test output, so failures cannot leave GUID directories
under `dist`. Other isolated temporary fixtures already use `finally` cleanup.
Native clipboard and installer tests remain in disposable Windows CI; running
them on a developer account can affect clipboard or installation state.

The 2026-11-06 cleanup removed historical `release-*`, `verified-github-*`, CI
downloads, duplicate ZIP/Setup files, obsolete SDK caches and old test binaries.
Original local diagnostics/configuration were moved out first. Application
settings under `%LOCALAPPDATA%/MouseWithOutBorders` and installed applications
are outside build cleanup.

Measured file sizes, including ignored files and `.git`, changed from
7,196,176,678 bytes (6.70 GiB) to approximately 502.5 MB (0.468 GiB):
6,693,690,276 bytes (6.234 GiB) recovered. `dist` changed from 6,328,452,386
bytes to 144,693,124 bytes (138 MiB). These are logical file sizes, not an NTFS
allocation measurement. The remaining workspace includes one shared NuGet
cache, one Inno compiler and 1.84 MB of preserved local diagnostics/source/settings.
