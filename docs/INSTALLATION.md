# Windows installation (two PCs)

This Windows desktop application shares mouse input and an optional clipboard between two PCs, and can remap keys locally. The same executable runs on both PCs; each PC chooses its own connection role.

**Security update in 0.1.19:** all traffic now requires TLS 1.2/1.3 and a new **MWB3-** pairing code containing 160 random bits. Update both PCs, then copy the new receiver code and pair again. Old local codes are replaced and obsolete saved remote codes are cleared; preferences are retained. Version 0.1.18 and earlier use the older protocol and cannot connect to 0.1.19.

The current release is **0.1.23**. Update both PCs to use the new mouse readiness confirmation. Existing MWB3 pairing codes remain valid. The tray icon stays gray until both PCs are ready to share the mouse. The tray menu offers **Connect** and enables **Disconnect** during an active session. On Windows 11, **Always show tray icon** is enabled by default.

**Status:** Windows x64 preview. Installation and transport tests run in GitHub Actions. Real pointer switching must still be checked on two physical machines. The current release is unsigned and Windows SmartScreen may warn before installation.

## Option A — easiest: Setup.exe

1. Open the [latest release](https://github.com/aesgalexis/mouse-without-borders/releases/latest).
2. Download [MouseWithOutBorders-0.1.23-Setup.exe](https://github.com/aesgalexis/mouse-without-borders/releases/download/v0.1.23/MouseWithOutBorders-0.1.23-Setup.exe).
3. Run it on each PC. Installation is for the current Windows user, without an administrator password. You can also use the portable ZIP without installing anything.
4. On either PC: open Mouse, with out borders; click **Allow connection**; click **Start receiving**.
5. Note this PC's local IPv4 address and the full **MWB3-** pairing code. **Copy code** copies all four groups of random characters.
6. On the other PC: click **Connect to other PC**, enter the first PC's IP and code, and select **Right** or **Left** according to the physical layout. Click **Connect mouse**. The **Enter other PC's code** button also opens these fields, even if this PC is already listening.
7. Either PC's physical mice and touchpad can now cross the shared edge. The listening PC uses the opposite edge automatically. Return across the shared edge, or use a local device to reclaim its PC. No second pairing is needed.

Keep both PCs powered on and connected to the same trusted private LAN.

This installer automatically closes the installed app before updating, even
when its window is hidden in the notification area. It requests a clean exit,
then closes an older or unresponsive instance if necessary. Settings are retained.
The same shutdown applies when uninstalling.

## Local key remapping (0.1.5 and later)

For local key remapping, open **Remap keys...**. Choose the input and output
keys with the two selectors, click **Add**, then **Save**.
Mappings run only on this PC while MWB is open, even
without another PC connected. **Start with Windows** enables them after sign-in.
The editor pauses remapping; cancelling preserves the previous configuration.

## Clipboard sharing (0.1.10 and later)

Enable **Share clipboard** on both paired PCs. New copies of plain text up to
1 MiB, or up to 20 regular files and 25 MiB total, can then be pasted on the other
PC. Folders and links are excluded. Clipboard sharing is disabled by default;
enabling or reconnecting does not send existing content. Transfers are encrypted
and yield to mouse traffic.

## Manual updates from the app (0.1.3 and later)

Click **Check & install updates** in the app window or the tray icon's right-click
menu. The tray action opens the window to show progress and results. It checks this
project's GitHub Releases, including previews, and installs only a higher version.
Download progress and errors appear below the button. The SHA-256 manifest must
match before Setup starts. The app then exits, Setup shows installation progress,
and the updated app opens automatically. Preferences are retained; Windows
is not restarted. Failed checks/downloads leave the app running and allow retry.
If Setup itself fails, it displays the error; the existing app can be reopened.

Updates require internet access to GitHub and are checked only on demand. A
portable copy uses the same per-user Setup installation; its original file remains
in place. Install 0.1.3 or later manually once to gain the button on an older app.

Upgrading to 0.1.19 replaces every old 8-character code or legacy hexadecimal key with a saved MWB3 code. Enter the new receiver code on the other PC once. Both PCs must run the encrypted protocol; no unencrypted compatibility mode is available.

## Option B — assisted install (Windows PowerShell)

This script defaults to the **0.1.23** release.

Download scripts/install.ps1 from the repository, inspect it, then run from PowerShell in the folder where you saved it:

    .\install.ps1

The script verifies the release download against its SHA-256 manifest, installs for the current user and opens the application.

On the **receiving PC**, use:

    .\install.ps1 -Receiver

This requests administrator authorization **only** to add a TCP 42187 inbound Windows Firewall rule scoped to private networks, which may otherwise block the receiver. If that prompt is declined, the application still installs; you may need to allow it manually.

If your Windows policy blocks running scripts, download Setup.exe directly and run it instead. You do **not** need to disable Windows security.

To run without opening the UI after the installation:

    .\install.ps1 -NoLaunch

## Network details

- Receiver TCP port: **42187**.
- TLS 1.2/1.3 with a certificate pin authenticated by the 160-bit pairing secret, then client proof inside TLS and direction-bound HMAC on every event. Per-connection certificates are not installed or trusted globally; Windows uses a temporary user key container that is removed when the certificate is disposed.
- Mouse and clipboard traffic are encrypted; clipboard payloads additionally use session-bound AES-GCM. IP addresses and traffic timing/volume remain visible. Use a trusted LAN only, never open or port-forward the port to the internet. Published 0.1.18 mouse traffic remains unencrypted.
- UAC prompts and locked/secure desktops require a local device. For Task Manager and other elevated windows, use **Restart as administrator...** in MWB on the affected PC and approve UAC locally. Cancelling UAC keeps the current connection running.
- If there are several IPv4 addresses (VPNs, virtual adapters), choose the address on the network shared by both PCs.

## Startup / tray

- **Start with Windows** is enabled by default and adds a per-user startup entry for the current executable. Disable it to save an opt-out.
- **Receive automatically when MWB opens** starts listening in receiver mode. **Connect and reconnect automatically** attempts the saved connection in controller mode and retries when the other PC is unavailable. Automatic connection is enabled by default. Incomplete pairing stays editable and existing opt-outs are retained.
- Closing the window minimizes it to the **notification area** by default.
- Right-click the tray icon and choose **Exit** to actually stop the application.

## Uninstall and rollback

Use Windows Settings > Installed apps > Mouse, with out borders > Uninstall, or run:

    .\scripts\uninstall.ps1

The assisted uninstall removes the app, saved Windows startup entry and (with administrator confirmation) the optional firewall rule. Settings and diagnostic logs stay in %LOCALAPPDATA%\MouseWithOutBorders by default.

To also delete pairing details and diagnostics:

    .\scripts\uninstall.ps1 -PurgeSettings

Windows' standard uninstaller removes the program and startup entry but does not automatically remove a separately elevated manual firewall rule; use the assisted uninstall or Windows Defender Firewall to clean that rule.

## Limitations and troubleshooting

- Run one mouse-sharing application at a time. Close another MWB instance or the PowerToys Mouse Without Borders module when testing this app; competing global input hooks can interfere with control and focus.
- This preview is limited to two Windows 10/11 x64 computers.
- Remote input includes mouse movement, clicks and wheel. Keyboard remapping is local; keyboard input and remote desktop are not shared. Clipboard text and files require explicit opt-in on both PCs.
- No Windows Service. The optional administrator restart applies only to the current session; Windows startup stays unelevated. UAC and locked/secure desktops require a local device.
- For connectivity, ensure private network profile, correct LAN IP, receiver running, both PCs updated and a matching MWB3 code. A rejected code or identity check stops automatic retries so the code can be corrected.
- For a lost connection, the controller's cursor is released after its network heartbeat timeout, normally about 5 seconds.
- Diagnostic logs: %LOCALAPPDATA%\MouseWithOutBorders\diagnostics.log. Do not publish logs containing personal information.
- These scripts change only the **local PC** on which they are run. They cannot install the application on another machine remotely.
