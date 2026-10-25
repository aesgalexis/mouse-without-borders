using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;

namespace MouseWithOutBorders;

internal static class InputRecoveryTests
{
    internal static void Run()
    {
        TestOutputThreadAndOrdering();
        TestStalledOutput();
        TestOutputFailure();
        TestLocalMovementDuringPlacement();
        TestEmergencyStop();
        TestRetainedInstanceObject();
        Console.WriteLine("PASS: native mouse output never runs on the capture thread; stalled output fails safely; local movement cancels pending placements; emergency stop works from the UI.");
    }

    private static void TestOutputThreadAndOrdering()
    {
        int captureThread = Environment.CurrentManagedThreadId;
        ConcurrentQueue<uint> events = new();
        using ManualResetEventSlim allowHook = new();
        using ManualResetEventSlim nativeStarted = new();
        using MouseInputQueue output = new(ex => throw new Exception("Unexpected output failure", ex),
            (count, inputs, _) =>
            {
                Assert(Environment.CurrentManagedThreadId != captureThread, "SendInput ran inside the capture thread.");
                nativeStarted.Set();
                Assert(allowHook.Wait(3000), "Native output blocked the thread needed for hook delivery.");
                events.Enqueue(inputs[0].Data.Mouse.Flags);
                return count;
            });
        NativeMouse.Inject(NativeMouse.LEFTDOWN, 0, output.Enqueue);
        Assert(nativeStarted.Wait(2000), "Native output worker did not start.");
        NativeMouse.Inject(NativeMouse.LEFTUP, 0, output.Enqueue);
        // Closing must retain the already queued up, without waiting for native
        // input. This also models shutdown while a hook callback is pending.
        output.Dispose();
        allowHook.Set();
        WaitUntil(() => !output.Pending, "Output did not finish queued releases after shutdown.");
        Assert(events.SequenceEqual([NativeMouse.LEFTDOWN, NativeMouse.LEFTUP]), "Shutdown reordered or dropped a held-button release.");
    }

    private static void TestStalledOutput()
    {
        using ManualResetEventSlim unblock = new();
        using ManualResetEventSlim started = new();
        ConcurrentQueue<Exception> errors = new();
        using MouseInputQueue output = new(errors.Enqueue, (count, _, _) =>
        {
            started.Set();
            unblock.Wait(5000);
            return count;
        });
        try
        {
            NativeMouse.Inject(NativeMouse.MOVE, 0, output.Enqueue);
            Assert(started.Wait(2000), "Stalled sender did not start.");
            Thread.Sleep(MouseInputQueue.StallTimeoutMilliseconds + 30);
            output.CheckHealth();
            output.CheckHealth();
            Stopwatch shutdown = Stopwatch.StartNew();
            output.Dispose();
            Assert(shutdown.ElapsedMilliseconds < 200 && errors.Count == 1,
                "Stalled native output blocked shutdown or reported duplicate failures.");
        }
        finally { unblock.Set(); }
    }

    private static void TestOutputFailure()
    {
        using ManualResetEventSlim failed = new();
        using MouseInputQueue output = new(_ => failed.Set(), (_, _, _) => 0);
        NativeMouse.Inject(NativeMouse.MOVE, 0, output.Enqueue);
        Assert(failed.Wait(2000), "Rejected asynchronous native input was not reported.");
        try
        {
            NativeMouse.Inject(NativeMouse.MOVE, 0, output.Enqueue);
            throw new Exception("A failed output queue accepted more input.");
        }
        catch (IOException) { }
    }

    private static void TestLocalMovementDuringPlacement()
    {
        ConcurrentQueue<NativeMouse.HookInfo> placements = new();
        ConcurrentQueue<Action> work = new();
        using WindowsMouseDesktop desktop = new(work.Enqueue, ex => throw ex,
            (count, input, _) =>
            {
                placements.Enqueue(new() { ExtraInfo = input[0].Data.Mouse.ExtraInfo });
                return count;
            });
        // No native input, windows or clipboard are changed by this fixture.
        desktop.MoveCursor(new Point(20, 30));
        WaitUntil(() => !placements.IsEmpty, "Placement was not queued.");
        Assert(desktop.PlacementPending && !desktop.SuppressPhysicalMove(false) && !desktop.PlacementPending,
            "A pending remote placement blocked a local mouse/trackpad.");
        Assert(desktop.SuppressInjectedMove(placements.First()), "A queued remote move overrode local recovery.");
        desktop.MoveCursor(new Point(40, 50));
        Assert(desktop.SuppressPhysicalMove(true), "A forwarding source accepted old-anchor movement before parking.");
        Assert(!desktop.SuppressPhysicalMove(false), "Returning ownership did not immediately release local movement.");
    }

    private static void TestEmergencyStop()
    {
        using MainWindow window = new(new AppSettings { AutoConnect = false, MinimizeToTray = false }, _ => { },
            listenerFactory: () => new(System.Net.IPAddress.Loopback, 0));
        _ = window.Handle;
        Button toggle = window.Controls.Find("ToggleConnection", true).OfType<Button>().Single();
        // Invoke the action directly because this test window is never shown.
        typeof(MainWindow).GetMethod("Toggle", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, null);
        Assert(toggle.Text == "Disconnect / Stop", "Emergency fixture did not start listening.");
        Message stop = Message.Create(window.Handle, 0x0312, (IntPtr)MainWindow.EmergencyHotkeyId, IntPtr.Zero);
        object[] arguments = [stop];
        typeof(MainWindow).GetMethod("WndProc", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, arguments);
        Label status = window.Controls.Find("ConnectionStatus", true).OfType<Label>().Single();
        Assert(status.Text.Contains("Ctrl+Alt+F12") && toggle.Text == "Start receiving",
            "Emergency shortcut did not stop listening independently of the input thread.");
    }

    private static void WaitUntil(Func<bool> condition, string message)
    {
        Stopwatch wait = Stopwatch.StartNew();
        while (!condition() && wait.ElapsedMilliseconds < 3000) Thread.Sleep(5);
        Assert(condition(), message);
    }

    private static void TestRetainedInstanceObject()
    {
        string name = @"Local\MWB-instance-test-" + Guid.NewGuid().ToString("N");
        using Mutex retainedObject = new(false, name);
        using ManualResetEventSlim holding = new();
        using ManualResetEventSlim release = new();
        Thread owner = new(() =>
        {
            using AppInstanceGuard first = new(name);
            Assert(first.Acquired, "An unowned retained mutex prevented startup.");
            holding.Set();
            release.Wait(3000);
        });
        owner.Start();
        try
        {
            Assert(holding.Wait(2000), "The test instance did not acquire ownership.");
            using AppInstanceGuard duplicate = new(name);
            Assert(!duplicate.Acquired, "A live instance allowed a duplicate on another thread.");
        }
        finally { release.Set(); owner.Join(3000); }
        using (AppInstanceGuard restarted = new(name))
            Assert(restarted.Acquired, "The retained named object blocked restart after exit.");
        Thread abandonedOwner = new(() => retainedObject.WaitOne());
        abandonedOwner.Start(); abandonedOwner.Join(3000);
        using AppInstanceGuard recovered = new(name);
        Assert(recovered.Acquired, "Abandoned ownership blocked application recovery.");
        Console.WriteLine("PASS: retained/abandoned instance objects allow restart while live ownership still prevents duplicates.");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
