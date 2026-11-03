using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace MouseWithOutBorders;

internal static class PointerVisibilityTests
{
    internal static void Run()
    {
        TestDelayedReturn();
        TestCompletedReturnRefresh();
        TestContinuousMovement();
        TestClampedReturnPosition();
        TestReusedWindowAndTakeover();
        TestFailedReturn();
        Console.WriteLine("PASS: visibility waits for an actual current position without starving continuous movement; reverse handoffs reassert hiding; stationary input cannot cancel parking; local takeover and failed-return cleanup recover.");
    }

    private sealed class Pointer : IPointerParkingWindow
    {
        internal int Hides, Refreshes, Disposals;
        public void HidePointer() => Hides++;
        public void RefreshCursor() => Refreshes++;
        public void Dispose() => Disposals++;
    }

    private sealed class Fixture : IDisposable
    {
        internal readonly ConcurrentQueue<Action> Work = new();
        internal readonly ConcurrentQueue<NativeMouse.HookInfo> Inputs = new();
        internal readonly Pointer Pointer = new();
        internal readonly WindowsMouseDesktop Desktop;
        internal readonly ManualResetEventSlim CompleteReturn = new();
        internal Point Cursor, Anchor;
        internal long Now;
        internal int Windows;
        private readonly bool _holdReturn;

        internal Fixture(bool holdReturn = false)
        {
            _holdReturn = holdReturn;
            Desktop = new(Work.Enqueue, ex => throw ex, (count, inputs, _) =>
            {
                Inputs.Enqueue(new() { ExtraInfo = inputs[0].Data.Mouse.ExtraInfo });
                if (_holdReturn && Inputs.Count == 2 && !CompleteReturn.Wait(3000))
                    throw new Exception("Test did not release native return output.");
                return count;
            }, anchor => { Anchor = anchor; Windows++; return Pointer; }, () => Cursor, () => Now);
        }

        internal void Park()
        {
            Desktop.BeginCursorTransition();
            Desktop.SetPointerHidden(true);
            Drain();
            Wait(() => !Desktop.OutputPending, "Parking was not sent.");
            Assert(!Desktop.SuppressInjectedMove(Inputs.Last()), "Parking was rejected.");
            Cursor = Anchor;
        }

        internal void Drain() { while (Work.TryDequeue(out Action? action)) action(); }
        public void Dispose() { CompleteReturn.Set(); Desktop.Dispose(); }
    }

    private static void TestDelayedReturn()
    {
        using Fixture fixture = new(holdReturn: true);
        fixture.Park();
        Point edge = fixture.Anchor + new Size(-100, 20);
        fixture.Desktop.BeginCursorTransition();
        fixture.Desktop.MoveCursor(edge);
        fixture.Desktop.SetPointerHidden(false);
        Wait(() => fixture.Inputs.Count == 2, "Return was not submitted.");
        Assert(!fixture.Desktop.SuppressInjectedMove(fixture.Inputs.Last()), "Current return was rejected.");
        fixture.Drain();
        Assert(fixture.Pointer.Disposals == 0, "The hook acknowledgement revealed the centre while SendInput was still running.");
        fixture.CompleteReturn.Set();
        Wait(() => !fixture.Desktop.OutputPending, "Return output did not complete.");
        fixture.Desktop.RecoverVisibility();
        Assert(fixture.Pointer.Disposals == 0, "Output completion revealed the centre before Windows applied the position.");
        fixture.Cursor = edge;
        fixture.Desktop.RecoverVisibility();
        Assert(fixture.Pointer.Disposals == 1, "The pointer was not revealed at its actual return position.");
    }

    private static void TestReusedWindowAndTakeover()
    {
        using Fixture fixture = new();
        fixture.Park();
        fixture.Desktop.BeginCursorTransition();
        fixture.Desktop.MoveCursor(fixture.Anchor + new Size(100, 20));
        fixture.Desktop.SetPointerHidden(false);
        Wait(() => !fixture.Desktop.OutputPending, "Return was not sent.");
        NativeMouse.HookInfo oldReturn = fixture.Inputs.Last();
        // Reverse again before the queued reveal can destroy the helper.
        fixture.Desktop.BeginCursorTransition();
        fixture.Desktop.SetPointerHidden(true);
        fixture.Drain();
        Wait(() => !fixture.Desktop.OutputPending, "Reused parking was not sent.");
        Assert(fixture.Windows == 1 && fixture.Pointer.Hides == 2 && fixture.Pointer.Disposals == 0,
            "A quick reverse handoff did not reassert the reused window's hiding.");
        Assert(fixture.Desktop.SuppressInjectedMove(oldReturn), "An obsolete return escaped after reverse handoff.");
        Assert(fixture.Desktop.SuppressPhysicalMove(false, stationary: true) && fixture.Desktop.PlacementPending,
            "A stationary pointer notification exposed the parked cursor.");
        Assert(!fixture.Desktop.SuppressPhysicalMove(false) && !fixture.Desktop.PlacementPending,
            "Genuine local movement was blocked by parking.");
        Assert(fixture.Desktop.SuppressInjectedMove(fixture.Inputs.Last()), "Late parking overrode local takeover.");
        fixture.Drain();
        Assert(fixture.Pointer.Disposals == 1, "Local takeover did not release cursor hiding.");
    }

    private static void TestCompletedReturnRefresh()
    {
        using Fixture fixture = new(holdReturn: true);
        fixture.Park();
        Point edge = fixture.Anchor + new Size(-100, 20);
        fixture.Desktop.BeginCursorTransition();
        fixture.Desktop.MoveCursor(edge);
        fixture.Desktop.SetPointerHidden(false);
        Wait(() => fixture.Inputs.Count == 2, "Return was not started.");
        fixture.Desktop.SuppressInjectedMove(fixture.Inputs.Last());
        fixture.Drain();
        fixture.Cursor = edge;
        fixture.CompleteReturn.Set();
        Wait(() => !fixture.Work.IsEmpty, "Native completion did not request a visibility recheck.");
        fixture.Drain();
        Assert(fixture.Pointer.Disposals == 1, "An applied return waited for the periodic watchdog to restore visibility.");
    }

    private static void TestContinuousMovement()
    {
        using Fixture fixture = new(holdReturn: true);
        fixture.Park();
        Point entry = fixture.Anchor + new Size(-100, 20);
        fixture.Desktop.BeginCursorTransition();
        fixture.Desktop.MoveCursor(entry);
        fixture.Desktop.SetPointerHidden(false);
        fixture.Desktop.MoveCursor(entry + new Size(2, 0));
        fixture.Desktop.MoveCursor(entry + new Size(4, 0));
        Wait(() => fixture.Inputs.Count >= 2, "Continuous movement did not start.");
        fixture.Desktop.SuppressInjectedMove(fixture.Inputs.ElementAt(1));
        fixture.Cursor = entry;
        fixture.Drain();
        Assert(fixture.Desktop.OutputPending && fixture.Pointer.Disposals == 1,
            "Continuous output prevented revealing an already applied current position.");
    }

    private static void TestFailedReturn()
    {
        using Fixture fixture = new();
        fixture.Park();
        fixture.Desktop.BeginCursorTransition();
        fixture.Desktop.MoveCursor(fixture.Anchor + new Size(-100, 20));
        fixture.Desktop.SetPointerHidden(false);
        Wait(() => !fixture.Desktop.OutputPending, "Failed return was not sent.");
        fixture.Desktop.SuppressInjectedMove(fixture.Inputs.Last());
        fixture.Now = CursorMotionGuard.PlacementTimeoutMilliseconds - 1;
        fixture.Desktop.MoveCursor(fixture.Anchor + new Size(-98, 20));
        Wait(() => !fixture.Desktop.OutputPending, "Following movement was not sent.");
        fixture.Now++;
        try { fixture.Desktop.RecoverVisibility(); throw new Exception("An unapplied return was silently revealed at the centre."); }
        catch (Win32Exception) { }
        fixture.Desktop.Dispose();
        Assert(fixture.Pointer.Disposals == 1, "Failed-return cleanup left the cursor hidden.");
    }

    private static void TestClampedReturnPosition()
    {
        using Fixture fixture = new();
        fixture.Park();
        Rectangle bounds = fixture.Desktop.Bounds;
        Point requested = new(bounds.Left - 100, bounds.Top - 100);
        Point clamped = new(bounds.Left, bounds.Top);
        Rectangle monitor = Screen.FromPoint(clamped).Bounds;
        Point visible = new(Math.Clamp(clamped.X, monitor.Left, monitor.Right - 1),
            Math.Clamp(clamped.Y, monitor.Top, monitor.Bottom - 1));
        fixture.Desktop.BeginCursorTransition();
        fixture.Desktop.MoveCursor(requested);
        fixture.Desktop.SetPointerHidden(false);
        Wait(() => !fixture.Desktop.OutputPending, "Clamped return was not sent.");
        fixture.Desktop.SuppressInjectedMove(fixture.Inputs.Last());
        fixture.Cursor = visible;
        fixture.Drain();
        Assert(fixture.Pointer.Disposals == 1, "Windows' clamped return position left the pointer hidden.");
    }

    [DllImport("user32.dll")] private static extern IntPtr GetCursor();
    [DllImport("user32.dll")] private static extern IntPtr SetCursor(IntPtr cursor);
    [DllImport("user32.dll")] private static extern IntPtr LoadCursor(IntPtr instance, IntPtr name);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();

    internal static void RunNative()
    {
        using MouseCapture keepStill = new((_, _) => true);
        if (!NativeMouse.GetCursorPos(out var position)) throw new Exception("Cannot read the cursor.");
        IntPtr originalCursor = GetCursor();
        IntPtr foreground = GetForegroundWindow();
        try
        {
            for (int attempt = 0; attempt < 8; attempt++)
            {
                ConcurrentQueue<Action> work = new();
                ConcurrentQueue<NativeMouse.HookInfo> inputs = new();
                HiddenPointerWindow? pointer = null;
                // The real helper covers the current cursor. Native output is
                // captured so this shape/lifecycle check needs no cursor warps.
                using WindowsMouseDesktop desktop = new(work.Enqueue, ex => throw ex,
                    (count, input, _) => { inputs.Enqueue(new() { ExtraInfo = input[0].Data.Mouse.ExtraInfo }); return count; },
                    _ => pointer = new HiddenPointerWindow(new Point(position.X, position.Y)));
                desktop.SetPointerHidden(true);
                Drain();
                Wait(() => !desktop.OutputPending, "Native parking fixture did not submit its placement.");
                IntPtr blank = pointer!.HiddenCursor;
                IntPtr arrow = LoadCursor(IntPtr.Zero, (IntPtr)32512);
                SetCursor(arrow);
                Assert(GetCursor() == arrow, "The control sequence did not replace the input thread's cursor image.");
                Assert(!desktop.SuppressInjectedMove(inputs.Last()), "Current parking was rejected.");
                AssertBlank("Parking was accepted with a visible arrow instead of the transparent cursor.");
                SetCursor(arrow);
                desktop.SuppressPhysicalMove(true);
                AssertBlank("Forwarding did not reassert cursor hiding.");
                SetCursor(arrow);
                desktop.RecoverVisibility();
                AssertBlank("Visibility recovery left a ghost cursor visible.");
                // Repeated hide must not save the blank image as the cursor to
                // restore, including when a quick reversal reuses the helper.
                pointer.HidePointer();
                pointer.HidePointer();
                desktop.BeginCursorTransition();
                desktop.SetPointerHidden(false);
                Drain();
                Assert(pointer.IsDisposed && GetCursor() != blank, "Reused pointer cleanup restored its own blank cursor.");
                Assert(GetForegroundWindow() == foreground, "Pointer visibility work changed foreground focus.");

                void Drain() { while (work.TryDequeue(out Action? action)) action(); }
                void AssertBlank(string message) => Assert(GetCursor() == blank, message);
            }
        }
        finally { SetCursor(originalCursor); }
        Console.WriteLine("PASS: native control reproduces a replaced thread cursor; parking, forwarding and recovery restore its transparent image; repeated hiding cleans up and preserves focus; no movement or buttons injected.");
    }

    private static void Wait(Func<bool> condition, string message)
    {
        Stopwatch wait = Stopwatch.StartNew();
        while (!condition() && wait.ElapsedMilliseconds < 3000) Thread.Sleep(1);
        Assert(condition(), message);
    }
    private static void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }
}
