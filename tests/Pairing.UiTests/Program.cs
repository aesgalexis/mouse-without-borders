namespace MouseWithOutBorders;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        if (args.Contains("--input-recovery-only", StringComparer.Ordinal)) { InputRecoveryTests.Run(); return; }
        if (args.Contains("--robustness-only", StringComparer.Ordinal)) { RobustnessUiTests.Run(); return; }
        if (args.Contains("--native-robustness-only", StringComparer.Ordinal)) { RobustnessUiTests.RunNative(); return; }
        if (args.Contains("--native-return-only", StringComparer.Ordinal)) { CursorReturnUiTests.RunNative(); return; }
        if (args.Contains("--native-keyboard-suppression-only", StringComparer.Ordinal)) { KeyboardSuppressionTests.RunNative(); return; }
        if (args.Contains("--elevation-only", StringComparer.Ordinal)) { ElevationUiTests.Run(); return; }
        if (args.Contains("--clipboard-only", StringComparer.Ordinal)) { ClipboardUiTests.Run(); return; }
        if (args.Contains("--cursor-only", StringComparer.Ordinal)) { TestPointerVisibility(); return; }
        AppSettings settings = new() { MinimizeToTray = false, AutoConnect = false };
        int saves = 0;
        using MainWindow window = new(settings, _ => saves++, listenerFactory:
            () => new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0));
        window.ShowInTaskbar = false;
        window.StartPosition = FormStartPosition.Manual;
        window.Location = new Point(-20000, -20000);
        window.Show();
        Application.DoEvents();

        RadioButton controller = Find<RadioButton>(window, "ControllerRole");
        RadioButton receiver = Find<RadioButton>(window, "ReceiverRole");
        Button enterCode = Find<Button>(window, "EnterOtherPcCode");
        TextBox host = Find<TextBox>(window, "ReceiverHost");
        TextBox remoteCode = Find<TextBox>(window, "RemotePairingCode");
        TextBox localCode = Find<TextBox>(window, "LocalPairingCode");
        Button toggle = Find<Button>(window, "ToggleConnection");

        if (!controller.Visible || !receiver.Visible || !receiver.Checked || !enterCode.Visible ||
            !localCode.Visible || localCode.Text.Length != 8 || !localCode.ReadOnly || remoteCode.Visible)
            throw new Exception("Receiver must show both role choices, a short code and an obvious enter-code action.");
        SaveScreenshot(window, args, "receiver.png");

        enterCode.PerformClick();
        if (!controller.Checked || !host.Visible || !remoteCode.Visible || remoteCode.ReadOnly ||
            localCode.Visible || enterCode.Visible || settings.Role != "Controller" || !host.Focused)
            throw new Exception("Enter-code action did not reveal editable connection fields and focus the IP.");
        SaveScreenshot(window, args, "controller.png");

        host.Text = "127.0.0.1";
        remoteCode.Text = "abcd23xy";
        receiver.Checked = true;
        if (settings.RemoteKey != "ABCD23XY" || settings.Host != "127.0.0.1")
            throw new Exception("Switching roles lost the entered connection details.");
        enterCode.PerformClick();
        if (!remoteCode.Focused || remoteCode.Text != "ABCD23XY")
            throw new Exception("Returning to connect must preserve and focus the code when an IP is already entered.");

        receiver.Checked = true;
        toggle.PerformClick();
        if (controller.Enabled || receiver.Enabled || !enterCode.Enabled)
            throw new Exception("An active receiver must still offer the enter-code action. Status: " +
                Find<Label>(window, "ConnectionStatus").Text);
        enterCode.PerformClick();
        if (!controller.Checked || !controller.Enabled || !receiver.Enabled || !remoteCode.Visible ||
            toggle.Text != "Connect mouse")
            throw new Exception("Enter-code action must stop listening and switch to the connecting role.");

        Find<CheckBox>(window, "MinimizeToTray").Checked = true;
        window.Close();
        if (window.IsDisposed || window.Visible)
            throw new Exception("Closing normally should keep the app running in the notification area.");
        uint exitMessage = NativeMouse.RegisterWindowMessage("MouseWithOutBorders.ExitForUpdate.79DBA1BD");
        if (exitMessage == 0 || !PostMessage(window.Handle, exitMessage, IntPtr.Zero, IntPtr.Zero))
            throw new Exception("Could not send the installer's exit request.");
        DateTime exitDeadline = DateTime.UtcNow.AddSeconds(5);
        while (!window.IsDisposed && DateTime.UtcNow < exitDeadline)
        {
            Application.DoEvents();
            Thread.Sleep(10);
        }
        if (!window.IsDisposed)
            throw new Exception("The installer's exit request must exit MWB even when it is hidden in the tray.");
        if (saves == 0) throw new Exception("Pairing preferences were not saved.");
        Console.WriteLine("PASS: receiver, enter-code action, editable code, role persistence and switching while listening.");
        Console.WriteLine("PASS: installer exit request closes MWB from the notification area.");
        UpdaterUiTests.Run();
        StartupUiTests.Run();
        ElevationUiTests.Run();
        RobustnessUiTests.Run();
        InputRecoveryTests.Run();
        KeyboardUiTests.Run(args.Length == 2 && args[0] == "--screenshots" ? args[1] : null);
        ClipboardUiTests.Run();
        if (args.Contains("--native-keyboard", StringComparer.Ordinal)) KeyboardUiTests.RunNative();
        if (args.Contains("--native-pointer", StringComparer.Ordinal))
        {
            RobustnessUiTests.RunNative();
            CursorReturnUiTests.RunNative();
            TestNativePointer();
            TestPointerVisibility();
        }
    }

    private static T Find<T>(Control parent, string name) where T : Control =>
        parent.Controls.Find(name, true).OfType<T>().Single();

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, NativeMouse.Input[] inputs, int size);

    private static void TestNativePointer()
    {
        if (!NativeMouse.GetCursorPos(out var original)) throw new Exception("Cannot read test cursor position.");
        Point start = new(original.X, original.Y);
        Rectangle monitor = Screen.FromPoint(start).Bounds;
        Point target = new(start.X + (start.X < monitor.Right - 3 ? 2 : -2), start.Y);
        IntPtr testMarker = (IntPtr)0x54455354;
        List<Point> observed = [];
        List<Point> previousPositions = [];
        try
        {
            using MouseCapture capture = new((message, info) =>
            {
                if (message != NativeMouse.WM_MOUSEMOVE || info.ExtraInfo != testMarker) return false;
                if (!NativeMouse.GetCursorPos(out var previous)) throw new Exception("Cannot read cursor position inside the hook.");
                previousPositions.Add(new Point(previous.X, previous.Y));
                observed.Add(new Point(info.Position.X, info.Position.Y));
                return true;
            });
            for (int i = 0; i < 3; i++)
            {
                NativeMouse.MoveCursor(target, SystemInformation.VirtualScreen, (count, inputs, size) =>
                {
                    inputs[0].Data.Mouse.ExtraInfo = testMarker;
                    return SendInput(count, inputs, size);
                });
                DateTime deadline = DateTime.UtcNow.AddSeconds(2);
                while (observed.Count <= i && DateTime.UtcNow < deadline)
                {
                    Application.DoEvents();
                    Thread.Sleep(5);
                }
                if (observed.Count != i + 1 || observed[i] != target || previousPositions[i] != start)
                    throw new Exception($"Windows pointer events were lost or had incorrect coordinates: " +
                        $"iteration={i}, count={observed.Count}, target={target}, start={start}, " +
                        $"observed={string.Join(",", observed)}, previous={string.Join(",", previousPositions)}.");
                if (!NativeMouse.GetCursorPos(out var current) || current.X != start.X || current.Y != start.Y)
                    throw new Exception("Suppressing a forwarded pointer event did not keep the local cursor still.");
            }
        }
        finally
        {
            NativeMouse.MoveCursor(start, SystemInformation.VirtualScreen);
            Application.DoEvents();
        }
        Console.WriteLine("PASS: native pointer events reach the hook, retain deltas and leave the local cursor still; no buttons injected.");
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct CursorInfo
    {
        internal int Size, Flags;
        internal IntPtr Cursor;
        internal NativeMouse.Point Position;
    }
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool GetCursorInfo(ref CursorInfo info);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(NativeMouse.Point position);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr window, int index);
    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int GetClassName(IntPtr window, System.Text.StringBuilder name, int length);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr window, out RectangleNative rect);
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct RectangleNative { public int Left, Top, Right, Bottom; public override string ToString() => $"{Left},{Top},{Right},{Bottom}"; }

    private static void TestPointerVisibility()
    {
        // Isolate pointer input during this short focus check too: a real local
        // click changing foreground must not be attributed to the helper window.
        using MouseCapture keepStill = new((_, _) => true);
        NativeMouse.GetCursorPos(out var original);
        Point position = new(original.X, original.Y);
        IntPtr foreground = GetForegroundWindow();
        IntPtr hiddenCursor;
        try
        {
            using (HiddenPointerWindow parked = new(position))
            {
                hiddenCursor = parked.HiddenCursor;
                parked.HidePointer();
                NativeMouse.MoveCursor(position, SystemInformation.VirtualScreen);
                DateTime until = DateTime.UtcNow.AddMilliseconds(250);
                while (DateTime.UtcNow < until) { Application.DoEvents(); Thread.Sleep(5); }
                CursorInfo info = new() { Size = System.Runtime.InteropServices.Marshal.SizeOf<CursorInfo>() };
                GetWindowRect(parked.Handle, out var actualBounds);
                System.Text.StringBuilder hitClass = new(256);
                GetClassName(WindowFromPoint(new NativeMouse.Point { X = position.X, Y = position.Y }), hitClass, 256);
                if (!GetCursorInfo(ref info) || info.Cursor != hiddenCursor)
                    throw new Exception($"The parked Windows cursor was not transparent: actual={info.Cursor}, hidden={hiddenCursor}, " +
                        $"position={info.Position.X},{info.Position.Y}, bounds={parked.Bounds}, window={WindowFromPoint(info.Position)}, expectedWindow={parked.Handle}, " +
                        $"visible={parked.Visible}, exStyle={GetWindowLong(parked.Handle, -20):X}, opacity={parked.Opacity}, actualBounds={actualBounds}, hitClass={hitClass}.");
                if (GetForegroundWindow() != foreground)
                    throw new Exception($"Hiding the pointer changed foreground: before={foreground}, " +
                        $"after={GetForegroundWindow()}, helper={parked.Handle}.");
            }
            CursorInfo restored = new() { Size = System.Runtime.InteropServices.Marshal.SizeOf<CursorInfo>() };
            if (!GetCursorInfo(ref restored) || restored.Cursor == hiddenCursor)
                throw new Exception("The Windows cursor was not restored after cleanup.");
        }
        finally { NativeMouse.MoveCursor(position, SystemInformation.VirtualScreen); Application.DoEvents(); }
        Console.WriteLine("PASS: parked cursor is transparent without activating a window; cleanup restores it.");
    }

    private static void SaveScreenshot(Form window, string[] args, string name)
    {
        if (args.Length != 2 || args[0] != "--screenshots") return;
        Directory.CreateDirectory(args[1]);
        using Bitmap bitmap = new(window.Width, window.Height);
        window.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
        bitmap.Save(Path.Combine(args[1], name));
        if (name == "receiver.png")
        {
            var tray = (NotifyIcon)typeof(MainWindow).GetField("_tray",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(window)!;
            ContextMenuStrip menu = tray.ContextMenuStrip!;
            menu.CreateControl();
            menu.Size = menu.GetPreferredSize(Size.Empty);
            using Bitmap menuImage = new(menu.Width, menu.Height);
            menu.DrawToBitmap(menuImage, new Rectangle(Point.Empty, menu.Size));
            menuImage.Save(Path.Combine(args[1], "tray-menu.png"));
        }
    }
}
