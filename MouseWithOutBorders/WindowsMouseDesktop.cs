using System.ComponentModel;

namespace MouseWithOutBorders;

// Lives on the input thread. Window creation/destruction is always posted out
// of WH_MOUSE_LL, which must not run WinForms focus/lifecycle code.
internal sealed class WindowsMouseDesktop : IMouseDesktop, IDisposable
{
    private readonly Action<Action> _post;
    private readonly CursorMotionGuard _motion;
    private readonly MouseInputQueue _output;
    private readonly Func<Point, IPointerParkingWindow> _pointerFactory;
    private readonly Func<Point> _readCursor;
    private readonly Func<long> _milliseconds;
    private IPointerParkingWindow? _hiddenPointer;
    private readonly HashSet<Point> _revealPositions = [];
    private bool _awaitingVisiblePlacement;
    private long _placementDeadline;
    private Point _anchor;
    private bool _hideRequested, _parkPending, _refreshQueued, _disposed;
    private bool _parkPrepared, _preflightQueued;
    private long _hideDeadline;
    private volatile bool _visibilityRequested;

    internal WindowsMouseDesktop(Action<Action> post, Action<Exception>? error = null,
        NativeMouse.InputSender? sender = null, Func<Point, IPointerParkingWindow>? pointerFactory = null,
        Func<Point>? readCursor = null, Func<long>? milliseconds = null)
    {
        _post = post;
        _pointerFactory = pointerFactory ?? (anchor => new HiddenPointerWindow(anchor, () =>
        {
            if (_parkPending) QueueVisibilityRefresh();
        }));
        _readCursor = readCursor ?? ReadCursor;
        _milliseconds = milliseconds ?? (() => Environment.TickCount64);
        _motion = new(_milliseconds);
        _output = new(ex => _post(() => (error ?? (failure => throw failure))(ex)), sender, () =>
        {
            // Retry after the native call too, without waiting for the 100 ms
            // watchdog or posting work for ordinary already-visible movement.
            if (_visibilityRequested) _post(() =>
            {
                if (!_disposed) QueueVisibilityRefresh();
            });
        });
    }
    public void BeginCursorTransition()
    {
        _parkPending = false;
        _parkPrepared = _preflightQueued = false;
        _visibilityRequested = false;
        _awaitingVisiblePlacement = false;
        _revealPositions.Clear();
        _motion.BeginTransition();
    }
    internal bool PlacementPending => _parkPending || _motion.PlacementPending;
    internal bool OutputPending => _output.Pending;
    internal bool IsCursorHidden => _hiddenPointer?.IsCursorHidden == true;

    internal bool SuppressInjectedMove(NativeMouse.HookInfo info)
    {
        bool suppress = _motion.SuppressInjectedMove(info);
        // Reassert the blank image immediately before accepting a parking
        // placement. Creating the helper earlier does not reserve that image.
        if (!suppress && _hideRequested) _hiddenPointer?.RefreshCursor();
        if (!suppress && _parkPending) QueueVisibilityRefresh();
        if (!suppress && !_hideRequested && _hiddenPointer is not null) QueueVisibilityRefresh();
        return suppress;
    }

    public void SetPointerHidden(bool hidden)
    {
        if (_disposed) return;
        bool changed = _hideRequested != hidden;
        _hideRequested = hidden;
        _visibilityRequested = !hidden && _hiddenPointer is not null;
        if (hidden && changed)
        {
            Rectangle bounds = (Screen.PrimaryScreen ?? Screen.AllScreens[0]).Bounds;
            _anchor = new Point(bounds.Left + bounds.Width / 2, bounds.Top + bounds.Height / 2);
            // Window creation must stay outside the hook, but parking must wait
            // for that window to hide the pointer. Otherwise the output worker
            // can visibly warp to the centre before the posted refresh runs.
            _parkPending = true;
            _parkPrepared = _preflightQueued = false;
            _hideDeadline = _milliseconds() + CursorMotionGuard.PlacementTimeoutMilliseconds;
            _visibilityRequested = true;
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
                _hiddenPointer ??= _pointerFactory(_anchor);
                // Reassert hiding even when a quick reverse handoff reuses the
                // window before the previous reveal was allowed to dispose it.
                if (!_parkPrepared)
                {
                    _parkPrepared = true;
                    // The SAME window covers source and centre. Establish its
                    // real cursor ownership here, before crossing to the centre.
                    _hiddenPointer.Prepare(Cursor);
                    _hiddenPointer.HidePointer();
                }
                else _hiddenPointer.RefreshCursor();
                // A return/local takeover can cancel parking before this post
                // runs. Never generate a fresh placement for an obsolete hide.
                if (_hideRequested && _parkPending && !_disposed)
                {
                    if (_hiddenPointer.IsCursorHidden)
                    {
                        _parkPending = false;
                        _visibilityRequested = false;
                        _motion.BeginTransition();
                        MoveCursor(_anchor);
                    }
                    else if (_milliseconds() >= _hideDeadline)
                        throw new Win32Exception("Windows did not hide the cursor at the exit point. Sharing stopped to preserve local control.");
                    else if (!_preflightQueued)
                    {
                        _preflightQueued = true;
                        Point source = Cursor;
                        Rectangle monitor = Screen.FromPoint(source).Bounds;
                        // A one-pixel tagged move stays under the source patch
                        // and makes Windows update actual input ownership.
                        int dx = source.X < monitor.Right - 1 ? 1 : -1;
                        MoveCursor(new Point(source.X + dx, source.Y));
                    }
                }
            }
            else TryRevealPointer();
        });
    }

    // Called by the input-loop timer too, so a lost placement notification
    // cannot leave a hidden window or local-input gate stuck indefinitely.
    internal void RecoverVisibility()
    {
        _output.CheckHealth();
        if (_disposed) return;
        if (_hideRequested)
        {
            _hiddenPointer?.RefreshCursor();
            if (_parkPending) QueueVisibilityRefresh();
        }
        else TryRevealPointer();
    }

    internal bool SuppressPhysicalMove(bool forwarding, bool stationary = false)
    {
        if (forwarding)
        {
            _hiddenPointer?.RefreshCursor();
            return PlacementPending;
        }
        if (stationary) return PlacementPending;
        if (!PlacementPending && !OutputPending && !_awaitingVisiblePlacement) return false;
        BeginCursorTransition();
        SetPointerHidden(false);
        return false;
    }

    private void TryRevealPointer()
    {
        if (_hiddenPointer is null || PlacementPending) return;
        // The low-level hook runs BEFORE Windows applies the placement. Its
        // acknowledgement is not proof that the pointer has left the centre.
        // Accept any position queued by this ownership: continuous remote input
        // may already have advanced past the initial entry/return placement.
        // Waiting for the entire output queue to empty can starve visibility.
        if (_awaitingVisiblePlacement && !_revealPositions.Contains(Cursor))
        {
            if (_milliseconds() >= _placementDeadline)
                throw new Win32Exception("The cursor did not reach its return position. Sharing stopped to restore local control.");
            return;
        }
        _awaitingVisiblePlacement = false;
        _revealPositions.Clear();
        RevealPointer();
    }

    private void RevealPointer()
    {
        _visibilityRequested = false;
        IPointerParkingWindow? pointer = _hiddenPointer;
        _hiddenPointer = null;
        pointer?.Dispose();
    }

    public Rectangle Bounds => SystemInformation.VirtualScreen;
    public Point Cursor => _readCursor();
    private static Point ReadCursor() => NativeMouse.GetCursorPos(out var position)
        ? new Point(position.X, position.Y) : throw new Win32Exception("Cannot read the mouse position.");
    public void MoveCursor(Point position)
    {
        Rectangle bounds = Bounds;
        if (_hideRequested || _hiddenPointer is not null)
        {
            if (!_awaitingVisiblePlacement)
                _placementDeadline = _milliseconds() + CursorMotionGuard.PlacementTimeoutMilliseconds;
            _awaitingVisiblePlacement = true;
            Point clamped = new(Math.Clamp(position.X, bounds.Left, bounds.Right - 1),
                Math.Clamp(position.Y, bounds.Top, bounds.Bottom - 1));
            Rectangle monitor = Screen.FromPoint(clamped).Bounds;
            // Windows snaps placements outside monitors (including gaps in a
            // virtual desktop) to a visible pixel. Verify that effective target.
            _revealPositions.Add(new Point(Math.Clamp(clamped.X, monitor.Left, monitor.Right - 1),
                Math.Clamp(clamped.Y, monitor.Top, monitor.Bottom - 1)));
        }
        _motion.MoveCursor(position, bounds, _output.Enqueue);
    }
    public void Inject(uint flags, int data) => NativeMouse.Inject(flags, data, _output.Enqueue);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _output.Dispose();
        RevealPointer();
    }
}
