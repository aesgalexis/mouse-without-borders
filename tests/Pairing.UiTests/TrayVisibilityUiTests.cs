using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text.Json;

namespace MouseWithOutBorders;

internal static class TrayVisibilityUiTests
{
    // Explicit opt-in: this changes only this test executable's Explorer preference,
    // then restores it. No startup registration, app settings or input hooks are used.
    internal static void RunNative()
    {
        Assert(OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000), "Native promotion requires Windows 11.");
        using MainWindow window = Window(new() { AutoConnect = false, MinimizeToTray = true }, _ => { },
            () => TrayVisibilityResult.Unsupported);
        NotifyIcon tray = GetTray(window);
        NativeWindow native = (NativeWindow)typeof(NotifyIcon).GetField("_window", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(tray)!;
        uint id = (uint)typeof(NotifyIcon).GetField("_id", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(tray)!;
        IconIdentifier identifier = new() { Size = System.Runtime.InteropServices.Marshal.SizeOf<IconIdentifier>(), Window = native.Handle, Id = id };
        using Microsoft.Win32.RegistryKey root = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Control Panel\NotifyIconSettings")!;
        string? name = null;
        PumpUntil(() =>
        {
            name = root.GetSubKeyNames().FirstOrDefault(candidate =>
            {
                using var key = root.OpenSubKey(candidate);
                return string.Equals(key?.GetValue("ExecutablePath") as string, Environment.ProcessPath, StringComparison.OrdinalIgnoreCase) &&
                    key?.GetValue("UID") is int uid && (uint)uid == id;
            });
            return name is not null;
        });
        using var own = root.OpenSubKey(name!, writable: true)!;
        List<(string Name, object? Value)> previous = [];
        foreach (string candidate in root.GetSubKeyNames())
        {
            using var key = root.OpenSubKey(candidate);
            if (string.Equals(key?.GetValue("ExecutablePath") as string, Environment.ProcessPath, StringComparison.OrdinalIgnoreCase))
                previous.Add((candidate, key?.GetValue("IsPromoted")));
        }
        try
        {
            // GetRect can report the overflow chevron for a hidden icon. It
            // verifies registration, not whether Explorer actually drew the icon.
            bool IsRegistered() => Shell_NotifyIconGetRect(ref identifier, out NativeRect icon) >= 0 &&
                icon.Right > icon.Left && icon.Bottom > icon.Top;
            using TrayIconVisibility visibility = new(WindowsTrayIconVisibility.TryPromote, error => throw new Exception(error),
                () => { tray.Visible = false; tray.Visible = true; });
            own.SetValue("IsPromoted", 0, Microsoft.Win32.RegistryValueKind.DWord);
            visibility.Enabled = true;
            PumpUntil(IsRegistered);
            Assert(own.GetValue("IsPromoted") is int value && value == 1,
                "Maintenance must restore the preference after the shell hides the icon.");
            window.Close();
            Assert(!window.Visible && !window.IsDisposed && Shell_NotifyIconGetRect(ref identifier, out _) == 0,
                "Closing the main window must keep its real notification icon registered.");
            Console.WriteLine("PASS: actual Windows 11 visibility preference, icon re-registration and close-to-tray retention; previous test-icon preference restored. Visual placement remains a manual check.");
        }
        finally
        {
            foreach (var entry in previous)
            {
                using var key = root.OpenSubKey(entry.Name, writable: true);
                if (key is null) continue;
                if (entry.Value is null) key.DeleteValue("IsPromoted", throwOnMissingValue: false);
                else key.SetValue("IsPromoted", entry.Value, Microsoft.Win32.RegistryValueKind.DWord);
            }
        }
    }

    internal static void Run()
    {
        TestOwnIconSelection();
        TestChangedRefresh();
        TestRecovery();
        TestPreferences();
        TestSaveFailure();
        TestConnectionIcon();
        Console.WriteLine("PASS: MWB-only tray promotion, delayed registration, shell recreation, error recovery, persisted opt-out and disposal; no real tray settings changed.");
    }

    private static void TestChangedRefresh()
    {
        int refreshes = 0;
        TrayVisibilityResult result = TrayVisibilityResult.Changed;
        using TrayIconVisibility visibility = new(() => result, _ => { }, () => refreshes++);
        visibility.Enabled = true;
        Assert(refreshes == 1, "Changing the preference must refresh Explorer's cached icon placement.");
        result = TrayVisibilityResult.Applied;
        visibility.Refresh();
        Assert(refreshes == 1, "An already-promoted icon must not flicker or be re-registered on every check.");
    }

    private static void TestOwnIconSelection()
    {
        const string executable = @"C:\Apps\MWB\MouseWithOutBorders.exe";
        List<string> writes = [];
        WindowsTrayIconVisibility.IconSetting[] icons =
        [
            new("own", executable.ToUpperInvariant(), false),
            new("already-visible", executable, true),
            new("other-app", @"C:\Apps\Other.exe", false),
            new("other-copy", @"C:\Other\MouseWithOutBorders.exe", false),
            new("relative", "MouseWithOutBorders.exe", false),
            new("missing", null, false),
            new("invalid", "C:\\bad\0path", false)
        ];
        Assert(WindowsTrayIconVisibility.PromoteOwnIcons(executable, icons, writes.Add) == TrayVisibilityResult.Changed &&
            writes.SequenceEqual(new[] { "own" }),
            "Only exact executable paths may be promoted; already-visible icons must not be rewritten.");
        writes.Clear();
        Assert(WindowsTrayIconVisibility.PromoteOwnIcons(executable, icons.Skip(2), writes.Add) == TrayVisibilityResult.Pending &&
            writes.Count == 0, "A missing registration must be retried without creating keys or changing other applications.");
        string expanded = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MWB", "app.exe");
        Assert(WindowsTrayIconVisibility.PromoteOwnIcons(expanded,
            [new("expanded", @"%LOCALAPPDATA%\MWB\app.exe", false)], writes.Add) == TrayVisibilityResult.Changed &&
            writes.SequenceEqual(new[] { "expanded" }), "Explorer paths containing environment variables must match correctly.");
    }

    private static void TestRecovery()
    {
        int calls = 0;
        List<string> errors = [];
        bool fail = false;
        using TrayIconVisibility visibility = new(() =>
        {
            calls++;
            if (fail) throw new UnauthorizedAccessException("Registry denied");
            return calls == 1 ? TrayVisibilityResult.Pending : TrayVisibilityResult.Applied;
        }, errors.Add);
        visibility.Enabled = true;
        PumpUntil(() => calls == 2);
        Assert(errors.Count == 0, "Late Explorer registration must recover automatically.");
        visibility.RefreshSoon();
        PumpUntil(() => calls == 3);
        fail = true;
        visibility.Refresh();
        visibility.Refresh();
        Assert(errors.Count == 1, "A repeated registry error must not crash or flood diagnostics.");
        fail = false;
        visibility.Refresh();
        fail = true;
        visibility.Refresh();
        Assert(errors.Count == 2, "Recovery must allow a later error to be reported again.");
        visibility.Enabled = false;
        int stopped = calls;
        visibility.RefreshSoon();
        visibility.Refresh();
        Assert(calls == stopped, "An opted-out app must not keep enforcing the tray preference.");
        visibility.Dispose();
        visibility.Enabled = true;
        visibility.RefreshSoon();
        visibility.Refresh();
        Assert(calls == stopped, "Disposed visibility maintenance must never touch the shell again.");
    }

    private static void TestPreferences()
    {
        Assert(new AppSettings().KeepTrayIconVisible &&
            JsonSerializer.Deserialize<AppSettings>("{}")!.KeepTrayIconVisible,
            "Fresh and existing settings must enable icon visibility by default.");
        string? saved = null;
        int calls = 0;
        using (MainWindow window = Window(new() { AutoConnect = false, MinimizeToTray = true },
            settings => saved = JsonSerializer.Serialize(settings), () => { calls++; return TrayVisibilityResult.Applied; }))
        {
            ToolStripMenuItem item = VisibilityItem(window);
            Assert(item.Text == "Always show tray icon" && item.Checked && calls == 1,
                "The tray menu must show the visibility option and apply its saved preference after creating the icon.");
            window.Close();
            Assert(!window.Visible && !window.IsDisposed, "Closing to the tray must keep the process and maintenance alive.");
            uint taskbarCreated = NativeMouse.RegisterWindowMessage("TaskbarCreated");
            Assert(taskbarCreated != 0 && PostMessage(window.Handle, taskbarCreated, IntPtr.Zero, IntPtr.Zero),
                "Cannot simulate Explorer's taskbar-recreated message.");
            PumpUntil(() => calls == 2);
            Assert(!window.Visible, "Recovering the tray icon must not reopen or focus the main window.");
            item.PerformClick();
            Assert(!item.Checked && saved is not null && !JsonSerializer.Deserialize<AppSettings>(saved)!.KeepTrayIconVisible,
                "The tray option must persist an explicit opt-out.");
        }
        AppSettings restored = JsonSerializer.Deserialize<AppSettings>(saved!)!;
        using MainWindow restarted = Window(restored, _ => { }, () => { calls++; return TrayVisibilityResult.Applied; });
        Assert(!VisibilityItem(restarted).Checked && calls == 2,
            "Restarting an opted-out app must preserve Windows' control without reapplying promotion.");
        VisibilityItem(restarted).PerformClick();
        Assert(restored.KeepTrayIconVisible && calls == 3, "Re-enabling must promote immediately.");
    }

    private static void TestSaveFailure()
    {
        AppSettings settings = new() { AutoConnect = false, KeepTrayIconVisible = false };
        int calls = 0;
        using MainWindow window = Window(settings, _ => throw new IOException("Settings unavailable"),
            () => { calls++; return TrayVisibilityResult.Applied; });
        ToolStripMenuItem item = VisibilityItem(window);
        item.PerformClick();
        Assert(!item.Checked && !settings.KeepTrayIconVisible && calls == 0,
            "A failed save must restore the menu and leave the previous tray preference in effect.");
    }

    private static void TestConnectionIcon()
    {
        AppSettings settings = new() { AutoConnect = false, MinimizeToTray = true };
        using MainWindow window = Window(settings, _ => { }, () => TrayVisibilityResult.Applied,
            () => new TcpListener(IPAddress.Loopback, MousePeer.Port));
        NotifyIcon tray = GetTray(window);
        Icon disconnected = tray.Icon!;
        Assert(tray.Visible && tray.Text.EndsWith("Disconnected", StringComparison.Ordinal),
            "The tray must report that MWB is disconnected at startup.");
        window.Controls.Find("ToggleConnection", true).OfType<Button>().Single().PerformClick();
        Assert(ReferenceEquals(tray.Icon, disconnected), "Listening without a peer must keep the disconnected icon.");

        MousePeer Connect()
        {
            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
            Task<MousePeer> connecting = Task.Run(() => MousePeer.ConnectAsync(
                "127.0.0.1", MousePeer.ParseKey(settings.PairingKey), timeout.Token));
            PumpUntil(() => connecting.IsCompleted);
            return connecting.GetAwaiter().GetResult();
        }

        using (MousePeer peer = Connect())
        {
            PumpUntil(() => tray.Text.EndsWith("Connected", StringComparison.Ordinal));
            using Bitmap gray = disconnected.ToBitmap();
            using Bitmap blue = tray.Icon!.ToBitmap();
            bool coloredPixel = false;
            for (int y = 0; y < gray.Height; y++)
            {
                for (int x = 0; x < gray.Width; x++)
                {
                    Color inactive = gray.GetPixel(x, y);
                    Color active = blue.GetPixel(x, y);
                    Assert(inactive.A == active.A && inactive.R == inactive.G && inactive.G == inactive.B,
                        "The disconnected tray icon must be gray and preserve the original transparency.");
                    coloredPixel |= active.A > 0 && active.B > active.R;
                }
            }
            Assert(coloredPixel, "An authenticated connection must restore the blue tray icon.");
            window.Close();
            Assert(!window.Visible && tray.Visible, "Closing the window must retain the connected tray icon.");
        }
        PumpUntil(() => ReferenceEquals(tray.Icon, disconnected));
        using (MousePeer peer = Connect())
        {
            PumpUntil(() => tray.Text.EndsWith("Connected", StringComparison.Ordinal));
            tray.ContextMenuStrip!.Items.OfType<ToolStripMenuItem>().Single(item => item.Text == "Stop mouse sharing").PerformClick();
            Assert(tray.Visible && ReferenceEquals(tray.Icon, disconnected),
                "Stopping a connection from the tray must turn the icon gray immediately.");
        }
        Console.WriteLine("PASS: gray tray icon at startup and while listening, blue during authenticated sharing, and gray after disconnect or stop while hidden.");
    }

    private static MainWindow Window(AppSettings settings, Action<AppSettings> save, Func<TrayVisibilityResult> promote,
        Func<TcpListener>? listenerFactory = null)
    {
        MainWindow window = new(settings, save, listenerFactory: listenerFactory, promoteTrayIcon: promote)
        {
            ShowInTaskbar = false, StartPosition = FormStartPosition.Manual, Location = new Point(-20000, -20000)
        };
        window.Show();
        Application.DoEvents();
        return window;
    }

    private static ToolStripMenuItem VisibilityItem(MainWindow window)
    {
        NotifyIcon tray = GetTray(window);
        return tray.ContextMenuStrip!.Items.OfType<ToolStripMenuItem>().Single(item => item.Name == "KeepTrayIconVisible");
    }

    private static NotifyIcon GetTray(MainWindow window) => (NotifyIcon)typeof(MainWindow)
        .GetField("_tray", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct IconIdentifier { internal int Size; internal IntPtr Window; internal uint Id; internal Guid Guid; }
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct NativeRect { internal int Left, Top, Right, Bottom; }
    [System.Runtime.InteropServices.DllImport("shell32.dll", ExactSpelling = true)]
    private static extern int Shell_NotifyIconGetRect(ref IconIdentifier icon, out NativeRect rect);

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

    private static void PumpUntil(Func<bool> condition)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition() && DateTime.UtcNow < deadline) { Application.DoEvents(); Thread.Sleep(10); }
        Assert(condition(), "Tray visibility maintenance did not reach the expected state.");
    }

    private static void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }
}
