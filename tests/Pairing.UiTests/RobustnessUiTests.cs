using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace MouseWithOutBorders;

internal static class RobustnessUiTests
{
    internal static void Run()
    {
        int uiThread = Environment.CurrentManagedThreadId;
        for (int attempt = 0; attempt < 4; attempt++)
        {
            ConcurrentQueue<int> callbackThreads = new();
            ConcurrentQueue<Exception> errors = new();
            ConcurrentQueue<MousePacket> sent = new();
            MouseSharingService service = new(false, true, packet => { sent.Enqueue(packet); return true; },
                errors.Enqueue, _ => callbackThreads.Enqueue(Environment.CurrentManagedThreadId));
            try
            {
                service.Receive(new MousePacket("layout", Data: 1));
                // Deliberately do not pump the application's UI thread. A hook
                // bound to that thread stops receiving input in this situation,
                // which also occurs during native minimize/title-bar loops.
                Complete(service.FlushAsync(), "Mouse input needs the main UI thread to process its queue.");
                Assert(!callbackThreads.IsEmpty && callbackThreads.All(thread => thread != uiThread),
                    "Mouse ownership/status work ran on the application's UI thread.");
                Assert(errors.IsEmpty, "Starting the independent input loop failed: " + string.Join("; ", errors));
                Assert(service.Ready.IsCompletedSuccessfully && sent.Count == 1 &&
                    sent.TryPeek(out MousePacket? ready) && ready.Kind == "ready" && ready.Data == 1,
                    "A passive receiver must confirm readiness without sending mouse input before a remote mouse enters.");
            }
            finally { service.Dispose(); }
            Complete(service.Completion, "The input thread remained alive after disposal.");
            service.Receive(new MousePacket("move", Dx: 1, Transfer: 1));
            service.Dispose();
        }
        try
        {
            using MouseSharingService rejected = new(true, true, _ => false, _ => { }, _ => { });
            throw new Exception("A failed initial layout send was accepted as a working input service.");
        }
        catch (IOException) { }
        Console.WriteLine("PASS: mouse input starts, processes work and stops independently of the blocked application UI; repeated shutdown and late packets are safe.");
    }

    internal static void RunNative()
    {
        if (!NativeMouse.GetCursorPos(out var original)) throw new Exception("Cannot read the original cursor position.");
        Point start = new(original.X, original.Y);
        Rectangle bounds = SystemInformation.VirtualScreen;
        ConcurrentQueue<Exception> errors = new();
        ConcurrentQueue<MousePacket> sent = new();
        ConcurrentQueue<string> statuses = new();
        using Form foreground = new()
        {
            Text = "MWB native regression focus target", StartPosition = FormStartPosition.Manual,
            Bounds = new Rectangle(Screen.FromPoint(start).WorkingArea.Location + new Size(30, 30), new Size(480, 240))
        };
        using MainWindow window = new(new AppSettings { AutoConnect = false, MinimizeToTray = false }, _ => { });
        window.StartPosition = FormStartPosition.Manual;
        window.Location = foreground.Location + new Size(30, 30);
        window.TopMost = true;
        MouseSharingService? service = null;
        ClickGuard? clickGuard = null;
        try
        {
            foreground.Show();
            window.Show();
            Application.DoEvents();
            Point minimize = MinimizeButton(window);
            Assert(GetAncestor(WindowFromPoint(new NativeMouse.Point { X = minimize.X, Y = minimize.Y }), 2) == window.Handle,
                "Another window covers the native test window's minimize button.");

            IntPtr ownedWindow = window.Handle;
            service = new(false, true, packet => { sent.Enqueue(packet); return true; }, errors.Enqueue, status =>
            {
                statuses.Enqueue(status);
                // Install on the production input thread so the test adds no
                // cross-thread low-level-hook dependency around SendInput.
                clickGuard ??= new ClickGuard(ownedWindow);
            });
            service.Receive(new MousePacket("layout", Data: 1));
            Enter(service, 1, pumpUi: true);
            MoveTo(service, minimize, pumpUi: true);
            service.Receive(new MousePacket("button", Flags: NativeMouse.LEFTDOWN, Transfer: 1));
            service.Receive(new MousePacket("button", Flags: NativeMouse.LEFTUP, Transfer: 1));
            Complete(service.FlushAsync(), "The remotely injected minimize click blocked the input loop.");
            PumpUntil(() => window.WindowState == FormWindowState.Minimized,
                "The receiver's remotely injected minimize click did not minimize the owned MWB window.");
            Assert(clickGuard is { AcceptedDowns: 1, RejectedDowns: 0 },
                "The native minimize click did not remain inside the owned MWB window.");

            PumpUntil(() => GetForegroundWindow() != window.Handle,
                "Windows did not transfer focus away from the minimized test window.");
            IntPtr expectedForeground = GetForegroundWindow();
            // All waits below intentionally block the application's UI thread.
            // Remote input must keep working without a physical/local wake-up.
            for (long transfer = 1; transfer <= 5; transfer++)
            {
                if (transfer > 1) Enter(service, transfer);
                Point target = new(bounds.Left + Math.Min(80, bounds.Width - 1),
                    bounds.Top + Math.Min(100 + (int)transfer * 4, bounds.Height - 1));
                MoveTo(service, target, transfer);
                Assert(GetForegroundWindow() == expectedForeground,
                    "Receiving a remote mouse activated a pointer-parking or settings window.");
                service.Receive(new MousePacket("move", Dx: -(target.X - bounds.Left + 1), Transfer: transfer));
                Complete(service.FlushAsync(), "Returning the mouse blocked the independent input loop.");
                WaitUntil(() => sent.Any(packet => packet.Kind == "return" && packet.Transfer == transfer),
                    "Returning over the receiver edge did not produce an ownership return.");
                Assert(GetForegroundWindow() == expectedForeground,
                    "Returning the mouse changed the receiver's foreground window.");
                Assert(window.WindowState == FormWindowState.Minimized,
                    "Mouse ownership changes restored the minimized MWB window.");
            }
            Assert(errors.IsEmpty, "Native remote input failed: " + string.Join("; ", errors));
        }
        catch (Exception ex)
        {
            NativeMouse.GetCursorPos(out var actual);
            throw new Exception($"{ex.Message} Actual cursor={actual.X},{actual.Y}; " +
                $"errors={string.Join("; ", errors)}; status={string.Join("; ", statuses)}; sent={string.Join("; ", sent)}; " +
                $"hook={clickGuard?.Trace}", ex);
        }
        finally
        {
            clickGuard?.Dispose();
            service?.Dispose();
            if (service is not null) Complete(service.Completion, "The native input service did not finish cleanup.");
            NativeMouse.MoveCursor(start, bounds);
            Application.DoEvents();
        }
        Console.WriteLine("PASS: an owned MWB window minimizes through remote input; movement and repeated handoffs continue with the main UI blocked and preserve foreground focus; no user-window clicks injected.");
    }

    private static void Enter(MouseSharingService service, long transfer, bool pumpUi = false)
    {
        service.Receive(new MousePacket("enter", Data: 1, Ratio: .5, Transfer: transfer));
        Complete(service.FlushAsync(), "A remote mouse entry blocked the input loop.");
        Rectangle bounds = SystemInformation.VirtualScreen;
        Point expected = new(bounds.Left + Math.Min(5, Math.Max(0, (bounds.Width - 1) / 2)),
            bounds.Top + (int)Math.Round(.5 * Math.Max(0, bounds.Height - 1)));
        WaitForPosition(expected, pumpUi);
    }

    private static void MoveTo(MouseSharingService service, Point target, long transfer = 1, bool pumpUi = false)
    {
        if (!NativeMouse.GetCursorPos(out var current)) throw new Exception("Cannot read the remote cursor position.");
        int dx = target.X - current.X, dy = target.Y - current.Y;
        while (dx != 0 || dy != 0)
        {
            int stepX = Math.Clamp(dx, -10000, 10000), stepY = Math.Clamp(dy, -10000, 10000);
            service.Receive(new MousePacket("move", Dx: stepX, Dy: stepY, Transfer: transfer));
            dx -= stepX; dy -= stepY;
        }
        Complete(service.FlushAsync(), "Remote cursor movement blocked the input loop.");
        WaitForPosition(target, pumpUi);
    }

    private static void WaitForPosition(Point expected, bool pumpUi = false)
    {
        bool AtTarget() => NativeMouse.GetCursorPos(out var point) && point.X == expected.X && point.Y == expected.Y;
        string message = "The remote cursor stopped moving; expected " + expected + ".";
        if (pumpUi) PumpUntil(AtTarget, message);
        else WaitUntil(AtTarget, message);
    }

    private static void Complete(Task task, string message)
    {
        if (!task.Wait(TimeSpan.FromSeconds(4))) throw new Exception(message);
        task.GetAwaiter().GetResult();
    }

    private static void WaitUntil(Func<bool> condition, string message)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(3);
        while (!condition() && DateTime.UtcNow < deadline) Thread.Sleep(5);
        Assert(condition(), message);
    }

    private static void PumpUntil(Func<bool> condition, string message)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(3);
        while (!condition() && DateTime.UtcNow < deadline) { Application.DoEvents(); Thread.Sleep(5); }
        Assert(condition(), message);
    }

    private static void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }

    private static Point MinimizeButton(Form window)
    {
        TitleBarInfo info = new()
        {
            Size = (uint)Marshal.SizeOf<TitleBarInfo>(), States = new uint[6], Rectangles = new NativeRectangle[6]
        };
        SendMessage(window.Handle, 0x033F, IntPtr.Zero, ref info); // WM_GETTITLEBARINFOEX
        NativeRectangle button = info.Rectangles[2];
        Assert(button.Right > button.Left && button.Bottom > button.Top, "Windows did not expose the test window's minimize button.");
        return new Point((button.Left + button.Right) / 2, (button.Top + button.Bottom) / 2);
    }

    private sealed class ClickGuard : IDisposable
    {
        private readonly Control _dispatch = new();
        private readonly NativeMouse.HookProcedure _observer;
        private readonly int _ownerThread = Environment.CurrentManagedThreadId;
        private readonly HashSet<int> _suppressedPhysicalButtons = [];
        private readonly TaskCompletionSource _disposed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private IntPtr _hook;
        private bool _isolating = true, _testPressed;
        private int _acceptedDowns, _rejectedDowns;
        private readonly ConcurrentQueue<string> _trace = new();
        internal string Trace => string.Join("; ", _trace);
        internal int AcceptedDowns => Volatile.Read(ref _acceptedDowns);
        internal int RejectedDowns => Volatile.Read(ref _rejectedDowns);

        internal ClickGuard(IntPtr ownedWindow)
        {
            _ = _dispatch.Handle;
            _observer = (code, wp, lp) =>
            {
                if (code >= 0)
                {
                    NativeMouse.HookInfo info = Marshal.PtrToStructure<NativeMouse.HookInfo>(lp);
                    uint message = unchecked((uint)wp.ToInt64());
                    if (_trace.Count < 20) _trace.Enqueue($"{message:X}:{info.ExtraInfo.ToInt64():X}@{info.Position.X},{info.Position.Y}");
                    if (!NativeMouse.IsOurInput(info))
                    {
                        int button = message switch
                        {
                            NativeMouse.WM_LBUTTONDOWN or NativeMouse.WM_LBUTTONUP => 0,
                            NativeMouse.WM_RBUTTONDOWN or NativeMouse.WM_RBUTTONUP => 1,
                            NativeMouse.WM_MBUTTONDOWN or NativeMouse.WM_MBUTTONUP => 2,
                            NativeMouse.WM_XBUTTONDOWN or NativeMouse.WM_XBUTTONUP => 2 + (int)(info.MouseData >> 16),
                            _ => -1
                        };
                        bool down = message is NativeMouse.WM_LBUTTONDOWN or NativeMouse.WM_RBUTTONDOWN or
                            NativeMouse.WM_MBUTTONDOWN or NativeMouse.WM_XBUTTONDOWN;
                        if (_isolating)
                        {
                            if (button >= 0)
                            {
                                if (down) _suppressedPhysicalButtons.Add(button);
                                else _suppressedPhysicalButtons.Remove(button);
                            }
                            return (IntPtr)1;
                        }
                        // When stopping, allow normal input immediately while
                        // consuming an up whose down was isolated by this test.
                        if (button >= 0 && (down ? _suppressedPhysicalButtons.Contains(button) : _suppressedPhysicalButtons.Remove(button)))
                            return (IntPtr)1;
                    }
                    else if (message == NativeMouse.WM_LBUTTONDOWN)
                    {
                        if (GetAncestor(WindowFromPoint(info.Position), 2) != ownedWindow)
                        {
                            Interlocked.Increment(ref _rejectedDowns);
                            return (IntPtr)1;
                        }
                        _testPressed = true;
                        Interlocked.Increment(ref _acceptedDowns);
                    }
                    else if (message == NativeMouse.WM_LBUTTONUP)
                    {
                        if (!_testPressed) return (IntPtr)1;
                        _testPressed = false;
                    }
                }
                IntPtr result = NativeMouse.CallNextHookEx(_hook, code, wp, lp);
                if (code >= 0 && _trace.Count < 20)
                {
                    NativeMouse.HookInfo info = Marshal.PtrToStructure<NativeMouse.HookInfo>(lp);
                    if (NativeMouse.IsOurInput(info)) _trace.Enqueue($"next={result}");
                }
                return result;
            };
            _hook = NativeMouse.InstallHook(_observer);
        }

        public void Dispose()
        {
            if (_disposed.Task.IsCompleted) return;
            void Cleanup()
            {
                if (_hook != IntPtr.Zero) { NativeMouse.UnhookWindowsHookEx(_hook); _hook = IntPtr.Zero; }
                _dispatch.Dispose();
                _disposed.TrySetResult();
                GC.KeepAlive(_observer);
            }
            void Stop()
            {
                _isolating = false;
                if (_testPressed) NativeMouse.Inject(NativeMouse.LEFTUP);
                if (_suppressedPhysicalButtons.Count == 0) { Cleanup(); return; }
                DateTime until = DateTime.UtcNow.AddSeconds(1);
                System.Windows.Forms.Timer releases = new() { Interval = 20 };
                releases.Tick += (_, _) =>
                {
                    if (_suppressedPhysicalButtons.Count != 0 && DateTime.UtcNow < until) return;
                    releases.Stop(); releases.Dispose(); Cleanup();
                };
                releases.Start();
            }
            if (Environment.CurrentManagedThreadId == _ownerThread) Stop();
            else
            {
                try { _dispatch.BeginInvoke((MethodInvoker)Stop); }
                catch (InvalidOperationException) { Cleanup(); }
                Complete(_disposed.Task, "The test-only pointer interceptor did not stop.");
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)] private struct NativeRectangle { internal int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct TitleBarInfo
    {
        internal uint Size;
        internal NativeRectangle TitleBar;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 6)] internal uint[] States;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 6)] internal NativeRectangle[] Rectangles;
    }
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(NativeMouse.Point point);
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr window, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessage(IntPtr window, uint message, IntPtr wParam, ref TitleBarInfo info);
}
