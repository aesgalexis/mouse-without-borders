# Mouse, with out borders

MWB is a free Windows application for sharing mouse and touchpad input between two PCs on your local network. Pair the computers once, then move the pointer across the left or right screen edge to control the other PC.

It works with pointing devices attached to either computer and requires no account or cloud service.

**[Download for Windows x64](https://github.com/aesgalexis/mouse-without-borders/releases)**

## Features

- Use mice and touchpads on either PC to control both computers.
- Optionally share plain text and files through the clipboard.
- Remap keys locally on each PC. Keyboard input is not shared between computers.
- Save pairing details, start with Windows and reconnect automatically.
- Install for the current user, or run the portable executable.

## Getting started

You need two Windows 10/11 x64 PCs on the same trusted private network.

1. Download the **Setup.exe** from the [releases page](https://github.com/aesgalexis/mouse-without-borders/releases) and install it on both PCs. Installation does not require administrator rights. A portable ZIP is also available.
2. On the first PC, select **Allow connection**, then **Start receiving**. Note its local IP address and pairing code beginning with **MWB3-**. Use **Copy code** to copy the full code.
3. On the second PC, select **Connect to other PC**. Enter the first PC's IP address and pairing code, choose the screen edge that leads to it, then select **Connect mouse**.
4. Move a mouse or touchpad across the shared edge to control the other PC. Cross back to return, or use a pointing device attached to that PC to take control locally.

If Windows Firewall prompts you, allow MWB on private networks. The receiving PC must be reachable on TCP port **42187**. See the [installation guide](docs/INSTALLATION.md) for firewall setup, assisted installation and uninstall instructions.

## Everyday use

- **Opening and closing:** closing the window keeps MWB running in the notification area by default. Open MWB again from its shortcut or double-click the tray icon to show the window. Choose **Exit** from the tray menu to quit.
- **Connection status:** the tray icon is gray while disconnected, waiting or retrying. It turns blue when a sharing session is connected.
- **Tray icon visibility:** on Windows 11, **Always show tray icon** is enabled by default in the tray menu. MWB reapplies its own visibility preference at startup, after Explorer recreates the taskbar and every 30 seconds. Turn it off to let Windows manage visibility manually. MWB saves that choice across restarts and updates. Promotion uses an internal Windows preference and is best effort. On Windows 10, or if Windows keeps MWB in the hidden-icons menu, use **Notification icon settings...** or drag the icon to the visible part of the tray.
- **Startup:** **Start with Windows** and **Connect automatically** are enabled by default. You can turn either option off; preferences and pairing details are retained across updates.
- **Stopping sharing:** press **Ctrl+Alt+F12**, or choose **Stop mouse sharing** from the tray menu. The shortcut also works while the window is hidden, provided Windows has not assigned it to another application.
- **Updates:** select **Check & install updates** from the main window or the tray icon's right-click menu to check GitHub Releases, including previews. The tray action opens the window to show progress and results. MWB verifies the download's SHA-256 checksum before installing and restarting. Updates are checked only on request. Updating a portable copy installs the per-user application rather than replacing the portable executable.

## Optional features

### Clipboard sharing

Enable **Share clipboard** on both PCs to share new copies of plain text and files in either direction. This option is disabled by default; enabling it does not send existing clipboard content.

- Plain text: up to **1 MiB**. Rich formatting and images are not supported.
- Files: up to **20 regular files** and **25 MiB total per copy**. Folders and links are not supported. Originals are never moved.

Clipboard transfers are encrypted and verified before becoming available to paste. Received files remain available after disconnecting or exiting MWB.

### Local key remapping

Open **Remap keys...** from the main window or tray menu. Select the input and output keys, choose **Add**, then **Save**. Remapping is disabled by default and works locally while MWB is running, independently of the mouse connection. Keyboard events are never sent to the other PC.

## Status and limitations

MWB is currently a **preview**. Automated Windows build, transport, interface and installer checks run in GitHub Actions. Physical testing across different mice, touchpads and two-PC setups remains incomplete; see the [verification record](docs/TESTING.md).

- Supports exactly two Windows 10/11 x64 PCs, arranged to the left and right.
- Uses the outer edges of the Windows virtual desktop. Complex multi-monitor arrangements need further testing.
- Both PCs must have an active, unlocked user session. UAC prompts and secure desktops require a local device.
- Run one mouse-sharing application at a time to avoid competing input hooks.

## Network and privacy

All mouse events, clipboard transfers and session messages travel over **TLS 1.2 or 1.3**. A random **160-bit pairing secret** authenticates the receiver's certificate pin, and the connecting PC proves knowledge of that secret inside TLS. Messages also have direction-bound authentication and sequence checks; clipboard payloads retain their additional AES-GCM protection. No certificate authority or certificate installation is required.

**Upgrading to 0.1.19:** update both PCs and pair again using the new **MWB3-** code. Old 8-character codes and legacy hexadecimal keys are replaced locally, and old saved remote codes are cleared. Preferences are retained. There is no fallback to the unencrypted protocol; 0.1.18 and earlier cannot connect to 0.1.19.

Use MWB on a trusted private network. Encryption does not hide the peers' IP addresses or traffic timing/volume, or protect a compromised PC. Do not expose or forward TCP port 42187 to the internet.

Settings and diagnostic logs are stored locally under `%LOCALAPPDATA%\MouseWithOutBorders`. Keep pairing codes, settings files and diagnostic logs private when reporting problems.

## Troubleshooting

- **Cannot connect:** check the receiving PC's LAN IP, pairing code, listening status and private-network firewall rules.
- **Window is hidden:** open MWB again from its shortcut, or double-click the notification-area icon.
- **Local pointer is stuck:** press **Ctrl+Alt+F12** to stop sharing and restore local control.
- **Cannot control an elevated application:** use **Restart as administrator...** in MWB on the PC displaying that application, then approve UAC locally. Administrator mode applies to the current session.

Use **Diagnostic log** in the main window to inspect connection and input errors.

## Building from source

Requires Windows and the **.NET 10 SDK**.

```powershell
dotnet build MouseWithOutBorders/MouseWithOutBorders.csproj -c Release
```

To build the self-contained executable, portable ZIP, installer and checksums,
install [Inno Setup 6](https://jrsoftware.org/isinfo.php), then run:

```powershell
./scripts/build.ps1
```

The complete latest distribution is in `dist/current`. Each successful build
replaces it; a failed build keeps the previous complete output. Temporary publish
files and duplicate runtime copies in `bin`/`obj` are removed automatically.
No version history is stored locally. See [build output and cleanup](docs/BUILDING.md).

Run the isolated developer checks with `./scripts/test.ps1`. This removes test
outputs afterwards and does not access your clipboard or install the application.
Native clipboard and install/upgrade/uninstall checks run in disposable Windows CI.
The application icon is generated during the Windows build. The
[GitHub Actions workflow](.github/workflows/build.yml) uses the same packaging script.

## Documentation

- [Installation, updates and uninstall](docs/INSTALLATION.md)
- [Testing and known validation limits](docs/TESTING.md)
- [Encrypted transport and security model](docs/SECURITY.md)
- [WinGet package preparation and submission](docs/WINGET.md)
- [Changelog](CHANGELOG.md)

## License

MWB is licensed under the [MIT License](LICENSE).
