# Changelog

## 0.1.11 — Local mouse recovery after handoff

- Move native mouse injection to a bounded output worker, keeping the capture thread available while Windows delivers injected events. Preserve queued button releases during normal shutdown.
- Let a local mouse or trackpad cancel pending cursor placements as soon as the PC is local or receiving; reject late placements after local recovery.
- Detect stalled/rejected native output and stop the affected session instead of keeping local input suppressed.
- Add **Ctrl+Alt+F12** to disconnect sharing from the independent UI thread, including while the window is hidden.
- Add regressions for blocked output, separate injection/capture threads, release ordering, local recovery during pending placement and emergency stop. Pairing and clipboard remain compatible; update both PCs for the recovery fixes.

## 0.1.10 — Clipboard sharing and input consistency

- Suppress remapped source keys consistently, including printable/OEM keys with modifiers and repeats. Reserve key state before injection, keep sources suppressed on native failures and prevent reentrant cleanup/configuration from leaving stale or stuck keys.
- Default the desktop shortcut task to unchecked on every installation and update. Preserve existing shortcuts without recreating one the user deleted.
- Remove redundant subtitles, examples, setup notes and key-remapping presets; keep concise English controls and validation.
- Add optional, bidirectional clipboard sharing for plain text up to 1 MiB and batches of up to 20 regular files, 25 MiB total. Folders and links are excluded. The option is disabled by default and must be enabled on both PCs.
- Encrypt clipboard payloads with session-bound AES-GCM, acknowledge bounded chunks, prioritize mouse packets, validate file names/sizes/SHA-256 and publish only completed copies. Older peers retain mouse sharing without clipboard transfers.
- Cancel superseded/disconnected transfers, suppress clipboard echoes and retain verified file copies in a bounded local cache so they can be pasted after disconnecting.

## 0.1.9 — Consistent English interface

- Use **Remap keys...** consistently in the main window and notification-area menu.
- Translate the complete key-remapping editor, status/help text, save failures and validation messages into English.
- Show printable OEM key symbols with English fallback labels instead of localized Windows descriptive key names, preserving the current keyboard layout.
- Update installation instructions and current documentation to match the English controls. Retain all 0.1.8 input, focus and recovery fixes.

## 0.1.8 — Responsive receiver windows and consistent input recovery

- Move mouse capture, packet processing and input injection to a dedicated input thread so minimizing, dragging or hiding MWB cannot block the remote button-up/movement events needed to finish Windows tracking loops.
- Defer pointer-window creation/destruction outside low-level hook callbacks and use native non-activating visibility changes to preserve foreground focus during crossings.
- Limit cursor-placement acknowledgement waits to 250 ms, retire expired placement tags and recover visibility automatically instead of leaving the local pointer blocked.
- Reserve injected button state before calling Windows, handle reentrant releases and retries, and stop a failed transition if a held button cannot be released.
- Preserve local drags already in progress when pairing/reconnecting, and clear obsolete release suppression when a new click begins.
- Bound input queues, detect a stopped input worker, contain deferred callback errors and drain cursor restoration before removing the hook during shutdown.
- Add blocked-UI, native receiver-minimize/focus, held-button/reentrancy, missing-acknowledgement and stalled-peer regressions.

## 0.1.7 — Keep the returning cursor at the shared edge

- Tag native cursor placements by ownership and discard queued parking/remote moves from a previous owner, preventing a late central parking move from overwriting the return position.
- Keep old-anchor movement suppressed and the parked pointer hidden until Windows processes the new owner's first placement. Return beside the shared edge at the other PC's exit height.
- Cancel queued remote motion when a local mouse takes over, preserve movement/click ordering within each ownership, and restore pointer visibility during cleanup.
- Add delayed-input regression checks for both layouts and both PCs, plus a native Windows replay that reproduces the unguarded centre jump and verifies the corrected return.

## 0.1.6 — Automatic startup, reconnection and administrator restart

- Enable **Start with Windows** and automatic connection/listening by default. Save the Windows startup preference, restore saved pairing at sign-in, and preserve explicit opt-outs.
- Skip automatic connection for incomplete pairing details and show offline-PC retry status instead of a misleading mouse-input error.
- Add an explicit **Restart as administrator...** action in the window and tray
  for Task Manager and other elevated windows that reject remote mouse input.
  Preserve pairing and resume active sharing; cancelling UAC keeps sharing running.
- Explain the Windows input privilege restriction and require a local device for
  UAC prompts and locked screens.

## 0.1.5 — Local keyboard remapping

- Add mouse-operated input/output key selectors, add/edit/remove controls, a pause switch, and an optional F11 → Backspace / F12 → Enter preset.
- Save per-user mappings and restore them when MWB starts, independently of the mouse connection.
- Handle key repeat, swaps, shared destinations and physical target keys; release injected keys when changing mappings, pausing or exiting.
- Run keyboard capture on a separate message thread, filter generated input and report Windows injection failures without logging typed keys.
- Display layout-specific OEM key names and distinguish Enter from Numpad Enter. Keyboard input remains local and is never sent to the other PC.

## 0.1.4 — Cursor visibility, consistent icons and dark interface

- Hide the parked cursor on the PC just left, on both crossing and returning. Restore it for local input, stopping and application exit.
- Use a small non-activating cursor window rather than changing the Windows cursor scheme or display counter.
- Load dedicated embedded icons for the application window, tray and header instead of relying on Explorer's cached associated icon.
- Install a versioned icon file for shortcuts and refresh existing desktop shortcuts pointing to this installation, including custom shortcut names.
- Apply explicit dark rendering to tray menus, selectors, checkboxes, role buttons and disabled/hover states; brighten secondary text and support scrolling on shorter screens.

## 0.1.3 — Two-way mouse sharing and updates

- Replace the app icon with a flat blue cursor crossing an open screen edge, generated from a transparent SVG at Windows icon sizes from 16 to 256 pixels.
- Add a manual **Check & install updates** button with the current version, download progress and retryable errors.
- Find the highest newer GitHub Release version, including published previews, and verify the installer's SHA-256 manifest before launching Setup.
- Close the app only after the verified installer starts; reopen it after successful installation without restarting Windows or losing pairing settings.
- Close the installed MWB automatically before updates, including when it is hidden in the notification area.
- Request a clean exit first, then terminate older or unresponsive instances from the same installation and Windows session.
- Apply the same shutdown when uninstalling and stop before changing files if MWB cannot be closed.
- Fix the desktop context menu appearing when returning across the shared edge: release only buttons actually pressed by MWB, and ignore unmatched releases.
- Capture mouse movement, clicks and scrolling on both PCs after one pairing; connecting/listening no longer determines which PC can share its mice.
- Include Windows pointer input from built-in touchpads and other mouse devices, while filtering MWB's own input to prevent feedback.
- Synchronize the shared screen edge, let local input reclaim control, and resolve simultaneous crossings and late packets.
- Require both PCs to run the new two-way protocol, with an explicit update message when connecting to an older version.

## 0.1.2 — Clear pairing and connection recovery

- Replace the hidden role dropdown with two visible role buttons and a direct action to enter the other PC's code, including while listening.
- Generate saved 8-character pairing codes, migrate existing local keys, accept lowercase/formatted input and retain connections to receivers with legacy keys.
- Add pairing-code and setup UI regression checks.
- Correct GitHub download URLs and include native runtime libraries in the single-file portable app and installer.
- Limit controller connection and pairing to five seconds, while preserving user cancellation.
- Report Windows mouse input failures and close the affected peer so the controller releases its cursor.
- Attempt every mouse button release during cleanup, logging failures without stopping the receiver.
- Add regression checks for stalled pairing, cancellation, rejected input and button cleanup.

## 0.1.1 — Windows installer and operational setup

- Assisted local Windows installation and uninstall PowerShell scripts.
- SHA-256 checksums for release assets, verified during assisted installation.
- GitHub Actions silent Setup.exe install/uninstall smoke test.
- Installation, rollback, firewall and two-PC validation documentation.
- Clear distinction between automated tests and unverified real-hardware behavior.

## 0.1.0 — Initial Windows preview

- Native Windows Forms UI with per-user saved role, IP, pairing key and layout preference.
- Mouse-only cross-PC movement, buttons and wheel over a paired LAN connection.
- Connection heartbeat, lost-connection recovery and reconnect support.
- System tray and optional Windows startup.
- Self-contained x64 build and per-user Inno Setup installer.
- Automated LAN authentication/packet round-trip smoke tests.
