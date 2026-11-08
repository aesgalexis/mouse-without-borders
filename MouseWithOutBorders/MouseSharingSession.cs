using System.ComponentModel;
using System.Drawing;

namespace MouseWithOutBorders;

internal interface IMouseDesktop
{
    Rectangle Bounds { get; }
    Point Cursor { get; }
    void BeginCursorTransition();
    void MoveCursor(Point position);
    void SetPointerHidden(bool hidden);
    void Inject(uint flags, int data);
}

/// <summary>Only release buttons for which this connection successfully injected a down.</summary>
internal sealed class RemoteMouseButtons
{
    private readonly HashSet<int> _held = [];
    private readonly long[] _revisions = new long[5];
    private bool _releasing;
    private static readonly (uint down, uint up, int data)[] Buttons = [
        (NativeMouse.LEFTDOWN, NativeMouse.LEFTUP, 0),
        (NativeMouse.RIGHTDOWN, NativeMouse.RIGHTUP, 0),
        (NativeMouse.MIDDLEDOWN, NativeMouse.MIDDLEUP, 0),
        (NativeMouse.XDOWN, NativeMouse.XUP, 1),
        (NativeMouse.XDOWN, NativeMouse.XUP, 2)
    ];

    internal static int Identify(uint flags, int data, out bool down)
    {
        for (int i = 0; i < Buttons.Length; i++)
        {
            var button = Buttons[i];
            if (data == button.data && (flags == button.down || flags == button.up))
            {
                down = flags == button.down;
                return i;
            }
        }
        down = false;
        return -1;
    }

    internal void Apply(uint flags, int data, Action<uint, int> inject)
    {
        if (flags is NativeMouse.XDOWN or NativeMouse.XUP && data == 3)
        {
            Apply(flags, 1, inject);
            Apply(flags, 2, inject);
            return;
        }
        int button = Identify(flags, data, out bool down);
        if (button < 0 || _held.Contains(button) == down) return;
        // SendInput can pump a nested hook/message loop. Reserve the state first
        // so a nested cleanup sees this down and cannot release the same up twice.
        long revision = ++_revisions[button];
        if (down) _held.Add(button);
        else _held.Remove(button);
        try { inject(flags, data); }
        catch
        {
            // A nested transition may already have released or replaced this
            // input. Only undo our reservation if it is still the latest one.
            if (_revisions[button] == revision)
            {
                if (down) _held.Remove(button);
                else _held.Add(button);
            }
            throw;
        }
    }

    internal void Release(Action<uint, int> inject, Action<Win32Exception> onError)
    {
        if (_releasing) return;
        _releasing = true;
        try
        {
            foreach (int button in _held.Order().ToArray())
            {
                var release = Buttons[button];
                try { Apply(release.up, release.data, inject); }
                catch (Win32Exception ex) { onError(ex); }
            }
        }
        finally { _releasing = false; }
    }
}

/// <summary>
/// A symmetric session: the connection role only chooses who sends the layout.
/// Input and ownership changes run on one input thread on each PC.
/// </summary>
internal sealed class MouseSharingSession : IDisposable
{
    private enum MouseLocation { Local, OtherPc, Receiving }
    private readonly IMouseDesktop _desktop;
    private readonly Func<MousePacket, bool> _send;
    private readonly Action<Win32Exception> _onInputError;
    private readonly Action<string> _status;
    private readonly Action? _onReady;
    private readonly int _side;
    private readonly RemoteMouseButtons _injectedButtons = new();
    private readonly HashSet<int> _physicalButtons = [];
    private readonly HashSet<int> _forwardedButtons = [];
    private readonly HashSet<int> _suppressRelease = [];
    private MouseLocation _location;
    private long _clock;
    private long _transfer;
    private int _origin;
    private Point _receivedPosition;
    private bool _started;
    private bool _disposed;

    internal bool Ready { get; private set; }
    internal bool ExitRight { get; private set; }
    internal bool IsRemote => _location == MouseLocation.OtherPc;
    internal bool IsReceiving => _location == MouseLocation.Receiving;

    internal MouseSharingSession(bool initiator, bool exitRight, IMouseDesktop desktop,
        Func<MousePacket, bool> send, Action<Win32Exception> onInputError, Action<string> status,
        IReadOnlyCollection<int>? initialPhysicalButtons = null, Action? onReady = null)
    {
        _side = initiator ? 0 : 1;
        ExitRight = exitRight;
        _desktop = desktop;
        _send = send;
        _onInputError = onInputError;
        _status = status;
        _onReady = onReady;
        if (initialPhysicalButtons is not null) _physicalButtons.UnionWith(initialPhysicalButtons);
    }

    internal void Start()
    {
        if (_disposed || _started) return;
        _started = true;
        if (_side == 0) Send(new MousePacket("layout", Data: ExitRight ? 1 : -1));
        ReportReady();
    }

    private void Send(MousePacket packet)
    {
        if (!_send(packet)) throw new IOException("Other PC disconnected.");
    }

    private MousePacket Stamp(MousePacket packet) => packet with { Transfer = _transfer, Origin = _origin };

    private double Ratio(int y) => Math.Clamp((double)(y - _desktop.Bounds.Top) / Math.Max(1, _desktop.Bounds.Height - 1), 0, 1);
    private int Y(double ratio) => _desktop.Bounds.Top +
        (int)Math.Round(Math.Clamp(ratio, 0, 1) * Math.Max(0, _desktop.Bounds.Height - 1));

    private void ReportReady()
    {
        if (_disposed) return;
        _status(Ready
            ? "Connected. Any mouse or touchpad on either PC can cross the shared edge."
            : "Preparing mouse sharing. Waiting for the other PC...");
    }

    private void MarkReady()
    {
        Ready = true;
        ReportReady();
        _onReady?.Invoke();
    }

    private void ReleaseInjected()
    {
        Win32Exception? failure = null;
        _injectedButtons.Release(_desktop.Inject, ex =>
        {
            failure ??= ex;
            _onInputError(ex);
        });
        // Keep ownership unchanged if Windows did not release a held button.
        // The host tears down the peer and retries cleanup instead of silently
        // leaving a drag pressed while continuing in another input ownership.
        if (failure is not null) throw failure;
    }

    private void RestoreLocal(double ratio)
    {
        _suppressRelease.UnionWith(_forwardedButtons);
        _forwardedButtons.Clear();
        _location = MouseLocation.Local;
        _desktop.BeginCursorTransition();
        Rectangle area = _desktop.Bounds;
        int inset = Math.Min(25, Math.Max(0, (area.Width - 1) / 2));
        _desktop.MoveCursor(new Point(ExitRight ? area.Right - 1 - inset : area.Left + inset, Y(ratio)));
        _desktop.SetPointerHidden(false);
        ReportReady();
    }

    private void TakeLocalControl(int y)
    {
        if (!IsReceiving) return;
        ReleaseInjected();
        _location = MouseLocation.Local;
        _desktop.BeginCursorTransition();
        Send(Stamp(new MousePacket("return", Ratio: Ratio(y))));
        ReportReady();
    }

    internal bool PhysicalMove(Point previous, Point position)
    {
        if (_disposed || !Ready) return false;
        int dx = position.X - previous.X;
        int dy = position.Y - previous.Y;
        bool stationary = dx == 0 && dy == 0;
        if (stationary && (IsRemote || IsReceiving)) return IsRemote;
        if (!IsRemote && !stationary) _desktop.SetPointerHidden(false);
        TakeLocalControl(position.Y);
        if (IsRemote)
        {
            Send(Stamp(new MousePacket("move", Dx: dx, Dy: dy)));
            return true;
        }

        Rectangle area = _desktop.Bounds;
        bool crossing = ExitRight ? (dx > 0 || stationary) && position.X >= area.Right - 1
            : (dx < 0 || stationary) && position.X <= area.Left;
        // Finish a local click/drag on its original PC before handing off.
        if (!crossing || _physicalButtons.Count != 0 || position.Y < area.Top || position.Y >= area.Bottom)
            return false;

        _transfer = checked(++_clock);
        _origin = _side;
        _location = MouseLocation.OtherPc;
        _desktop.BeginCursorTransition();
        _desktop.SetPointerHidden(true);
        // Suppressing moves at a central anchor also works for devices that only
        // produce Windows pointer events, including built-in touchpads.
        Send(Stamp(new MousePacket("enter", Data: ExitRight ? 1 : -1, Ratio: Ratio(position.Y))));
        _status("Mouse is on the other PC. Cross its shared edge to return.");
        return true;
    }

    internal bool PhysicalButton(uint flags, int data)
    {
        if (_disposed) return false;
        if (flags is NativeMouse.XDOWN or NativeMouse.XUP && data == 3)
        {
            bool first = PhysicalButton(flags, 1);
            return PhysicalButton(flags, 2) || first;
        }
        int button = RemoteMouseButtons.Identify(flags, data, out bool down);
        if (button < 0) return IsRemote;
        if (down)
        {
            _physicalButtons.Add(button);
            // A fresh down starts a new local click even if an old forwarded
            // press never produced its expected physical release.
            _suppressRelease.Remove(button);
        }
        else _physicalButtons.Remove(button);
        if (!Ready) return false;
        if (!IsRemote) _desktop.SetPointerHidden(false);
        if (!down && _suppressRelease.Remove(button)) return true;
        TakeLocalControl(_desktop.Cursor.Y);
        if (!IsRemote) return false;
        if (down) _forwardedButtons.Add(button);
        else if (!_forwardedButtons.Remove(button)) return true;
        Send(Stamp(new MousePacket("button", Data: data, Flags: flags)));
        return true;
    }

    internal bool PhysicalWheel(uint flags, int delta)
    {
        if (_disposed || !Ready) return false;
        if (!IsRemote) _desktop.SetPointerHidden(false);
        TakeLocalControl(_desktop.Cursor.Y);
        if (!IsRemote) return false;
        Send(Stamp(new MousePacket("wheel", Data: delta, Flags: flags)));
        return true;
    }

    internal void Receive(MousePacket packet)
    {
        if (_disposed || !_started) return;
        if (packet.Kind == "layout")
        {
            if (_side == 1 && !Ready && packet.Data is 1 or -1)
            {
                ExitRight = packet.Data < 0;
                // Confirm that this PC's input loop has accepted the layout.
                // A transport connection alone must not enable remote capture.
                Send(new MousePacket("ready", Data: packet.Data));
                MarkReady();
            }
            return;
        }
        if (packet.Kind == "ready")
        {
            if (_side == 0 && !Ready && packet.Data == (ExitRight ? 1 : -1)) MarkReady();
            return;
        }
        if (!Ready || packet.Transfer <= 0 || packet.Transfer == long.MaxValue || packet.Origin is < 0 or > 1)
            return;
        _clock = Math.Max(_clock, packet.Transfer);
        if (packet.Kind == "enter")
        {
            if (packet.Origin == _side || packet.Data != (ExitRight ? -1 : 1) || !double.IsFinite(packet.Ratio))
                return;
            // Lamport order resolves simultaneous crossings identically at both
            // ends. Old moves, clicks and returns cannot affect a later handoff.
            if (packet.Transfer < _transfer || (packet.Transfer == _transfer && packet.Origin <= _origin))
                return;
            if (IsRemote) RestoreLocal(packet.Ratio);
            ReleaseInjected();
            _transfer = packet.Transfer;
            _origin = packet.Origin;
            _location = MouseLocation.Receiving;
            _desktop.BeginCursorTransition();
            Rectangle area = _desktop.Bounds;
            int inset = Math.Min(5, Math.Max(0, (area.Width - 1) / 2));
            _receivedPosition = new Point(ExitRight ? area.Right - 1 - inset : area.Left + inset, Y(packet.Ratio));
            _desktop.MoveCursor(_receivedPosition);
            _desktop.SetPointerHidden(false);
            _status("Mouse from the other PC is here. Your local mice and touchpad also work.");
            return;
        }
        if (packet.Transfer != _transfer || packet.Origin != _origin) return;
        if (packet.Kind == "return" && IsRemote && double.IsFinite(packet.Ratio))
        {
            RestoreLocal(packet.Ratio);
            return;
        }
        if (!IsReceiving) return;
        switch (packet.Kind)
        {
            case "move":
                if (Math.Abs((long)packet.Dx) > 10000 || Math.Abs((long)packet.Dy) > 10000) return;
                Rectangle area = _desktop.Bounds;
                long nextX = (long)_receivedPosition.X + packet.Dx;
                long nextY = (long)_receivedPosition.Y + packet.Dy;
                _receivedPosition = new Point((int)Math.Clamp(nextX, area.Left, area.Right - 1),
                    (int)Math.Clamp(nextY, area.Top, area.Bottom - 1));
                if (ExitRight ? packet.Dx > 0 && nextX >= area.Right : packet.Dx < 0 && nextX < area.Left)
                {
                    ReleaseInjected();
                    _location = MouseLocation.Local;
                    _desktop.BeginCursorTransition();
                    // Park invisibly while the other PC resumes control. Local
                    // device input will reveal this PC's pointer again.
                    _desktop.SetPointerHidden(true);
                    Send(Stamp(new MousePacket("return", Ratio: Ratio(_receivedPosition.Y))));
                    ReportReady();
                }
                else _desktop.MoveCursor(_receivedPosition);
                break;
            case "button":
                _injectedButtons.Apply(packet.Flags, packet.Data, _desktop.Inject);
                break;
            case "wheel":
                if (packet.Flags is NativeMouse.WHEEL or NativeMouse.HWHEEL && packet.Data is >= -12000 and <= 12000)
                    _desktop.Inject(packet.Flags, packet.Data);
                break;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { ReleaseInjected(); }
        finally
        {
            try { if (IsRemote) RestoreLocal(0.5); }
            finally
            {
                try { _desktop.SetPointerHidden(false); }
                finally { _location = MouseLocation.Local; }
            }
        }
    }
}
