namespace MouseWithOutBorders;

// The receiver must keep processing button-up/move packets while its main
// window is minimized, dragged, or inside a Windows non-client tracking loop.
internal sealed class MouseSharingService : IDisposable
{
    private const int MaximumQueuedPackets = 2048;
    private readonly Thread _thread;
    private readonly Action<Exception> _error;
    private readonly TaskCompletionSource<bool> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Control? _dispatch;
    private MouseSharingSession? _session;
    private WindowsMouseDesktop? _desktop;
    private int _stopping, _queuedPackets, _reportedError;

    internal Task Completion => _completion.Task;

    internal MouseSharingService(bool initiator, bool exitRight, Func<MousePacket, bool> send,
        Action<Exception> error, Action<string> status)
    {
        _error = error;
        TaskCompletionSource<bool> ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _thread = new Thread(() =>
        {
            try
            {
                using Control dispatch = new();
                _ = dispatch.Handle;
                _dispatch = dispatch;
                using WindowsMouseDesktop desktop = new(action => Post(action), Fail);
                _desktop = desktop;
                // Install capture before taking the held-state snapshot, so a
                // release during startup cannot fall into an unobserved gap.
                using MouseCapture capture = new(OnPhysicalInput, OnInjectedInput);
                _session = new MouseSharingSession(initiator, exitRight, desktop, send,
                    ex => ReportError(ex), status, NativeMouse.GetHeldButtons());
                using System.Windows.Forms.Timer recovery = new() { Interval = 100 };
                recovery.Tick += (_, _) =>
                {
                    try { desktop.RecoverVisibility(); }
                    catch (Exception ex) { Fail(ex); }
                };
                recovery.Start();
                try
                {
                    _session.Start();
                    ready.TrySetResult(true);
                    if (Volatile.Read(ref _stopping) == 0) Application.Run();
                }
                finally
                {
                    Interlocked.Exchange(ref _stopping, 1);
                    try { _session.Dispose(); } catch (Exception ex) { ReportError(ex); }
                    // Keep the hook installed until the final restoration has
                    // reached it; pending old parking moves remain rejectable.
                    long deadline = Environment.TickCount64 + CursorMotionGuard.PlacementTimeoutMilliseconds;
                    while ((desktop.PlacementPending || desktop.OutputPending) && Environment.TickCount64 < deadline)
                    {
                        Application.DoEvents();
                        Thread.Sleep(1);
                    }
                    desktop.Dispose();
                }
            }
            catch (Exception ex)
            {
                if (!ready.TrySetException(ex)) ReportError(ex);
            }
            finally
            {
                _session = null;
                _desktop = null;
                _completion.TrySetResult(true);
            }
        }) { IsBackground = true, Name = "MWB mouse sharing" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        try
        {
            ready.Task.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
        }
        catch { Dispose(); throw; }
    }

    private bool Post(Action action)
    {
        Control? dispatch = _dispatch;
        if (dispatch is null || dispatch.IsDisposed || Completion.IsCompleted) return false;
        try
        {
            dispatch.BeginInvoke((MethodInvoker)(() =>
            {
                try { action(); }
                catch (Exception ex) { Fail(ex); }
            }));
            return true;
        }
        catch (InvalidOperationException) { return false; }
    }

    internal void Receive(MousePacket packet)
    {
        if (Volatile.Read(ref _stopping) != 0) return;
        if (Interlocked.Increment(ref _queuedPackets) > MaximumQueuedPackets)
        {
            Interlocked.Decrement(ref _queuedPackets);
            Fail(new IOException("Mouse input backlog exceeded its limit. Reconnecting."));
            return;
        }
        if (!Post(() =>
        {
            Interlocked.Decrement(ref _queuedPackets);
            if (Volatile.Read(ref _stopping) != 0) return;
            try { _session?.Receive(packet); }
            catch (Exception ex) { Fail(ex); }
        }))
        {
            Interlocked.Decrement(ref _queuedPackets);
            if (Volatile.Read(ref _stopping) == 0) Fail(new IOException("Mouse input loop is unavailable."));
        }
    }

    private bool OnPhysicalInput(uint message, NativeMouse.HookInfo info)
    {
        if (Volatile.Read(ref _stopping) != 0 || _session is not { } session) return false;
        try
        {
            if (message == NativeMouse.WM_MOUSEMOVE)
            {
                // Pending remote/parking placement must never lock a local
                // mouse or trackpad. Local movement retires queued placement
                // tags before ownership is reclaimed.
                if (_desktop!.SuppressPhysicalMove(session.IsRemote)) return true;
                if (!NativeMouse.GetCursorPos(out var previous)) return session.IsRemote;
                return session.PhysicalMove(new Point(previous.X, previous.Y), new Point(info.Position.X, info.Position.Y));
            }
            uint flag = message switch
            {
                NativeMouse.WM_LBUTTONDOWN => NativeMouse.LEFTDOWN,
                NativeMouse.WM_LBUTTONUP => NativeMouse.LEFTUP,
                NativeMouse.WM_RBUTTONDOWN => NativeMouse.RIGHTDOWN,
                NativeMouse.WM_RBUTTONUP => NativeMouse.RIGHTUP,
                NativeMouse.WM_MBUTTONDOWN => NativeMouse.MIDDLEDOWN,
                NativeMouse.WM_MBUTTONUP => NativeMouse.MIDDLEUP,
                NativeMouse.WM_XBUTTONDOWN => NativeMouse.XDOWN,
                NativeMouse.WM_XBUTTONUP => NativeMouse.XUP,
                NativeMouse.WM_MOUSEWHEEL => NativeMouse.WHEEL,
                NativeMouse.WM_MOUSEHWHEEL => NativeMouse.HWHEEL,
                _ => 0
            };
            if (flag == 0) return session.IsRemote;
            int data = flag is NativeMouse.WHEEL or NativeMouse.HWHEEL
                ? (short)((info.MouseData >> 16) & 0xFFFF)
                : flag is NativeMouse.XDOWN or NativeMouse.XUP ? (int)((info.MouseData >> 16) & 0xFFFF) : 0;
            return flag is NativeMouse.WHEEL or NativeMouse.HWHEEL
                ? session.PhysicalWheel(flag, data) : session.PhysicalButton(flag, data);
        }
        catch (Exception ex) { Fail(ex); return false; }
    }

    private bool OnInjectedInput(uint message, NativeMouse.HookInfo info)
    {
        if (message != NativeMouse.WM_MOUSEMOVE) return false;
        try { return _desktop?.SuppressInjectedMove(info) == true; }
        catch (Exception ex) { Fail(ex); return false; }
    }

    private void ReportError(Exception ex)
    {
        if (Interlocked.Exchange(ref _reportedError, 1) != 0) return;
        // Error reporting must not throw through an unmanaged hook callback.
        try { _error(ex); } catch { }
    }

    private void Fail(Exception ex)
    {
        ReportError(ex);
        RequestStop();
    }

    private void RequestStop()
    {
        if (Interlocked.Exchange(ref _stopping, 1) != 0) return;
        Post(Application.ExitThread);
    }

    internal async Task FlushAsync()
    {
        TaskCompletionSource<bool> flushed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        if (Volatile.Read(ref _stopping) == 0) Post(() => flushed.TrySetResult(true));
        await Task.WhenAny(flushed.Task, Completion).ConfigureAwait(false);
    }

    public void Dispose()
    {
        RequestStop();
        if (Thread.CurrentThread != _thread) _thread.Join(TimeSpan.FromSeconds(2));
    }
}
