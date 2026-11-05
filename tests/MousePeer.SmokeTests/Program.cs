using System.ComponentModel;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace MouseWithOutBorders;

internal static class Program
{
    private static async Task Main()
    {
        Console.WriteLine("Mouse transport and input smoke tests...");

        TestPairingCodes();
        Console.WriteLine("PASS: 160-bit codes, input normalization, legacy rejection and saved-code migration.");
        await SecureTransportTests.RunAsync();
        MouseSharingTests.Run();
        CursorMotionGuardTests.Run();
        await BackpressureTests.RunAsync();
        await ClipboardTransportTests.RunAsync();
        byte[] key = MousePeer.ParseKey(MousePeer.NewKey());
        await TestAuthenticatedRoundTrip(key);
        Console.WriteLine("PASS: authenticated mouse data roundtrip.");
        await TestIncorrectKeyRejected(key);
        Console.WriteLine("PASS: incorrect pairing key denied.");
        await TestStalledPairing(key, afterProof: false);
        Console.WriteLine("PASS: silent receiver times out and its socket is closed.");
        await TestStalledPairing(key, afterProof: true);
        Console.WriteLine("PASS: stalled pairing response times out and its socket is closed.");
        await TestPairingCancellation(key);
        Console.WriteLine("PASS: user cancellation is preserved during pairing.");
        await TestOldVersionRejected(key, "MOUSE1");
        await TestOldVersionRejected(key, "MOUSE2");
        Console.WriteLine("PASS: an older app gets a clear update-both-PCs error and its socket is closed.");
        TestMouseInjection();
        Console.WriteLine("PASS: mouse events are encoded correctly and rejected input reports an error.");
        TestButtonReleaseFailures();
        Console.WriteLine("PASS: all buttons are released even when Windows rejects earlier releases.");

        Console.WriteLine("All smoke tests passed.");
    }

    private static void TestPairingCodes()
    {
        for (int i = 0; i < 100; i++)
        {
            string code = MousePeer.NewKey();
            if (!code.StartsWith("MWB3-", StringComparison.Ordinal) || code.Length != 40 ||
                MousePeer.ParseKey(code).Any(b => "01IO".Contains((char)b)) || MousePeer.ParseKey(code).Length != 32)
                throw new Exception("Generated pairing code is not 32 unambiguous random characters with a version prefix.");
        }

        string generated = MousePeer.NewKey();
        if (!MousePeer.ParseKey("  " + generated.ToLowerInvariant() + "  ").SequenceEqual(MousePeer.ParseKey(MousePeer.NormalizeKey(generated))))
            throw new Exception("Typing lowercase or pasting a formatted code changes the key.");

        foreach (string invalid in new[] { "", "ABCD23XY", "0123456789ABCDEF0123456789ABCDEF", new string('Z', 32),
            "MWB3-" + new string('Z', 31), "MWB3-" + new string('Z', 33), "MWB3-" + new string('0', 32) })
        {
            try
            {
                MousePeer.ParseKey(invalid);
                throw new Exception("Invalid pairing code was accepted.");
            }
            catch (ArgumentException ex) when (ex.Message.Contains("MWB3", StringComparison.Ordinal)) { }
        }

        AppSettings settings = new()
        {
            PairingKey = "0123456789ABCDEF0123456789ABCDEF",
            Role = "Controller", Host = "127.0.0.1", RemoteKey = "FEDCBA9876543210FEDCBA9876543210",
            AutoConnect = true, ExitEdge = "Left"
        };
        if (!settings.UpgradePairingCode() || MousePeer.ParseKey(settings.PairingKey).Length != 32)
            throw new Exception("Existing receiver did not migrate to a strong code.");
        string upgraded = settings.PairingKey;
        if (settings.UpgradePairingCode() || settings.PairingKey != upgraded || settings.Role != "Controller" ||
            settings.Host != "127.0.0.1" || settings.RemoteKey != "" ||
            !settings.AutoConnect || settings.ExitEdge != "Left")
            throw new Exception("Code migration changed preferences, retained a legacy remote secret or rotated a valid new code.");
        settings.RemoteKey = generated;
        if (settings.UpgradePairingCode() || settings.RemoteKey != generated)
            throw new Exception("Migration discarded a valid saved secure connection.");

        foreach (string? invalid in new[] { "", "ABCD0123", "ABCD23XY", null })
        {
            settings.PairingKey = invalid!;
            if (!settings.UpgradePairingCode() || MousePeer.ParseKey(settings.PairingKey).Length != 32)
                throw new Exception("Invalid saved code was not replaced.");
        }
    }

    private static async Task TestAuthenticatedRoundTrip(byte[] key)
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(12));
        TcpListener listener = new(IPAddress.Loopback, MousePeer.Port);
        listener.Start();
        try
        {
            TaskCompletionSource<MousePacket> gotMove = new(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource<MousePacket> gotReply = new(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource<MousePacket> gotReverseMove = new(TaskCreationOptions.RunContinuationsAsynchronously);

            Task server = Task.Run(async () =>
            {
                using TcpClient socket = await listener.AcceptTcpClientAsync(timeout.Token);
                using MousePeer peer = await MousePeer.AcceptAsync(socket, key, timeout.Token);
                await peer.ReadLoopAsync(packet =>
                {
                    if (packet.Kind == "move")
                    {
                        gotMove.TrySetResult(packet);
                        peer.Queue(new MousePacket("move", Dx: -9, Dy: 13, Transfer: 42, Origin: 1));
                        peer.Queue(new MousePacket("return", Ratio: 0.4, Transfer: packet.Transfer, Origin: packet.Origin));
                    }
                }, timeout.Token);
            }, timeout.Token);

            using MousePeer controller = await MousePeer.ConnectAsync("127.0.0.1", key, timeout.Token);
            Task read = controller.ReadLoopAsync(packet =>
            {
                if (packet.Kind == "return") gotReply.TrySetResult(packet);
                if (packet.Kind == "move") gotReverseMove.TrySetResult(packet);
            }, timeout.Token);

            if (!controller.Queue(new MousePacket("move", Dx: 26, Dy: -11, Transfer: 41, Origin: 0)))
                throw new Exception("Could not queue mouse move.");

            MousePacket move = await gotMove.Task.WaitAsync(timeout.Token);
            MousePacket reply = await gotReply.Task.WaitAsync(timeout.Token);
            MousePacket reverse = await gotReverseMove.Task.WaitAsync(timeout.Token);
            if (move.Dx != 26 || move.Dy != -11 || reply.Ratio != 0.4 || move.Transfer != 41 ||
                reply.Transfer != 41 || reverse.Dx != -9 || reverse.Dy != 13 || reverse.Transfer != 42 || reverse.Origin != 1)
                throw new Exception("Mouse packets were corrupted in transit.");

            timeout.Cancel();
            try { await Task.WhenAll(read, server); }
            catch (OperationCanceledException) { }
        }
        finally { listener.Stop(); }
    }

    private static async Task TestIncorrectKeyRejected(byte[] key)
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(12));
        TcpListener listener = new(IPAddress.Loopback, MousePeer.Port);
        listener.Start();
        try
        {
            Task server = Task.Run(async () =>
            {
                using TcpClient tcp = await listener.AcceptTcpClientAsync(timeout.Token);
                try
                {
                    using MousePeer _ = await MousePeer.AcceptAsync(tcp, key, timeout.Token);
                    throw new Exception("Receiver accepted a wrong pairing key.");
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException or IOException) { /* Client rejects the authenticated certificate pin. */ }
            }, timeout.Token);

            byte[] wrong = MousePeer.ParseKey(MousePeer.NewKey());
            try
            {
                using MousePeer _ = await MousePeer.ConnectAsync("127.0.0.1", wrong, timeout.Token);
                throw new Exception("Controller connected using wrong pairing key.");
            }
            catch (UnauthorizedAccessException) { /* expected */ }
            await server;
        }
        finally { listener.Stop(); }
    }

    private static async Task TestStalledPairing(byte[] key, bool afterProof)
    {
        using CancellationTokenSource watchdog = new(TimeSpan.FromSeconds(12));
        TcpListener listener = new(IPAddress.Loopback, MousePeer.Port);
        listener.Start();
        try
        {
            Task<MousePeer> connection = MousePeer.ConnectAsync("127.0.0.1", key, watchdog.Token);
            using TcpClient socket = await listener.AcceptTcpClientAsync(watchdog.Token);
            Stream transport = socket.GetStream();
            if (afterProof)
            {
                var secured = await SecureTransport.AcceptAsync(socket, key, watchdog.Token);
                transport = secured.Stream;
            }
            using Stream securedTransport = transport;
            using StreamReader reader = new(transport, leaveOpen: true);
            if (afterProof)
            {
                string? proof = await reader.ReadLineAsync(watchdog.Token);
                if (proof is null || !proof.StartsWith("PROOF|", StringComparison.Ordinal))
                    throw new Exception("Controller did not send its pairing proof.");
            }

            try
            {
                using MousePeer unexpected = await connection.WaitAsync(watchdog.Token);
                throw new Exception("Controller accepted a receiver that never completed pairing.");
            }
            catch (TimeoutException) when (!watchdog.IsCancellationRequested) { /* expected */ }

            if (await reader.ReadLineAsync(watchdog.Token) is not null)
                throw new Exception("Timed-out controller left its connection open.");
        }
        finally
        {
            watchdog.Cancel();
            listener.Stop();
        }
    }

    private static async Task TestPairingCancellation(byte[] key)
    {
        using CancellationTokenSource watchdog = new(TimeSpan.FromSeconds(12));
        using CancellationTokenSource cancelled = CancellationTokenSource.CreateLinkedTokenSource(watchdog.Token);
        TcpListener listener = new(IPAddress.Loopback, MousePeer.Port);
        listener.Start();
        try
        {
            Task<MousePeer> connection = MousePeer.ConnectAsync("127.0.0.1", key, cancelled.Token);
            using TcpClient socket = await listener.AcceptTcpClientAsync(watchdog.Token);
            cancelled.Cancel();
            try
            {
                using MousePeer unexpected = await connection.WaitAsync(watchdog.Token);
                throw new Exception("Cancelled pairing unexpectedly succeeded.");
            }
            catch (OperationCanceledException) when (cancelled.IsCancellationRequested && !watchdog.IsCancellationRequested)
            { /* User cancellation must not become a timeout. */ }

            using StreamReader reader = new(socket.GetStream());
            if (await reader.ReadLineAsync(watchdog.Token) is not null)
                throw new Exception("Cancelled controller left its connection open.");
        }
        finally
        {
            cancelled.Cancel();
            listener.Stop();
        }
    }

    private static async Task TestOldVersionRejected(byte[] key, string protocol)
    {
        using CancellationTokenSource watchdog = new(TimeSpan.FromSeconds(12));
        TcpListener listener = new(IPAddress.Loopback, MousePeer.Port);
        listener.Start();
        try
        {
            Task<MousePeer> connecting = MousePeer.ConnectAsync("127.0.0.1", key, watchdog.Token);
            using TcpClient socket = await listener.AcceptTcpClientAsync(watchdog.Token);
            using StreamWriter writer = new(socket.GetStream(), leaveOpen: true) { AutoFlush = true };
            using StreamReader reader = new(socket.GetStream(), leaveOpen: true);
            await writer.WriteLineAsync((protocol + "|" + new string('A', 48)).AsMemory(), watchdog.Token);
            try
            {
                using MousePeer _ = await connecting;
                throw new Exception("Older protocol was accepted for two-way input.");
            }
            catch (NotSupportedException ex) when (ex.Message.Contains("both PCs", StringComparison.Ordinal)) { }
            if (await reader.ReadLineAsync(watchdog.Token) is not null)
                throw new Exception("Incompatible connection left its socket open.");
        }
        finally { listener.Stop(); }
    }

    private static void TestMouseInjection()
    {
        NativeMouse.Inject(NativeMouse.WHEEL, -120, (count, inputs, size) =>
        {
            if (count != 1 || inputs.Length != 1 || inputs[0].Type != 0 ||
                inputs[0].Data.Mouse.Flags != NativeMouse.WHEEL ||
                inputs[0].Data.Mouse.MouseData != unchecked((uint)-120) ||
                size != (IntPtr.Size == 8 ? 40 : 28))
                throw new Exception("Incorrect native mouse event or INPUT structure layout.");
            return 1;
        });

        foreach (int error in new[] { 0, 5 })
        {
            try
            {
                NativeMouse.Inject(NativeMouse.LEFTDOWN, 0, (_, _, _) =>
                {
                    Marshal.SetLastPInvokeError(error);
                    return 0;
                });
                throw new Exception("Rejected input was silently ignored.");
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == error)
            {
                if (!ex.Message.Contains("Windows did not accept", StringComparison.Ordinal))
                    throw new Exception("Input error did not explain that Windows rejected the event.");
            }
        }
    }

    private static void TestButtonReleaseFailures()
    {
        List<(uint flags, int data)> attempts = [];
        List<Win32Exception> failures = [];
        RemoteMouseButtons buttons = new();
        foreach (var (flags, data) in new (uint, int)[] {
            (NativeMouse.LEFTDOWN, 0), (NativeMouse.RIGHTDOWN, 0), (NativeMouse.MIDDLEDOWN, 0),
            (NativeMouse.XDOWN, 1), (NativeMouse.XDOWN, 2) })
            buttons.Apply(flags, data, (_, _) => { });
        buttons.Release((flags, data) => NativeMouse.Inject(flags, data, (_, inputs, _) =>
        {
            attempts.Add((inputs[0].Data.Mouse.Flags, (int)inputs[0].Data.Mouse.MouseData));
            Marshal.SetLastPInvokeError(5);
            return 0;
        }), failures.Add);

        (uint flags, int data)[] expected = [
            (NativeMouse.LEFTUP, 0), (NativeMouse.RIGHTUP, 0), (NativeMouse.MIDDLEUP, 0),
            (NativeMouse.XUP, 1), (NativeMouse.XUP, 2)
        ];
        if (!attempts.SequenceEqual(expected) || failures.Count != expected.Length)
            throw new Exception("A failed release prevented cleanup of other mouse buttons.");
    }
}
