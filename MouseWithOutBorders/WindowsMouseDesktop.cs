using System.ComponentModel;

namespace MouseWithOutBorders;

// Lives on the input thread. Window creation/destruction is always posted out
// of WH_MOUSE_LL, which must not run WinForms focus/lifecycle code.
internal sealed class WindowsMouseDesktop : IMouseDesktop, IDisposable
{
    private readonly Action<Action> _post;
    private readonly CursorMotionGuard _motion = new();
    private readonly MouseInputQueue _output;
    private HiddenPointerWindow? _hiddenPointer;
    private Point _anchor;
    private bool _hideRequested, _parkPending, _refreshQueued, _disposed;

    internal WindowsMouseDesktop(Action<Action> post, Action<Exception>? error = null,
        NativeMouse.InputSender? sender = null)
    {
        _post = post;
        _output = new(ex => _post(() => (error ?? (failure => throw failure))(ex)), sender);
    }
    public void BeginCursorTransition()
    {
        _parkPending = false;
        _motion.BeginTransition();
    }
    internal bool PlacementPending => _parkPending || _motion.PlacementPending;
    internal bool OutputPending => _output.Pending;

    internal bool SuppressInjectedMove(NativeMouse.HookInfo info)
    {
        bool suppress = _motion.SuppressInjectedMove(info);
        if (!suppress && !_hideRequested) QueueVisibilityRefresh();
        return suppress;
    }

    public void SetPointerHidden(bool hidden)
    {
        if (_disposed) return;
        bool changed = _hideRequested != hidden;
        _hideRequested = hidden;
        if (hidden && changed)
        {
            Rectangle bounds = (Screen.PrimaryScreen ?? Screen.AllScreens[0]).Bounds;
            _anchor = new Point(bounds.Left + bounds.Width / 2, bounds.Top + bounds.Height / 2);
            // Window creation must stay outside the hook, but parking must wait
            // for that window to hide the pointer. Otherwise the output worker
            // can visibly warp to the centre before the posted refresh runs.
            _parkPending = true;
        }
        if (!hidden) _parkPending = false;
        if (changed || (!hidden && _hiddenPointer is not null)) QueueVisibilityRefresh();
    }

    private void QueueVisibilityRefresh()
    {
        if (_disposed || _refreshQueued) return;
        _refreshQueued = true;
        _post(() =>
        {
            _refreshQueued = false;
            if (_disposed) return;
            if (_hideRequested)
            {
                if (_hiddenPointer is null)
                {
                    _hiddenPointer = new HiddenPointerWindow(_anchor);
                    _hiddenPointer.HidePointer();
                }
                // A return/local takeover can cancel parking before this post
                // runs. Never generate a fresh placement for an obsolete hide.
                if (_hideRequested && _parkPending && !_disposed)
                {
                    _parkPending = false;
                    MoveCursor(_anchor);
                }
            }
            else if (!PlacementPending) RevealPointer();
        });
    }

    // Called by the input-loop timer too, so a lost placement notification
    // cannot leave a hidden window or local-input gate stuck indefinitely.
    internal void RecoverVisibility()
    {
        _output.CheckHealth();
        if (!_disposed && !_hideRequested && !PlacementPending) RevealPointer();
    }

    internal bool SuppressPhysicalMove(bool forwarding)
    {
        if (!PlacementPending) return false;
        if (forwarding) return true;
        BeginCursorTransition();
        SetPointerHidden(false);
        return false;
    }

    private void RevealPointer()
    {
        HiddenPointerWindow? pointer = _hiddenPointer;
        _hiddenPointer = null;
        pointer?.Dispose();
    }

    public Rectangle Bounds => SystemInformation.VirtualScreen;
    public Point Cursor => NativeMouse.GetCursorPos(out var position)
        ? new Point(position.X, position.Y) : throw new Win32Exception("Cannot read the mouse position.");
    public void MoveCursor(Point position) => _motion.MoveCursor(position, Bounds, _output.Enqueue);
    public void Inject(uint flags, int data) => NativeMouse.Inject(flags, data, _output.Enqueue);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _output.Dispose();
        RevealPointer();
    }
}
