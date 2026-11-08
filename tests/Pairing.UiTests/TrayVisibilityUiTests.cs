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
        TestControllerConnect();
        TestConnectionIcon();
        TestSharingReadinessTimeout();
        TestControllerReadiness(acknowledge: false);
        TestControllerReadiness(acknowledge: true);
        TestStoppedPairingCannotChangeNewListener();
        TestUnavailableController();
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
        ToolStripMenuItem connect = TrayItem(tray, "TrayConnect");
        ToolStripMenuItem disconnect = TrayItem(tray, "TrayDisconnect");
        ToolStripMenuItem cancel = TrayItem(tray, "TrayCancelConnection");
        Button toggle = Find<Button>(window, "ToggleConnection");
        Label status = Find<Label>(window, "ConnectionStatus");
        Icon disconnected = tray.Icon!;
        Assert(tray.Visible && tray.Text.EndsWith("Disconnected", StringComparison.Ordinal),
            "The tray must report that MWB is disconnected at startup.");
        Assert(connect.Text == "Connect" && connect.Enabled && disconnect.Text == "Disconnect" && !disconnect.Enabled && !cancel.Available,
            "The idle tray menu must offer Connect and disable Disconnect.");
        window.Close();
        connect.PerformClick();
        Assert(!window.Visible && ReferenceEquals(tray.Icon, disconnected) && !connect.Enabled && !disconnect.Enabled &&
            cancel.Available && cancel.Text == "Stop listening" && toggle.Text == "Stop listening" &&
            tray.Text.EndsWith("Waiting for other PC", StringComparison.Ordinal),
            "Window and tray must show a waiting receiver without claiming a connection.");

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
            PumpUntil(() => tray.Text.EndsWith("Preparing mouse sharing", StringComparison.Ordinal));
            Assert(ReferenceEquals(tray.Icon, disconnected) && !disconnect.Enabled && cancel.Available &&
                toggle.Text == "Stop listening" && !status.Text.StartsWith("Connected", StringComparison.Ordinal),
                "Authentication alone must not advertise a usable mouse connection.");
            Assert(peer.Queue(new MousePacket("layout", Data: 1)), "Could not send the peer's screen layout.");
            PumpUntil(() => tray.Text.EndsWith("Connected", StringComparison.Ordinal));
            Assert(!connect.Enabled && disconnect.Enabled && !cancel.Available && !window.Visible && toggle.Text == "Disconnect" &&
                status.Text.StartsWith("Connected", StringComparison.Ordinal),
                "Confirmed mouse readiness must update both window and tray while the window is hidden.");
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
            Assert(coloredPixel, "A ready mouse connection must restore the blue tray icon.");
            window.Show();
            window.Close();
            Assert(!window.Visible && tray.Visible && disconnect.Enabled,
                "Closing the window must retain the connected tray icon and its Disconnect action.");
        }
        PumpUntil(() => ReferenceEquals(tray.Icon, disconnected));
        Assert(!connect.Enabled && !disconnect.Enabled && cancel.Available && toggle.Text == "Stop listening" &&
            tray.Text.EndsWith("Waiting for other PC", StringComparison.Ordinal),
            "Losing the peer must disable Disconnect and offer to stop the receiver that is still listening.");
        using (MousePeer peer = Connect())
        {
            PumpUntil(() => tray.Text.EndsWith("Preparing mouse sharing", StringComparison.Ordinal));
            Assert(ReferenceEquals(tray.Icon, disconnected) && !disconnect.Enabled,
                "A new session must obtain fresh readiness instead of inheriting the previous connection's state.");
            Assert(peer.Queue(new MousePacket("layout", Data: 1)), "Could not send the reconnecting peer's layout.");
            PumpUntil(() => tray.Text.EndsWith("Connected", StringComparison.Ordinal));
            disconnect.PerformClick();
            Assert(tray.Visible && ReferenceEquals(tray.Icon, disconnected) && connect.Enabled && !disconnect.Enabled && !cancel.Available,
                "Disconnect must stop sharing, restore Connect and turn the icon gray immediately.");
        }
        Console.WriteLine("PASS: window and tray stay disconnected until mouse readiness, follow peer loss and require fresh readiness on reconnect.");
    }

    private static void TestSharingReadinessTimeout()
    {
        AppSettings settings = new() { AutoConnect = false };
        using MainWindow window = Window(settings, _ => { }, () => TrayVisibilityResult.Applied,
            () => new TcpListener(IPAddress.Loopback, MousePeer.Port));
        NotifyIcon tray = GetTray(window);
        Icon disconnected = tray.Icon!;
        TrayItem(tray, "TrayConnect").PerformClick();
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(12));
        Task<MousePeer> connecting = Task.Run(() => MousePeer.ConnectAsync(
            "127.0.0.1", MousePeer.ParseKey(settings.PairingKey), timeout.Token));
        PumpUntil(() => connecting.IsCompleted);
        using MousePeer peer = connecting.GetAwaiter().GetResult();
        PumpUntil(() => tray.Text.EndsWith("Preparing mouse sharing", StringComparison.Ordinal));
        // MousePeer keeps sending heartbeats, but never starts a sharing session.
        PumpUntil(() => tray.Text.EndsWith("Waiting for other PC", StringComparison.Ordinal), seconds: 8);
        Assert(ReferenceEquals(tray.Icon, disconnected) && !TrayItem(tray, "TrayDisconnect").Enabled &&
            Find<Label>(window, "ConnectionStatus").Text.Contains("mouse sharing", StringComparison.OrdinalIgnoreCase),
            "A responsive transport without mouse readiness must time out with a useful error and resume listening.");
        TrayItem(tray, "TrayCancelConnection").PerformClick();
        Console.WriteLine("PASS: heartbeats cannot keep an unready mouse session falsely connected forever.");
    }

    private static void TestControllerReadiness(bool acknowledge)
    {
        string code = MousePeer.NewKey();
        using TcpListener receiver = new(IPAddress.Loopback, MousePeer.Port);
        receiver.Start();
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(15));
        Task<MousePeer> accepting = Task.Run(async () =>
        {
            TcpClient client = await receiver.AcceptTcpClientAsync(timeout.Token);
            return await MousePeer.AcceptAsync(client, MousePeer.ParseKey(code), timeout.Token);
        });
        using MainWindow window = Window(new()
        {
            Role = "Controller", AutoConnect = false, Host = "127.0.0.1", RemoteKey = code
        }, _ => { }, () => TrayVisibilityResult.Applied);
        NotifyIcon tray = GetTray(window);
        Icon disconnected = tray.Icon!;
        TrayItem(tray, "TrayConnect").PerformClick();
        PumpUntil(() => accepting.IsCompleted);
        using MousePeer peer = accepting.GetAwaiter().GetResult();
        TaskCompletionSource<MousePacket> layout = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task reading = peer.ReadLoopAsync(packet =>
        {
            if (packet.Kind == "layout") layout.TrySetResult(packet);
        }, timeout.Token);
        try
        {
            PumpUntil(() => layout.Task.IsCompleted && tray.Text.EndsWith("Preparing mouse sharing", StringComparison.Ordinal));
            Assert(!reading.IsCompleted && ReferenceEquals(tray.Icon, disconnected) &&
                !TrayItem(tray, "TrayDisconnect").Enabled &&
                Find<Button>(window, "ToggleConnection").Text == "Cancel connection",
                "A controller must remain cancellable and disconnected until the receiver confirms mouse readiness.");
            if (acknowledge)
            {
                Assert(peer.Queue(new MousePacket("ready", Data: layout.Task.Result.Data)),
                    "The receiver could not acknowledge the controller's layout.");
                PumpUntil(() => tray.Text.EndsWith("Connected", StringComparison.Ordinal));
                Assert(!reading.IsCompleted && TrayItem(tray, "TrayDisconnect").Enabled &&
                    Find<Button>(window, "ToggleConnection").Text == "Disconnect" &&
                    Find<Label>(window, "ConnectionStatus").Text.StartsWith("Connected", StringComparison.Ordinal),
                    "The receiver's confirmation must enable the same connected state in the controller window and tray.");
                peer.Dispose();
            }
            // Withhold readiness while keeping the fake receiver's heartbeat and reader
            // alive, or close an established session after a positive acknowledgement.
            PumpUntil(() => TrayItem(tray, "TrayConnect").Enabled, seconds: 8);
            string status = Find<Label>(window, "ConnectionStatus").Text;
            Assert(ReferenceEquals(tray.Icon, disconnected) && tray.Text.EndsWith("Disconnected", StringComparison.Ordinal) &&
                Find<Button>(window, "ToggleConnection").Text == "Connect mouse" &&
                !TrayItem(tray, "TrayDisconnect").Enabled && !TrayItem(tray, "TrayCancelConnection").Available,
                "Both readiness failure and peer loss must return to idle when automatic reconnect is disabled.");
            Assert(acknowledge ? status.Contains("Open MWB", StringComparison.Ordinal) :
                status.Contains("confirm mouse sharing", StringComparison.Ordinal) &&
                status.Contains("both PCs", StringComparison.Ordinal),
                "The controller must explain unavailable peers or the missing readiness confirmation.");
        }
        finally
        {
            peer.Dispose();
            PumpUntil(() => reading.IsCompleted);
            try { reading.GetAwaiter().GetResult(); }
            catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException) { }
        }
        Console.WriteLine(acknowledge
            ? "PASS: receiver readiness enables controller sharing; peer loss respects disabled automatic reconnection."
            : "PASS: a legacy receiver with working heartbeats cannot bypass the controller's readiness timeout.");
    }

    private static void TestStoppedPairingCannotChangeNewListener()
    {
        using MainWindow window = Window(new() { AutoConnect = false }, _ => { }, () => TrayVisibilityResult.Applied,
            () => new TcpListener(IPAddress.Loopback, MousePeer.Port));
        NotifyIcon tray = GetTray(window);
        TrayItem(tray, "TrayConnect").PerformClick();
        using TcpClient unfinished = new();
        unfinished.Connect(IPAddress.Loopback, MousePeer.Port);
        PumpUntil(() => unfinished.Available > 0);
        TrayItem(tray, "TrayCancelConnection").PerformClick();
        TrayItem(tray, "TrayConnect").PerformClick();
        string expectedStatus = Find<Label>(window, "ConnectionStatus").Text;
        DateTime deadline = DateTime.UtcNow.AddMilliseconds(300);
        while (DateTime.UtcNow < deadline) { Application.DoEvents(); Thread.Sleep(10); }
        Assert(Find<Label>(window, "ConnectionStatus").Text == expectedStatus &&
            tray.Text.EndsWith("Waiting for other PC", StringComparison.Ordinal),
            "A cancelled authentication callback must not overwrite the restarted listener's state.");
        TrayItem(tray, "TrayCancelConnection").PerformClick();
    }

    private static void TestUnavailableController()
    {
        // Prove the fixed test port is free before attempting a refused connection.
        using TcpListener reservation = new(IPAddress.Loopback, MousePeer.Port);
        reservation.Start();
        reservation.Stop();
        using MainWindow window = Window(new()
        {
            Role = "Controller", AutoConnect = false, Host = "127.0.0.1", RemoteKey = MousePeer.NewKey()
        }, _ => { }, () => TrayVisibilityResult.Applied);
        NotifyIcon tray = GetTray(window);
        TrayItem(tray, "TrayConnect").PerformClick();
        PumpUntil(() => TrayItem(tray, "TrayConnect").Enabled);
        Assert(tray.Text.EndsWith("Disconnected", StringComparison.Ordinal) &&
            Find<Button>(window, "ToggleConnection").Text == "Connect mouse" &&
            Find<Label>(window, "ConnectionStatus").Text.Contains("Open MWB", StringComparison.Ordinal),
            "An unavailable remote with retries disabled must leave both controls idle and explain how to start the other PC.");
    }

    private static void TestControllerConnect()
    {
        using MainWindow window = Window(new() { Role = "Controller", AutoConnect = false, MinimizeToTray = true },
            _ => { }, () => TrayVisibilityResult.Applied);
        NotifyIcon tray = GetTray(window);
        ToolStripMenuItem connect = TrayItem(tray, "TrayConnect");
        ToolStripMenuItem disconnect = TrayItem(tray, "TrayDisconnect");
        ToolStripMenuItem cancel = TrayItem(tray, "TrayCancelConnection");
        TextBox host = window.Controls.Find("ReceiverHost", true).OfType<TextBox>().Single();
        TextBox code = window.Controls.Find("RemotePairingCode", true).OfType<TextBox>().Single();

        window.Close();
        connect.PerformClick();
        Assert(window.Visible && host.Focused && connect.Enabled && !disconnect.Enabled,
            "Connect must open the setup and focus the missing address.");
        host.Text = "127.0.0.1";
        window.Close();
        connect.PerformClick();
        Assert(window.Visible && code.Focused && connect.Enabled && !disconnect.Enabled,
            "Connect must focus the pairing code when the address is already set.");

        using TcpListener receiver = new(IPAddress.Loopback, MousePeer.Port);
        receiver.Start();
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
        Task<TcpClient> accepting = receiver.AcceptTcpClientAsync(timeout.Token).AsTask();
        code.Text = MousePeer.NewKey();
        window.Close();
        connect.PerformClick();
        PumpUntil(() => accepting.IsCompleted);
        using TcpClient client = accepting.GetAwaiter().GetResult();
        Assert(!window.Visible && !connect.Enabled && !disconnect.Enabled && cancel.Available && cancel.Text == "Cancel connection",
            "Connect must use the saved address and offer to cancel before pairing completes.");
        cancel.PerformClick();
        Assert(connect.Enabled && !disconnect.Enabled && !cancel.Available,
            "Cancelling an attempt must return the tray menu to its idle state.");
        Console.WriteLine("PASS: tray Connect guides incomplete controller setup, connects with saved details and supports cancelling a pending attempt.");
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

    private static ToolStripMenuItem VisibilityItem(MainWindow window) => TrayItem(GetTray(window), "KeepTrayIconVisible");

    private static T Find<T>(Control root, string name) where T : Control => root.Controls.Find(name, true).OfType<T>().Single();

    private static ToolStripMenuItem TrayItem(NotifyIcon tray, string name) =>
        tray.ContextMenuStrip!.Items.OfType<ToolStripMenuItem>().Single(item => item.Name == name);

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

    private static void PumpUntil(Func<bool> condition, int seconds = 5)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (!condition() && DateTime.UtcNow < deadline) { Application.DoEvents(); Thread.Sleep(10); }
        Assert(condition(), "Tray visibility maintenance did not reach the expected state.");
    }

    private static void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }
}
