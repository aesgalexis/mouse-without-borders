# Mouse, with out borders

**All your mice and touchpads, two Windows computers.** Pair the PCs once and use the pointing devices attached to either PC across the shared screen edge.

- Cross the **left or right edge** using a mouse or touchpad on either PC.
- Return across the shared edge, or move a local pointing device to reclaim that PC.
- Hide the inactive parked pointer, restoring it when returning or using a local device.
- Local key-to-key remapping with input/output selectors. Keyboard input is not shared over the network.
- Optional text and file clipboard sharing (details below).
- No account or cloud service required.
- Per-user Windows installer and uninstaller; also available as a portable executable.
- Tray icon, option to launch with Windows, option to connect automatically, saved device pairing.
- **Check & install updates** button with GitHub release downloads, SHA-256 verification and automatic app restart (from 0.1.3).
- Authenticated connection with heartbeat detection and automatic reconnection after an established connection drops.

**Project status: preview.** Windows compilation is automated; real two-PC interaction still needs hardware testing. This is not a production/stability guarantee.

## Install on both PCs

The current release is **0.1.13**. Opening MWB again brings the existing window to the foreground, including when hidden or minimized. It includes local mouse/trackpad recovery after handoff and a **Ctrl+Alt+F12** emergency stop. It retains optional text/file clipboard sharing, reliable suppression of remapped keys, a concise English interface and desktop shortcut creation disabled by default during updates. It includes the earlier receiver responsiveness, focus and input-recovery fixes, automatic startup/reconnection and administrator restart. Install it on both PCs for clipboard sharing. Older two-way releases retain mouse compatibility; 0.1.2 uses the incompatible one-way protocol.

1. Download [MouseWithOutBorders-0.1.13-Setup.exe](https://github.com/aesgalexis/mouse-without-borders/releases/download/v0.1.13/MouseWithOutBorders-0.1.13-Setup.exe) from the [v0.1.13 release](https://github.com/aesgalexis/mouse-without-borders/releases/tag/v0.1.13). A portable ZIP and SHA-256 checksums are also available there.
2. Run the setup on **both Windows 10/11 x64 PCs**. No administrator rights are needed to install for the current user.
3. On either PC, select the receiving role, then **Start receiving**. Note the displayed local IP and **8-character pairing code**.
4. On the other PC, click **Connect to other PC**, enter that IP and code, and select which edge leads to the first PC. Click **Connect mouse**.
5. Use **any mouse or touchpad on either PC** to cross the shared edge. The opposite edge is configured automatically on the listening PC. Move back across the shared edge to return. Moving or clicking a local device takes that PC back from a remote mouse.

For an assisted Windows installation or rollback, see [installation instructions](docs/INSTALLATION.md). For automated checks vs. pending two-PC tests, see [verification record](docs/TESTING.md). Changes are tracked in [CHANGELOG.md](CHANGELOG.md).

The assisted `scripts/install.ps1` script defaults to 0.1.13. It verifies release SHA-256 checksums and installs only on the current PC; `-Receiver` adds the optional private-network firewall rule on the PC accepting the connection.

The pairing code has **8 letters and digits**, without ambiguous characters such as `0`, `O`, `1` or `I`. Lowercase input and pasted spaces or hyphens are accepted. It is saved per user under %LOCALAPPDATA%\MouseWithOutBorders and stays the same between launches. Existing 8-character codes are retained during this update. Upgrading from 32-character local keys generates a short local code; the parser also accepts 32-character keys, but **both apps must support the new two-way protocol**. Never publish a pairing code, diagnostic log or settings file.

The app runs in the **notification area** by default when its window is closed. Use its tray menu's **Exit** to quit, or disable **Close to notification area**. **Start with Windows** and **Connect automatically** are enabled by default. After the first launch, MWB starts in the notification area when you sign in and restores its saved connection/listening role. A connecting PC uses the saved IP and pairing code and keeps retrying if the other PC is not ready yet; incomplete connection details stay editable. Your preferences are retained when upgrading.

From version **0.1.3**, click **Check & install updates** at the bottom of the window
to download and install the newest higher version from this repository's GitHub
Releases, including published previews. The app shows download progress, verifies
the release's SHA-256 manifest, then starts Setup with installation progress and
exits. Setup reopens the app after a successful update and retains pairing and
preferences. It never restarts Windows. Updates run only when you click the button.
If the check, download or verification fails, the app stays open and you can retry.
When run from a portable copy, this action installs the per-user application;
it does not replace the portable executable. Older versions need one manual
Setup installation to get the button.

## Local keyboard remapping

Open **Remap keys...** from the main window or tray. Select the input and output keys, click **Add**, then **Save**. Mappings are disabled by default, saved per user, and run while MWB is open independently of the mouse connection. The editor pauses remapping; Cancel restores the previous configuration. **Start with Windows** resumes saved mappings after sign-in. Elevated applications require MWB at the same privilege level; secure screens and firmware-only keys are not remapped. Keyboard events are never transmitted or logged.

## Clipboard sharing

Available from **0.1.10**. Enable **Share clipboard** on both PCs. New copies of plain text and files then become available to paste on the paired PC in either direction. Enabling or reconnecting does not send existing clipboard content.

- Plain text: up to **1 MiB** encoded as UTF-8. Rich formatting and images are excluded.
- Files: up to **20 regular files** and **25 MiB total per copy**. Folders, links and files inside linked folders are excluded. Received files are copies; originals are never moved.
- Transfers use encrypted, acknowledged chunks and yield to mouse traffic. A new copy, disabling the option or disconnecting cancels unfinished work.
- Received files are verified before appearing in the clipboard. They remain available after disconnecting or exiting MWB. The cache under `%LOCALAPPDATA%\MouseWithOutBorders\Clipboard` is limited to **64 MiB**; older batches are pruned, while the newest completed batch is retained for pasting.

Both peers must support clipboard sharing and have it enabled. Older releases continue to share the mouse. Desktop shortcut creation is unchecked by default, including during updates; existing shortcuts are preserved.

## Network and security

Both PCs must be on the same **trusted private network** with TCP port **42187** reachable on the receiver.

If Windows Firewall blocks the receiver, allow the application on **private** networks when prompted. To add a rule manually, run PowerShell as administrator on the receiving PC:

    New-NetFirewallRule -DisplayName "Mouse With Out Borders" -Direction Inbound -Action Allow -Profile Private -Protocol TCP -LocalPort 42187 -Program "$env:LOCALAPPDATA\Programs\MouseWithOutBorders\MouseWithOutBorders.exe"

The connection requires matching pairing keys, uses challenge-based HMAC authentication and verifies every packet. Mouse packets are **not encrypted**. Clipboard transfers additionally use session-bound AES-GCM encryption. Use a trusted private network; never open or port-forward TCP 42187 to the internet. The listener binds to the local PC's network interfaces; use Windows Firewall to limit access.

Settings are stored in the signed-in user's LocalAppData folder; Windows account security protects access to those files. No cloud service is used.

## Supported / limits

- Exactly **two PCs**, Windows 10/11 x64, with interactive user sessions.
- Left/right screen arrangement; the outer edge is based on the Windows virtual desktop, so other monitors on a PC are taken into account. Complex non-rectangular display arrangements need additional testing.
- Windows mouse/touchpad pointer movement, vertical/horizontal wheel, primary and X buttons are supported on both PCs.
- A handoff waits for locally held buttons to be released so a local click/drag is not split between desktops.
- Windows security boundaries (UAC prompts, locked/secure desktop, elevated applications) can restrict input injection.
- Some mice, DPI settings, remote sessions, exclusive-input games, network policies and antivirus software may behave differently. Physical two-PC testing is still necessary.
- The origin PC releases its cursor on disconnect or heartbeat timeout (normally within ~5 seconds).

## Troubleshooting

- **Local mouse/trackpad stops after a crossing:** update **both PCs to 0.1.13**. This release separates Windows injection from capture and lets local movement cancel pending cursor placements. Press **Ctrl+Alt+F12** on the affected PC to stop sharing without restarting Windows (the shortcut must be available).

- **Inconsistent control or focus:** run only one mouse-sharing application at a time. Another active MWB instance or PowerToys Mouse Without Borders can intercept the same pointer input and interfere with handoffs.
- **Cannot connect:** confirm both PCs are on the same LAN, the receiver IP is correct, and Windows Firewall allows TCP 42187 on a private network.
- **Both PCs show a code:** on either PC, click **Connect to other PC**. Enter the listening PC's local IP and code, and click **Connect mouse**.
- **Update both PCs:** two-way input requires the new version on both computers; 0.1.2 cannot use this protocol.
- **Pairing rejected:** enter the receiving PC's current 8-character code again, especially after an upgrade.
- **Cursor does not cross:** make sure both apps report connected, and move through the *outer* edge selected on the controlling PC.
- **App disappeared:** check the Windows notification area (system tray); double-click its icon to reopen.
- **Task Manager won't highlight Close/Minimize or accept remote clicks:** Windows can block MWB's injected input when the target window has higher privileges (UIPI). On the PC displaying Task Manager, use **Restart as administrator...** in MWB or its tray menu, and approve UAC with that PC's local mouse/touchpad. MWB saves pairing and resumes an active connection after the restart. Cancelling UAC leaves the original connection running. Administrator mode applies to this session; normal Windows startup remains unelevated. With an older MWB version, choose **Exit** in its tray menu, then right-click its shortcut and choose **Run as administrator**. UAC prompts and locked/secure desktops still require a local device.
- **Diagnostics:** choose **Diagnostic log**. Logs are under %LOCALAPPDATA%\MouseWithOutBorders and rotate at approximately 512 KB.

## Development

Requires **Windows**, .NET 8 SDK, and (to build the installer locally) [Inno Setup 6](https://jrsoftware.org/isinfo.php).

    dotnet build MouseWithOutBorders/MouseWithOutBorders.csproj -c Release
    dotnet publish MouseWithOutBorders/MouseWithOutBorders.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o dist/app
    & ([IO.Path]::Combine([Environment]::GetFolderPath('ProgramFilesX86'), 'Inno Setup 6', 'ISCC.exe')) installer\MouseWithOutBorders.iss

The icon is generated automatically on Windows by the MSBuild project target. CI on windows-latest generates the icon, builds and publishes the portable ZIP and installable EXE as build artifacts. After checks pass on main, a new project version publishes a preview release. Version tags also build and publish a GitHub Release.

The assisted installer prompts for Windows admin approval only if `-Receiver` is selected to add a private-network firewall rule. The application installs in the current user's %LOCALAPPDATA%\Programs directory and runs without elevation by default; its optional **Restart as administrator...** action requests UAC approval for the current session. Uninstall retains personal settings by design; these can be removed from LocalAppData manually.

Clipboard tests use isolated data and fake clipboard adapters; they do not modify the user's clipboard.
