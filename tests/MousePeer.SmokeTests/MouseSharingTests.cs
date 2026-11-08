using System.ComponentModel;
using System.Drawing;

namespace MouseWithOutBorders;

internal static class MouseSharingTests
{
    internal static void Run()
    {
        TestSharingReadiness();
        TestFailedReadyAcknowledgement();
        TestNoPhantomClicks();
        TestReentrantButtonInjection();
        TestButtonInjectionFailures();
        TestBothDirections(true);
        TestBothDirections(false);
        TestLocalTakeover();
        TestSimultaneousCrossings();
        TestHeldButtons();
        TestButtonsHeldAtStartup();
        TestFreshClickAfterReturn();
        TestFailedReleaseKeepsOwnership();
        TestStalePackets();
        TestPointerRestoration();
        TestPointerInput();
        TestDelayedCursorPlacements();
        Console.WriteLine("PASS: no phantom releases, both mice/directions, takeover, crossing races, stale packets and pointer input.");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    private sealed class Desktop : IMouseDesktop
    {
        public Rectangle Bounds { get; } = new(-500, 0, 1000, 700);
        public Point Cursor { get; set; } = new(0, 300);
        internal bool PointerHidden;
        internal readonly CursorMotionGuard Motion = new();
        internal bool DelayPlacements;
        internal readonly List<(Point position, NativeMouse.HookInfo input)> Placements = [];
        public void BeginCursorTransition() => Motion.BeginTransition();
        public void SetPointerHidden(bool hidden)
        {
            PointerHidden = hidden;
            if (hidden) MoveCursor(new Point(Bounds.Left + Bounds.Width / 2, Bounds.Top + Bounds.Height / 2));
        }
        internal readonly List<(uint flags, int data)> Injected = [];
        internal Action<uint, int>? Injecting;
        public void MoveCursor(Point point)
        {
            if (!DelayPlacements) { Cursor = point; return; }
            Motion.MoveCursor(point, Bounds, (_, inputs, _) =>
            {
                Placements.Add((point, new NativeMouse.HookInfo { ExtraInfo = inputs[0].Data.Mouse.ExtraInfo }));
                return 1;
            });
        }
        internal void ApplyPlacement(int index)
        {
            var placement = Placements[index];
            if (!Motion.SuppressInjectedMove(placement.input)) Cursor = placement.position;
        }
        public void Inject(uint flags, int data)
        {
            Injecting?.Invoke(flags, data);
            Injected.Add((flags, data));
        }
    }

    private sealed class Pair : IDisposable
    {
        internal readonly Desktop[] Desktops = [new(), new()];
        internal readonly MouseSharingSession[] Sessions;
        internal readonly Queue<(int from, MousePacket packet)> Packets = new();
        internal readonly List<(int from, MousePacket packet)> Sent = [];
        private readonly Action<Win32Exception> _onInputError;

        internal Pair(bool right = true, Action<Win32Exception>? onInputError = null)
        {
            _onInputError = onInputError ?? (ex => throw new Exception("Unexpected input error.", ex));
            Sessions = [Create(0, right), Create(1, right)];
            Sessions[0].Start();
            Sessions[1].Start();
            Flush();
        }

        private MouseSharingSession Create(int side, bool right) => new(side == 0, right, Desktops[side], packet =>
        {
            Packets.Enqueue((side, packet));
            Sent.Add((side, packet));
            return true;
        }, _onInputError, _ => { });

        internal void Flush()
        {
            int limit = 1000;
            while (Packets.TryDequeue(out var entry))
            {
                if (--limit == 0) throw new Exception("Mouse packets are feeding back into the connection.");
                Sessions[1 - entry.from].Receive(entry.packet);
            }
        }

        internal void Move(int side, int dx, int dy = 0, bool flush = true)
        {
            Desktop desktop = Desktops[side];
            Point previous = desktop.Cursor;
            Point next = new(Math.Clamp(previous.X + dx, desktop.Bounds.Left, desktop.Bounds.Right - 1),
                Math.Clamp(previous.Y + dy, desktop.Bounds.Top, desktop.Bounds.Bottom - 1));
            if (!Sessions[side].PhysicalMove(previous, next)) desktop.Cursor = next;
            if (flush) Flush();
        }

        internal void Cross(int side, bool flush = true)
        {
            Desktop desktop = Desktops[side];
            desktop.Cursor = new(Sessions[side].ExitRight ? desktop.Bounds.Right - 2 : desktop.Bounds.Left + 1, 300);
            Move(side, Sessions[side].ExitRight ? 4 : -4, flush: flush);
        }

        internal void Button(int side, uint flags, int data = 0)
        {
            Sessions[side].PhysicalButton(flags, data);
            Flush();
        }

        public void Dispose()
        {
            foreach (MouseSharingSession session in Sessions) session.Dispose();
        }
    }

    private static void TestSharingReadiness()
    {
        foreach (bool right in new[] { false, true })
        {
            Desktop[] desktops = [new(), new()];
            List<MousePacket>[] sent = [[], []];
            List<string>[] statuses = [[], []];
            int[] notifications = [0, 0];
            using MouseSharingSession initiator = new(true, right, desktops[0], packet =>
            {
                sent[0].Add(packet);
                return true;
            }, ex => throw ex, statuses[0].Add, onReady: () => notifications[0]++);
            using MouseSharingSession receiver = new(false, right, desktops[1], packet =>
            {
                sent[1].Add(packet);
                return true;
            }, ex => throw ex, statuses[1].Add, onReady: () => notifications[1]++);
            int direction = right ? 1 : -1;
            initiator.Receive(new MousePacket("ready", Data: direction));
            receiver.Receive(new MousePacket("layout", Data: direction));
            Assert(!initiator.Ready && !receiver.Ready && notifications.Sum() == 0,
                "Packets received before input startup must not announce readiness.");
            initiator.Start();
            receiver.Start();
            Assert(!initiator.Ready && !receiver.Ready && sent[0].Single().Kind == "layout" && sent[1].Count == 0,
                "Starting the local input loop must wait for the remote layout or acknowledgement.");
            foreach (MouseSharingSession session in new[] { initiator, receiver })
            {
                Point edge = new(right ? desktops[0].Bounds.Right - 1 : desktops[0].Bounds.Left, 300);
                Assert(!session.PhysicalMove(new Point(edge.X - direction, edge.Y), edge) &&
                    !session.PhysicalButton(NativeMouse.LEFTDOWN, 0) &&
                    !session.PhysicalButton(NativeMouse.LEFTUP, 0) &&
                    !session.PhysicalWheel(NativeMouse.WHEEL, 120) && !session.IsRemote,
                    "An unconfirmed session must keep movement, clicks and scrolling local.");
                session.Receive(new MousePacket("enter", Data: direction, Transfer: 1));
                Assert(!session.IsReceiving, "An unconfirmed session must not accept remote input.");
            }
            initiator.Receive(new MousePacket("ready", Data: 0));
            initiator.Receive(new MousePacket("ready", Data: -direction));
            receiver.Receive(new MousePacket("ready", Data: direction));
            receiver.Receive(new MousePacket("layout", Data: 0));
            Assert(!initiator.Ready && !receiver.Ready && notifications.Sum() == 0 &&
                statuses.SelectMany(values => values).All(status => !status.StartsWith("Connected.")) &&
                desktops.All(desktop => !desktop.PointerHidden && desktop.Injected.Count == 0),
                "Invalid readiness packets must not enable sharing, hide a cursor or report Connected.");

            MousePacket layout = sent[0].Single();
            receiver.Receive(layout);
            Assert(receiver.Ready && !initiator.Ready && notifications.SequenceEqual(new[] { 0, 1 }) &&
                sent[1].Single() == new MousePacket("ready", Data: direction),
                "The receiver must acknowledge its accepted layout before the initiator becomes ready.");
            initiator.Receive(sent[1].Single());
            Assert(initiator.Ready && receiver.ExitRight != right && notifications.SequenceEqual(new[] { 1, 1 }) &&
                statuses.All(values => values.Last().StartsWith("Connected.")),
                "Both input loops must announce readiness after the matching acknowledgement.");
            initiator.Start();
            receiver.Start();
            receiver.Receive(layout with { Data = -direction });
            initiator.Receive(sent[1].Single());
            Assert(receiver.ExitRight != right && notifications.SequenceEqual(new[] { 1, 1 }) &&
                sent.All(packets => packets.Count == 1),
                "Duplicate starts, layouts and acknowledgements must not restart or alter a ready session.");
        }
    }

    private static void TestFailedReadyAcknowledgement()
    {
        int notifications = 0;
        List<string> statuses = [];
        using MouseSharingSession receiver = new(false, true, new Desktop(), _ => false,
            ex => throw ex, statuses.Add, onReady: () => notifications++);
        receiver.Start();
        bool failed = false;
        try { receiver.Receive(new MousePacket("layout", Data: 1)); }
        catch (IOException) { failed = true; }
        Assert(failed && !receiver.Ready && notifications == 0 &&
            statuses.All(status => !status.StartsWith("Connected.")),
            "A failed readiness acknowledgement must fail the session without claiming it is connected.");
    }

    private static void TestNoPhantomClicks()
    {
        List<(uint, int)> events = [];
        RemoteMouseButtons buttons = new();
        buttons.Release((flags, data) => events.Add((flags, data)), _ => { });
        buttons.Apply(NativeMouse.RIGHTUP, 0, (flags, data) => events.Add((flags, data)));
        buttons.Apply(NativeMouse.XDOWN, 4, (flags, data) => events.Add((flags, data)));
        Assert(events.Count == 0, "Cleanup or an unmatched button-up generated a phantom click.");
        buttons.Apply(NativeMouse.RIGHTDOWN, 0, (flags, data) => events.Add((flags, data)));
        buttons.Apply(NativeMouse.RIGHTUP, 0, (flags, data) => events.Add((flags, data)));
        buttons.Release((flags, data) => events.Add((flags, data)), _ => { });
        Assert(events.SequenceEqual(new[] { (NativeMouse.RIGHTDOWN, 0), (NativeMouse.RIGHTUP, 0) }),
            "A real right click was duplicated by cleanup.");

        using Pair pair = new();
        for (int round = 0; round < 30; round++)
        {
            pair.Cross(0);
            pair.Move(0, -100);
            Assert(!pair.Sessions[0].IsRemote && !pair.Sessions[1].IsReceiving, "Mouse did not return.");
        }
        Assert(pair.Desktops.All(desktop => desktop.Injected.Count == 0), "Crossing without clicks injected button events.");
    }

    private static void TestBothDirections(bool right)
    {
        using Pair pair = new(right);
        Assert(pair.Sessions[0].Ready && pair.Sessions[1].Ready && pair.Sessions[1].ExitRight != right,
            "Both PCs must share the same physical screen layout.");
        for (int side = 0; side < 2; side++)
        {
            pair.Cross(side);
            Assert(pair.Desktops[side].PointerHidden && !pair.Desktops[1 - side].PointerHidden,
                "The departed PC must hide its pointer while the destination stays visible.");
            Assert(pair.Sessions[side].IsRemote && pair.Sessions[1 - side].IsReceiving,
                "A local mouse could not cross from one of the two PCs.");
            int direction = pair.Sessions[side].ExitRight ? 1 : -1;
            Point before = pair.Desktops[1 - side].Cursor;
            pair.Move(side, direction * 30, 17);
            Assert(pair.Desktops[1 - side].Cursor == new Point(before.X + direction * 30, before.Y + 17),
                "Remote movement has the wrong direction or scale.");
            pair.Button(side, NativeMouse.LEFTDOWN);
            pair.Button(side, NativeMouse.LEFTUP);
            pair.Sessions[side].PhysicalWheel(NativeMouse.WHEEL, -120);
            pair.Flush();
            Assert(pair.Desktops[1 - side].Injected.TakeLast(3).SequenceEqual(new[] {
                (NativeMouse.LEFTDOWN, 0), (NativeMouse.LEFTUP, 0), (NativeMouse.WHEEL, -120) }),
                "Clicks or scrolling did not work in both directions.");
            pair.Move(side, -direction * 150);
            Assert(!pair.Sessions[side].IsRemote && !pair.Sessions[1 - side].IsReceiving,
                "The mouse could not return from one of the two PCs.");
            Assert(!pair.Desktops[side].PointerHidden && pair.Desktops[1 - side].PointerHidden,
                "Returning must reveal the source and hide the cursor on the PC just left.");
        }
        for (int side = 0; side < 2; side++)
        {
            Rectangle area = pair.Desktops[side].Bounds;
            pair.Desktops[side].Cursor = new Point(pair.Sessions[side].ExitRight ? area.Right - 1 : area.Left, 300);
            pair.Move(side, 0);
            Assert(pair.Sessions[side].IsRemote, "Pushing a mouse already at the shared edge did not cross.");
            int direction = pair.Sessions[side].ExitRight ? 1 : -1;
            pair.Move(side, direction * 20);
            pair.Button(side, NativeMouse.XDOWN, 3);
            pair.Button(side, NativeMouse.XUP, 3);
            Assert(pair.Desktops[1 - side].Injected.TakeLast(4).SequenceEqual(new[] {
                (NativeMouse.XDOWN, 1), (NativeMouse.XDOWN, 2), (NativeMouse.XUP, 1), (NativeMouse.XUP, 2) }),
                "Combined thumb-button events did not work.");
            pair.Move(side, -direction * 100);
        }
    }

    private static void TestReentrantButtonInjection()
    {
        RemoteMouseButtons buttons = new();
        List<(uint flags, int data)> events = [];
        void Inject(uint flags, int data)
        {
            events.Add((flags, data));
            buttons.Release(Inject, ex => throw ex);
        }
        buttons.Apply(NativeMouse.LEFTDOWN, 0, Inject);
        buttons.Release(Inject, ex => throw ex);
        Assert(events.SequenceEqual(new[] { (NativeMouse.LEFTDOWN, 0), (NativeMouse.LEFTUP, 0) }),
            "Nested native input processing missed a down or duplicated a release.");

        buttons = new();
        events.Clear();
        try { buttons.Apply(NativeMouse.LEFTDOWN, 0, (flags, data) =>
        {
            events.Add((flags, data));
            buttons.Release((release, value) => events.Add((release, value)), ex => throw ex);
            throw new Win32Exception(5);
        }); }
        catch (Win32Exception) { }
        buttons.Release((flags, data) => events.Add((flags, data)), ex => throw ex);
        Assert(events.Count == 2, "An outer injection failure undid a nested successful release.");

        events.Clear();
        buttons.Apply(NativeMouse.LEFTDOWN, 0, (flags, data) => events.Add((flags, data)));
        try { buttons.Apply(NativeMouse.LEFTUP, 0, (flags, data) =>
        {
            events.Add((flags, data));
            buttons.Apply(NativeMouse.LEFTDOWN, 0, (nested, value) => events.Add((nested, value)));
            throw new Win32Exception(5);
        }); }
        catch (Win32Exception) { }
        buttons.Release((flags, data) => events.Add((flags, data)), ex => throw ex);
        Assert(events.Select(input => input.flags).SequenceEqual(new[] {
            NativeMouse.LEFTDOWN, NativeMouse.LEFTUP, NativeMouse.LEFTDOWN, NativeMouse.LEFTUP }),
            "An outer injection failure lost a nested replacement press.");
    }

    private static void TestButtonInjectionFailures()
    {
        RemoteMouseButtons buttons = new();
        List<(uint flags, int data)> events = [];
        int failures = 0;
        try { buttons.Apply(NativeMouse.LEFTDOWN, 0, (_, _) => throw new Win32Exception(5)); }
        catch (Win32Exception) { failures++; }
        buttons.Release((flags, data) => events.Add((flags, data)), _ => failures++);
        Assert(events.Count == 0 && failures == 1, "A rejected down was treated as a held button.");

        buttons.Apply(NativeMouse.LEFTDOWN, 0, (flags, data) => events.Add((flags, data)));
        buttons.Apply(NativeMouse.RIGHTDOWN, 0, (flags, data) => events.Add((flags, data)));
        buttons.Release((flags, data) =>
        {
            if (flags == NativeMouse.LEFTUP) throw new Win32Exception(5);
            events.Add((flags, data));
        }, _ => failures++);
        buttons.Release((flags, data) => events.Add((flags, data)), _ => failures++);
        Assert(failures == 2 && events.SequenceEqual(new[] {
            (NativeMouse.LEFTDOWN, 0), (NativeMouse.RIGHTDOWN, 0),
            (NativeMouse.RIGHTUP, 0), (NativeMouse.LEFTUP, 0) }),
            "A failed release prevented other cleanup or was not retried.");
    }

    private static void TestButtonsHeldAtStartup()
    {
        foreach (int held in Enumerable.Range(0, 5))
        {
            Desktop desktop = new();
            using MouseSharingSession session = new(true, true, desktop, _ => true, ex => throw ex,
                _ => { }, [held]);
            session.Start();
            session.Receive(new MousePacket("ready", Data: 1));
            Point edge = new(desktop.Bounds.Right - 1, 300);
            Assert(!session.PhysicalMove(new Point(edge.X - 1, edge.Y), edge) && !session.IsRemote,
                "A button held before startup allowed a local drag to cross PCs.");
            uint up = held switch {
                0 => NativeMouse.LEFTUP, 1 => NativeMouse.RIGHTUP, 2 => NativeMouse.MIDDLEUP,
                _ => NativeMouse.XUP
            };
            int data = held >= 3 ? held - 2 : 0;
            Assert(!session.PhysicalButton(up, data), "The original local drag release was swallowed.");
            Assert(session.PhysicalMove(edge, edge) && session.IsRemote,
                "Releasing a button held at startup did not allow crossing again.");
        }
    }

    private static void TestFreshClickAfterReturn()
    {
        using Pair pair = new();
        pair.Cross(0);
        pair.Button(0, NativeMouse.LEFTDOWN);
        pair.Move(0, -100);
        Assert(!pair.Sessions[0].PhysicalButton(NativeMouse.LEFTDOWN, 0) &&
            !pair.Sessions[0].PhysicalButton(NativeMouse.LEFTUP, 0),
            "An old suppressed release swallowed the up belonging to a fresh local click.");
    }

    private static void TestFailedReleaseKeepsOwnership()
    {
        int errors = 0;
        using Pair pair = new(onInputError: _ => errors++);
        pair.Cross(0);
        pair.Button(0, NativeMouse.LEFTDOWN);
        pair.Desktops[1].Injecting = (flags, _) =>
        {
            if (flags == NativeMouse.LEFTUP) throw new Win32Exception(5);
        };
        bool failed = false;
        try { pair.Move(0, -100); }
        catch (Win32Exception) { failed = true; }
        Assert(failed && errors == 1 && pair.Sessions[0].IsRemote && pair.Sessions[1].IsReceiving &&
            !pair.Desktops[1].PointerHidden,
            "A failed button release silently handed off ownership with a drag still pressed.");
        pair.Desktops[1].Injecting = null;
        pair.Sessions[1].Dispose();
        Assert(pair.Desktops[1].Injected.TakeLast(1).Single() == (NativeMouse.LEFTUP, 0),
            "Session teardown did not retry a failed release.");

        Desktop desktop = new();
        MouseSharingSession session = new(false, false, desktop, _ => true, _ => { }, _ => { });
        session.Start();
        session.Receive(new MousePacket("layout", Data: 1));
        session.Receive(new MousePacket("enter", Data: 1, Transfer: 1));
        session.Receive(new MousePacket("button", Flags: NativeMouse.LEFTDOWN, Transfer: 1));
        desktop.PointerHidden = true;
        desktop.Injecting = (_, _) => throw new Win32Exception(5);
        try { session.Dispose(); }
        catch (Win32Exception) { }
        Assert(!desktop.PointerHidden && !session.IsReceiving && !session.IsRemote,
            "Cleanup failure left the cursor hidden or the session active.");
    }

    private static void TestLocalTakeover()
    {
        using Pair pair = new();
        pair.Cross(0);
        pair.Move(1, 30);
        Assert(!pair.Sessions[0].IsRemote && !pair.Sessions[1].IsReceiving,
            "Moving the receiver's physical mouse did not restore local control.");
        Assert(pair.Desktops.All(desktop => !desktop.PointerHidden), "Local takeover did not restore the source pointer.");
        pair.Cross(1);
        Assert(pair.Sessions[1].IsRemote && pair.Sessions[0].IsReceiving,
            "The second PC's mouse could not cross after taking control.");
        pair.Button(0, NativeMouse.LEFTDOWN);
        pair.Button(0, NativeMouse.LEFTUP);
        Assert(!pair.Sessions[1].IsRemote && !pair.Sessions[0].IsReceiving,
            "A local click did not reclaim the PC from remote control.");
    }

    private static void TestSimultaneousCrossings()
    {
        using Pair pair = new();
        pair.Cross(0, flush: false);
        pair.Cross(1, flush: false);
        pair.Flush();
        Assert(pair.Sessions.Count(session => session.IsRemote) == 1 &&
            pair.Sessions.Count(session => session.IsReceiving) == 1,
            "Simultaneous crossings locked both PCs or lost both mice.");
        Assert(pair.Desktops.Count(desktop => desktop.PointerHidden) == 1, "Simultaneous crossings hid the wrong number of pointers.");
        int side = pair.Sessions[0].IsRemote ? 0 : 1;
        pair.Move(side, pair.Sessions[side].ExitRight ? 10 : -10, 20);
        Assert(pair.Sessions[side].IsRemote && pair.Sessions[1 - side].IsReceiving,
            "The winning mouse lost control after simultaneous crossings.");
    }

    private static void TestHeldButtons()
    {
        using Pair pair = new();
        pair.Button(0, NativeMouse.LEFTDOWN);
        pair.Cross(0);
        Assert(!pair.Sessions[0].IsRemote, "A local drag was split between two PCs.");
        pair.Button(0, NativeMouse.LEFTUP);
        pair.Cross(0);
        pair.Button(0, NativeMouse.LEFTDOWN);
        pair.Move(0, -100);
        Assert(pair.Desktops[1].Injected.SequenceEqual(new[] { (NativeMouse.LEFTDOWN, 0), (NativeMouse.LEFTUP, 0) }),
            "Returning did not release exactly the remote button held by MWB.");
        Assert(pair.Sessions[0].PhysicalButton(NativeMouse.LEFTUP, 0),
            "Releasing a button after returning generated an unmatched local release.");
        Assert(!pair.Sessions[0].PhysicalButton(NativeMouse.LEFTUP, 0), "Release suppression did not clear.");

        pair.Cross(1);
        pair.Button(1, NativeMouse.XDOWN, 2);
        pair.Sessions[0].Dispose();
        Assert(pair.Desktops[0].Injected.TakeLast(2).SequenceEqual(new[] { (NativeMouse.XDOWN, 2), (NativeMouse.XUP, 2) }),
            "Disconnect did not release the held X button.");
        Assert(!pair.Desktops[0].Injected.Any(input => input.flags == NativeMouse.RIGHTUP),
            "Disconnect generated an unpressed right-button release.");
    }

    private static void TestStalePackets()
    {
        using Pair pair = new();
        pair.Cross(0);
        MousePacket oldEnter = pair.Sent.Last(entry => entry.packet.Kind == "enter").packet;
        pair.Move(0, -100);
        pair.Cross(1);
        pair.Sessions[1].Receive(new MousePacket("return", Ratio: 0.5, Transfer: oldEnter.Transfer, Origin: oldEnter.Origin));
        Assert(pair.Sessions[1].IsRemote, "An old return cancelled a newer crossing.");
        int before = pair.Desktops[1].Injected.Count;
        pair.Sessions[1].Receive(oldEnter with { Kind = "button", Flags = NativeMouse.RIGHTDOWN });
        Assert(pair.Desktops[1].Injected.Count == before, "A stale button packet generated a click.");
        pair.Sessions[0].Receive(oldEnter with { Kind = "button", Flags = NativeMouse.RIGHTDOWN });
        Assert(pair.Desktops[0].Injected.Count == 0, "An old packet affected the new mouse owner.");
    }

    private static void TestPointerRestoration()
    {
        using Pair pair = new();
        pair.Cross(0);
        pair.Move(0, -100);
        Assert(pair.Desktops[1].PointerHidden, "Leaving the receiving PC left its ghost pointer visible.");
        pair.Move(1, 10);
        Assert(!pair.Desktops[1].PointerHidden, "Local movement did not reveal the hidden pointer.");
        pair.Cross(1);
        pair.Sessions[1].Dispose();
        Assert(!pair.Desktops[1].PointerHidden, "Disconnect left the source pointer hidden.");
        pair.Desktops[0].PointerHidden = true;
        pair.Sessions[0].Dispose();
        Assert(!pair.Desktops[0].PointerHidden, "Cleanup did not restore a hidden idle pointer.");
    }

    private static void TestPointerInput()
    {
        Assert(!NativeMouse.IsOurInput(new NativeMouse.HookInfo { Flags = 1, ExtraInfo = (IntPtr)123 }),
            "Windows-generated touchpad/accessibility input was discarded.");
        Assert(NativeMouse.IsOurInput(new NativeMouse.HookInfo { Flags = 1, ExtraInfo = NativeMouse.InputMarker }),
            "MWB input would feed back into the other PC.");
        NativeMouse.MoveCursor(new Point(-500, 0), new Rectangle(-500, 0, 1000, 700), (_, inputs, _) =>
        {
            var mouse = inputs[0].Data.Mouse;
            Assert(mouse.Flags == (NativeMouse.MOVE | NativeMouse.ABSOLUTE | NativeMouse.VIRTUALDESK) &&
                mouse.ExtraInfo == NativeMouse.InputMarker && mouse.Dx is >= 0 and < 66 && mouse.Dy is >= 0 and < 94,
                "Absolute cursor input did not map the virtual desktop or include the feedback marker.");
            return 1;
        });
    }

    private static void TestDelayedCursorPlacements()
    {
        foreach (bool right in new[] { true, false })
        foreach (int side in new[] { 0, 1 })
        {
            using Pair pair = new(right);
            Desktop source = pair.Desktops[side];
            source.DelayPlacements = true;
            pair.Cross(side);
            MousePacket enter = pair.Sent.Last(entry => entry.from == side && entry.packet.Kind == "enter").packet;
            Assert(source.Motion.PlacementPending, "Parking must wait for its native acknowledgement.");
            pair.Sessions[side].Receive(enter with { Kind = "return", Ratio = 0.23 });
            Assert(!pair.Sessions[side].IsRemote && source.Motion.PlacementPending,
                "Returning must wait for the edge placement, even before parking has completed.");
            source.ApplyPlacement(0);
            Assert(source.Motion.PlacementPending, "An old parking move must not acknowledge the return placement.");
            source.ApplyPlacement(1);
            int x = pair.Sessions[side].ExitRight ? source.Bounds.Right - 26 : source.Bounds.Left + 25;
            Point expected = new(x, (int)Math.Round(0.23 * (source.Bounds.Height - 1)));
            Assert(source.Cursor == expected && !source.Motion.PlacementPending,
                "Returning must restore the shared edge and preserve vertical position.");
            source.ApplyPlacement(0);
            Assert(source.Cursor == expected, "A late central parking move overwrote the return position.");

            // Receiving a new owner must also cancel queued motion from the old owner.
            Desktop receiver = pair.Desktops[1 - side];
            receiver.DelayPlacements = true;
            pair.Sessions[1 - side].Receive(enter with { Kind = "move", Dy = 3 });
            Assert(receiver.Placements.Count == 1, "The receiver must have a queued remote move.");
            Point local = receiver.Cursor;
            pair.Sessions[1 - side].PhysicalButton(NativeMouse.LEFTDOWN, 0);
            receiver.ApplyPlacement(0);
            Assert(receiver.Cursor == local, "A queued remote move overrode local takeover.");
        }

        CursorMotionGuard motion = new();
        List<NativeMouse.HookInfo> inputs = [];
        uint Capture(uint _, NativeMouse.Input[] input, int __)
        {
            inputs.Add(new NativeMouse.HookInfo { ExtraInfo = input[0].Data.Mouse.ExtraInfo });
            return 1;
        }
        Rectangle bounds = new(0, 0, 1920, 1080);
        motion.MoveCursor(new Point(10, 200), bounds, Capture);
        motion.MoveCursor(new Point(20, 200), bounds, Capture);
        Assert(inputs.All(NativeMouse.IsOurInput), "Generation-tagged cursor movements must never feed back.");
        Assert(inputs.All(info => info.ExtraInfo.ToInt64() is >= 0 and <= uint.MaxValue),
            "Ownership markers must survive Windows input paths that retain only 32 bits.");
        Assert(!motion.SuppressInjectedMove(inputs[0]) && !motion.SuppressInjectedMove(inputs[1]),
            "Moves in the same ownership must retain their order before button events.");
        motion.BeginTransition();
        Assert(!motion.PlacementPending && motion.SuppressInjectedMove(inputs[1]),
            "Taking local control must cancel old motion without waiting for an unnecessary warp.");
        Console.WriteLine("PASS: delayed parking/return placements, edge and height restoration in both layouts/PCs, stale remote motion and ordered current input.");
    }
}
