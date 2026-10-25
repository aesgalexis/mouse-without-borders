# Verification record and manual acceptance checklist

This file distinguishes automated checks from hardware testing. Do not claim a feature works on two physical PCs until observed.

## Automated (GitHub Actions / Windows x64)

- [x] .NET Windows Forms restore, build, and self-contained publish (prior build on 2026-10-20)
- [x] Inno Setup compiler produces Setup.exe (prior build on 2026-10-20)
- [x] LAN protocol smoke tests: successful authenticated TCP exchange and rejection of wrong key (prior build on 2026-10-20)
- [ ] **v0.1.1** complete CI including silent install/uninstall verification; see Actions run for current result
- [ ] SHA-256 manifest attached to v0.1.1 release, after publication

CI checks only what it runs. A successful build does not prove smooth mouse interaction.

## Version 0.1.11 handoff recovery (2026-10-25)

The reported failure is loss of local mouse/trackpad control after leaving the
receiving PC. Native injection previously ran on the same thread as the global
mouse hook, and pending placements gated all physical movement. Native mouse
output now has its own bounded worker; local/receiving physical movement retires
pending placement tags, and Ctrl+Alt+F12 stops sharing from the UI thread.

Local Release compilation passes. The standard UI suite and new input-recovery
suite pass, including a sender deliberately blocked waiting for capture-thread
progress, queued down/up ordering through normal shutdown, output rejection and
timeout, cancellation of stale placements by local movement, and emergency stop.
Transport/ownership, updater, keyboard, clipboard transfer and clipboard service
suites pass. Run fixed-port transport/clipboard suites sequentially.

The optional native receiver/focus replay did **not** pass in this local session:
Windows output stalled and the new watchdog stopped the sharing service. This is
recorded as a remaining native validation limitation, not a successful handoff
test. Physical acceptance must install 0.1.11 on both PCs, repeat crossings and
returns from each local mouse/trackpad, and verify local recovery and the emergency
shortcut with MWB hidden. No third-party application is identified as the cause.

## Version 0.1.12 terminated-instance update (2026-10-25)

An actual local installation attempt with the published 0.1.11 installer failed:
WMI retained three old MWB processes with non-STILL_ACTIVE exit codes while their
native termination waits remained pending. Setup counted those as live, and the
retained singleton object also prevented normal startup.

0.1.12 checks native termination status, renames the terminated mapped executable
before replacement, and acquires mutex ownership rather than checking object
existence. The complete UI suite passes, including unowned/abandoned retained
mutexes, restart, and rejection while another thread owns the instance mutex.
The locally built 0.1.12 installer then completed successfully on this affected
PC (exit 0), installed file version 0.1.12.0, and reopened MWB without a Windows
restart. The original native/two-PC validation limitation above still applies.

## Version 0.1.10 clipboard and interface verification (2026-10-24)

The Windows Release build and self-contained executable compile successfully. The cleaned
main window and key-remapping editor were rendered and visually inspected.

Local suites pass for mouse ownership/input, authenticated TCP transport,
backpressure, updater, keyboard remapping and the complete setup UI. Clipboard
coverage includes explicit opt-in and persistence, AES-GCM/directional validation,
legacy peer compatibility, bounded frames/queues, malformed payloads, Unicode
text/names, size/count/path limits, SHA-256, cancellation, negative acknowledgments,
cache retention and recovery after failed partial-file deletion. A complete
25 MiB file transfer through real loopback peers preserves mouse-packet delivery;
service tests use separate STA workers and fake clipboard adapters, including
disable/re-enable, reconnect, replacement of unfinished copies and echo suppression.
The tests never read or replace the user's clipboard. Symlink creation is skipped
when Windows does not grant the required privilege; junction rejection also has
a dedicated fixture.

Keyboard suppression regressions pass for all 18,632 distinct source/target pairs
in the 137-key catalog, plus 13,312 OEM deletion/modifier cases and reentrant
press/release/configuration/failure scenarios. A separate native Windows run
passes 1,849 cases, observing the end of the hook chain so an original printable
source leaking alongside its remapped destination causes a failure. Its own sink
intercepts all test input before it can reach a user's window, including injected
Shift/Ctrl/Alt/AltGr/Windows combinations and simulated destination failures.
Run it with `dotnet run --project tests/Pairing.UiTests/Pairing.UiTests.csproj -c Release -- --native-keyboard-suppression-only`.
The standard UI suite also passes after the keyboard changes. Physical use of the
affected keyboard/layout remains part of acceptance for the next installed build.

Inno Setup compiles the candidate installer. The installer smoke script now checks
fresh default opt-out, explicit shortcut opt-in, deleted shortcuts staying absent
across manual/app updates and preservation of existing/custom shortcuts. Actual
installation/update/uninstall checks passed in the disposable CI account in
[the complete pre-release build](https://github.com/aesgalexis/mouse-without-borders/actions/runs/40112600772).
The candidate has not been installed over the user's running application. The
versioned release pipeline repeats these checks before publishing 0.1.10.

Physical acceptance remains pending: install the candidate on two PCs, enable
clipboard sharing on both, copy/paste text and file batches in both directions,
check over-limit/folder rejection, repeat copies during mouse crossings and
disconnect while a transfer is active. Verify native Explorer paste after MWB exits.

## Version 0.1.9 English-interface verification (2026-10-24)

Release compilation passed without warnings or errors. Keyboard smoke tests and
the complete UI suite passed. The remapping editor and notification-area menu
were rendered and visually inspected with English labels, help, preset and save
controls. A source scan found no remaining Spanish application-authored strings
in the application or current installation instructions. OEM keys show their
layout's printable symbols instead of localized Windows descriptive names.

## Version 0.1.8 receiver and focus regressions (2026-10-24)

The independent mouse-input loop is tested with the application's UI thread
deliberately blocked, repeated creation/disposal and late packets after shutdown.
Session tests cover reentrant down/up/cleanup, release failure recovery, a fresh
click after return and all five buttons already held when the session starts.
Cursor tests use an injected monotonic clock to check missing acknowledgements,
expired tag rejection, new placement retries and 32-bit tag delivery. A real
authenticated loopback peer stops reading to verify bounded outgoing queues.

Run `dotnet run --project tests/Pairing.UiTests/Pairing.UiTests.csproj -c Release -- --native-robustness-only`
on an interactive Windows desktop to minimize an owned MWB test window via the
remote-input path, then check repeated handoffs and foreground focus while the
main UI thread is blocked. The test intercepts input on a separate thread: its
single injected click is allowed only on the owned test window, foreign pointer
motion is suppressed for the short check, and cursor position is restored after
cleanup. It cannot click a user's window. The standard UI suite also runs the
blocked-UI lifecycle regression without moving/clicking the desktop pointer.

Local verification passed: Release compilation with zero warnings/errors, all
transport/backpressure, cursor, button, updater, keyboard and UI suites, plus
the native receiver-minimize/focus regression. The native test was run with MWB
and the PowerToys Mouse Without Borders module temporarily closed (with user
approval) and restored afterward: their active global hooks had rejected the
test's generated input. Running two mouse-sharing programs simultaneously can
interfere with handoffs and input; use one during physical acceptance testing.

Manual two-PC acceptance: minimize and restore MWB on both PCs while remotely
controlling them; click and drag other windows, use tray/menu actions, repeat
crossings in both directions and verify the foreground app does not change
until clicked. Reconnect with a local button held, unplug/reconnect the network
and confirm buttons/cursors recover. These hardware checks remain pending.

## Version 0.1.6 release verification (2026-10-24)

The Windows x64 Release build passed locally with zero warnings and errors.
Self-contained single-file publication and all transport/input, updater, keyboard
and pairing UI suites passed, including the new automatic-startup/retry and
administrator-restart checks. The pairing and remapping screens were captured;
the main window's administrator action and help text were visually inspected.
The installer and portable ZIP are generated with SHA-256 checksums. GitHub
Actions also runs the install/upgrade/uninstall suite in its disposable account
before publishing v0.1.6. Physical two-PC use, actual UAC and sign-out/reboot
remain manual acceptance checks.

## Version 0.1.7 return-position regression (2026-10-24)

The previous hook accepted every MWB-generated cursor placement, including an
old central parking placement delivered after a return. The new cursor-motion
guard assigns ownership tags, rejects placements from a previous owner and
suppresses old-anchor input until the new placement reaches the Windows hook.
The complete tag fits in 32 bits because the local native test observed Windows
input delivery discarding the upper half of a 64-bit tag.

Transport/input tests exercise delayed parking versus return, the return edge
and vertical position for both PCs and layouts, queued remote movement after
local takeover, and movement ordering within the same ownership. The Windows
native replay verifies that the former unguarded behavior reproduces a centre
jump, then verifies stale parking/input rejection and resumed local movement
for both shared edges. Run it alone with
`dotnet run --project tests/Pairing.UiTests/Pairing.UiTests.csproj -c Release -- --native-return-only`.
It injects no buttons and restores the original cursor position.

Local Release compilation passed without warnings or errors. All transport,
updater, keyboard and UI suites passed, as did the full `--native-pointer` check
(return positioning, pointer event deltas/suppression and hidden-cursor cleanup).
Actual repeated returns across two physical PCs remain a manual acceptance check.

## Administrator windows

The pairing UI suite covers the explicit administrator restart, cancellation of
UAC without stopping the listener, launch/settings-save failures, successful exit
from tray mode, elevated status and resuming a listener with AutoConnect disabled.
Elevation is replaced with a callback: automated tests do not request UAC.
Run just these checks with `dotnet run --project tests/Pairing.UiTests/Pairing.UiTests.csproj -c Release -- --elevation-only`.
Local verification on 2026-10-24: Release compilation and these focused UI checks
passed, as did the transport/input smoke suite. The administrator action and help
text were also rendered and inspected. This does not confirm physical-PC behavior.

Manual two-PC check: on the receiving desktop, open Task Manager and check hover,
Close and Minimize using the remote mouse. If blocked in standard mode, use
**Restart as administrator...** on that PC and approve UAC with its local device.
Verify reconnection, hover, Close and Minimize, then local takeover and reverse
handoff. Also cancel UAC once and confirm sharing stays connected. UAC/locked
screens require a local device. These physical checks remain pending.

## Connection and input regression checks

Run `dotnet run --project tests/MousePeer.SmokeTests/MousePeer.SmokeTests.csproj -c Release`.
The suite also checks a receiver that never sends its challenge, a receiver that
stops after receiving the pairing proof, cancellation and socket cleanup. Input
tests use a fake native sender to verify event layout, rejected input with and
without a Windows error code, and continued cleanup after a failed button release.
These checks do not inject mouse events into the desktop.

Pairing checks cover generated 8-character codes, case/spacing normalization,
invalid input, migration of existing local keys without changing connection
preferences, legacy 32-character keys, and authenticated round trips with both
key formats.

Startup UI checks cover the enabled defaults, compatibility with older settings,
persisted opt-outs, startup-registration failures and checkbox recovery, hidden
automatic listening at sign-in, and incomplete saved pairing. A loopback test
starts the receiver after the first connection failure and verifies that the
hidden connecting app retries automatically. Authentication is left pending so
this check does not install desktop mouse hooks. Registry changes are replaced
with callbacks; the tests do not change the user's Windows startup entry.
These checks passed locally on 2026-10-24, alongside the Release build and the
pairing, updater and keyboard UI checks. Actual sign-out/reboot and physical
two-PC interaction remain manual checks.

Run `dotnet run --project tests/Pairing.UiTests/Pairing.UiTests.csproj -c Release`
on Windows to check that both roles are visible, the receiver offers a direct
enter-code action, the connecting role exposes editable IP/code fields, entered
details survive role changes, and the action stops an active listener before
switching roles. Settings writes are replaced with an in-memory callback, and
the test listener uses an available loopback port so an installed app can stay open.

Local verification on 2026-10-23: Windows Release build, self-contained x64
publish, all transport/input smoke tests (including short and legacy keys),
and the pairing UI checks passed. Both setup screens were rendered and inspected.
This does not replace the physical two-PC checks below.

## Installer upgrade regression checks

Run `dotnet run --project tests/Updater.SmokeTests/Updater.SmokeTests.csproj -c Release`
to check numeric version selection, preview inclusion, pagination, missing assets,
GitHub errors/rate limits, URL validation, SHA-256 verification, download progress,
cancellation and failed-download cleanup. Installer launch is replaced with a
callback so these tests never execute a downloaded program. Add `-- --live` for
a read-only check against the real GitHub release list; add `--download` to also
download and verify the latest installer, with process launch suppressed and
temporary files removed afterward.

The pairing UI suite also checks manual-only updates, displayed version, duplicate
request prevention, retry after failure, preserving the listener when an update
fails, exiting after successful handoff even in tray mode, and cancellation on exit.

The next installer is checked with MWB open as a window, hidden in the
notification area, and running the original v0.1.2 executable. Current builds
must exit cleanly; v0.1.2 exercises the fallback for an older app that does not
understand the installer's exit request. Uninstall is also checked with MWB in
the tray. `tests/Installer.SmokeTests.ps1` performs these checks in the disposable
GitHub Actions account; it refuses to overwrite a different local installation.

The UI tests also send the installer exit message to a hidden window and verify
that the app exits instead of remaining in the notification area.
The installer smoke suite also passes `/MWBUPDATE=1` and verifies that the old
tray process exits and the updated installed executable opens automatically.

## Two-way input regression checks

The transport suite now simulates paired desktops on both left/right layouts.
It checks crossings and returns from both PCs, physical-mouse takeover,
simultaneous crossings, generation-stamped late packets, local held buttons,
remote held-button cleanup, and repeated edge crossings without any injected
button event. Empty cleanup and unmatched right-button releases produce no input.
The TCP checks preserve handoff IDs and origin fields in both directions and
reject an older protocol with an update-both-PCs message.

Windows input emitted by MWB is tagged and filtered so it cannot feed back to the
other PC. Other Windows pointer events remain available to mice and touchpads.
An optional native check can be run on an interactive Windows desktop:
`dotnet run --project tests/Pairing.UiTests/Pairing.UiTests.csproj -c Release -- --native-pointer`.
It sends three tagged test movements of two pixels through a real hook, verifies
the pointer deltas and suppression, restores the original cursor position and
does not press any buttons. This native check passed locally on 2026-10-23.
Physical USB/Bluetooth/touchpad use across two updated PCs still needs acceptance
testing; the simulator and native check do not replace that hardware test.

Local verification on 2026-10-23: Inno Setup 6.7.3 compiled the candidate;
updates from v0.1.2 in the tray and from the candidate both visible and hidden
passed. The older build exercised forced termination; the candidate exited
cleanly in both cases. The published v0.1.2 executable and original settings were
restored afterward, with the pairing code unchanged.

## Physical two-PC acceptance — not yet performed

- [ ] Setup.exe installs on PC A and PC B
- [ ] App appears in Start menu and tray; uninstaller removes it
- [ ] IP/key pairing connects only with correct key
- [ ] Both PCs initially show a code; the mouse PC's enter-code action opens the IP/code fields
- [ ] After upgrading an existing receiver, its new 8-character code pairs and persists after restart
- [ ] Moving right across PC A outer edge reaches PC B
- [ ] Moving left across PC B edge returns to PC A
- [ ] Crossings and returns also work with PC B's own mouse, without a second connection
- [ ] Built-in laptop touchpads cross, click, scroll and return on either PC
- [ ] Repeated crossings with no buttons pressed do not open desktop context menus
- [ ] A local mouse takes control from remote input, including simultaneous movements/crossings
- [ ] Primary, secondary, middle and X button clicks reach PC B
- [ ] Vertical and horizontal wheel reach PC B
- [ ] With inverted layout, left crossing from PC A reaches PC B and returns through right
- [ ] Cursor does not get stuck on PC A after receiver power-off / Wi-Fi loss
- [ ] Auto-connect succeeds when PC A starts before PC B
- [ ] Closing window keeps app in notification area; Exit ends process and releases cursor
- [ ] Start with Windows and autoconnect persist across sign-out/reboot
- [ ] Two monitors on one PC, different resolutions, and DPI scaling behave acceptably
- [ ] Windows Firewall rule only allows private LAN; uninstall removes optional rule
- [ ] No unintended keyboard, clipboard or file-sharing functionality
- [ ] UAC/secure desktop limitations communicated accurately
- [ ] A Windows input rejection appears in receiver status/logs and returns the controller's cursor

## Bug reports

File an issue at https://github.com/aesgalexis/mouse-without-borders/issues containing:

1. App version on both PCs.
2. Windows version on both PCs, physical screen arrangement and monitor resolution.
3. Whether mouse clicks, movement or wheel failed (and in which direction).
4. Private LAN network details **without revealing public IPs, pairing keys or other secrets**.
5. Steps to reproduce and whether disconnect/reconnect recovered.

Attach sanitized diagnostic logs only when necessary. Do not commit personal pairing details, IP addresses from your home network, or log files.

## Version 0.1.4 visual and cursor checks

Session tests verify pointer visibility in both crossing directions, on returning,
local takeover, simultaneous crossings and disposal. The optional interactive
`--native-pointer` UI check now also verifies the actual transparent Windows cursor,
foreground preservation, and cursor restoration after disposing the parking window.
The native check passed locally on 2026-10-23. No system cursor scheme is modified.
UI captures cover both pairing modes and the dark tray menu. The installer smoke
suite creates a custom-named legacy desktop shortcut and checks its icon is migrated
to the installed versioned ICO during every upgrade case.
## Version 0.1.5 local keyboard remapping

Keyboard.SmokeTests covers selection validation, JSON persistence, Enter/Backspace,
repeat, swaps, shared destinations, real target-key collisions, disabling and
cleanup, including a failed release. UI tests configure both keys using only
selectors/buttons, reject duplicate inputs, edit/remove, save/cancel, restart and
verify independence from the mouse connection. The optional `--native-keyboard`
UI test exercises the real global keyboard hook and Windows SendInput; a separate
test hook intercepts all generated Enter/Backspace events before user windows.
This native check passed locally on 2026-10-23. No typed text is logged.
