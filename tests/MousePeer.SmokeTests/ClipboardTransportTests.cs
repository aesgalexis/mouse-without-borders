using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MouseWithOutBorders;

internal static class ClipboardTransportTests
{
    internal static async Task RunAsync()
    {
        await TestRoundTripAndOptIn();
        await TestCapabilitiesAndClipboardEncryption();
        await TestPriorityAndCancellation();
        await TestSlowAndFailingSubscribers();
        await TestMalformedFrames();
        Console.WriteLine("PASS: clipboard opt-in, authenticated encryption, bounds, priority and callback isolation over TLS.");
    }

    private static async Task TestRoundTripAndOptIn()
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(12));
        byte[] key = MousePeer.ParseKey(MousePeer.NewKey());
        TcpListener listener = new(IPAddress.Loopback, MousePeer.Port);
        listener.Start();
        try
        {
            Task<MousePeer> connecting = MousePeer.ConnectAsync("127.0.0.1", key, timeout.Token);
            using TcpClient socket = await listener.AcceptTcpClientAsync(timeout.Token);
            using MousePeer receiver = await MousePeer.AcceptAsync(socket, key, timeout.Token);
            using MousePeer sender = await connecting;
            Task senderRead = sender.ReadLoopAsync(_ => throw new Exception("Transport control reached mouse handling."), timeout.Token);
            Task receiverRead = receiver.ReadLoopAsync(_ => throw new Exception("Transport control reached mouse handling."), timeout.Token);
            try
            {
                if (sender.ClipboardAvailable || receiver.ClipboardAvailable) throw new Exception("Clipboard defaults to enabled.");
                await ExpectThrows<InvalidOperationException>(() => sender.SendClipboardAsync(new byte[] { 1 }, timeout.Token));
                sender.ConfigureClipboard(true);
                await ExpectThrows<InvalidOperationException>(() => sender.SendClipboardAsync(new byte[] { 1 }, timeout.Token));
                receiver.ConfigureClipboard(true);
                await Until(() => sender.ClipboardAvailable && receiver.ClipboardAvailable, timeout.Token);
                TaskCompletionSource<byte[]> received = new(TaskCreationOptions.RunContinuationsAsynchronously);
                receiver.ClipboardReceived += data => received.TrySetResult(data.ToArray());
                byte[] original = RandomNumberGenerator.GetBytes(MousePeer.MaxClipboardPlaintext);
                await sender.SendClipboardAsync(original, timeout.Token);
                if (!(await received.Task.WaitAsync(timeout.Token)).SequenceEqual(original)) throw new Exception("Clipboard chunk changed in transit.");
                await ExpectThrows<ArgumentOutOfRangeException>(() => sender.SendClipboardAsync(new byte[MousePeer.MaxClipboardPlaintext + 1], timeout.Token));
                await ExpectThrows<ArgumentOutOfRangeException>(() => sender.SendClipboardAsync(ReadOnlyMemory<byte>.Empty, timeout.Token));
                receiver.ConfigureClipboard(false);
                await Until(() => !sender.ClipboardAvailable, timeout.Token);
                await ExpectThrows<InvalidOperationException>(() => sender.SendClipboardAsync(new byte[] { 2 }, timeout.Token));
            }
            finally
            {
                timeout.Cancel();
                await IgnoreShutdown(senderRead);
                await IgnoreShutdown(receiverRead);
            }
        }
        finally { listener.Stop(); }
    }

    private static async Task TestCapabilitiesAndClipboardEncryption()
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(12));
        using RawPeer raw = await RawPeer.Create(timeout.Token);
        using MousePeer peer = raw.Peer;
        TaskCompletionSource<MousePacket> mouse = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task read = peer.ReadLoopAsync(packet => mouse.TrySetResult(packet), timeout.Token);
        try
        {
            MousePacket initial = await raw.ReadPacket(timeout.Token);
            if (initial.Kind != "capabilities" || initial.Data != 1 || initial.Payload is not null)
                throw new Exception("New peers did not advertise a small disabled capability frame.");
            peer.ConfigureClipboard(true);
            // A secure mouse-only peer need not advertise clipboard support.
            await raw.SendPacket(new MousePacket("move", Dx: 17), timeout.Token);
            if ((await mouse.Task.WaitAsync(timeout.Token)).Dx != 17) throw new Exception("Mouse-only peer input failed.");
            await ExpectThrows<InvalidOperationException>(() => peer.SendClipboardAsync(new byte[] { 1 }, timeout.Token));
            if ((await raw.ReadPacket(timeout.Token)).Kind != "capabilities") throw new Exception("Unexpected bulk data sent to a mouse-only peer.");
            await raw.SendPacket(new MousePacket("capabilities", Data: 3), timeout.Token);
            await Until(() => peer.ClipboardAvailable, timeout.Token);
            byte[] plaintext = Encoding.UTF8.GetBytes("Confidential clipboard text: " + new string('X', 120));
            await peer.SendClipboardAsync(plaintext, timeout.Token);
            MousePacket encrypted = await raw.ReadPacket(timeout.Token);
            if (encrypted.Kind != "clipboard" || encrypted.Payload is null ||
                encrypted.Payload.Contains(Encoding.UTF8.GetString(plaintext), StringComparison.Ordinal))
                throw new Exception("Clipboard wire frame is not encrypted.");
            byte[] packed = Convert.FromBase64String(encrypted.Payload);
            if (packed.AsSpan().IndexOf(plaintext) >= 0) throw new Exception("Clipboard plaintext appeared on the wire.");
            if (!raw.Decrypt(packed, "CLIENT").SequenceEqual(plaintext)) throw new Exception("Independent AES-GCM decryption failed.");
            // Reflecting a valid frame in the opposite direction must fail its AAD.
            await raw.SendPacket(encrypted, timeout.Token);
            await ExpectThrows<IOException>(() => read.WaitAsync(timeout.Token));
            if (peer.Queue(new MousePacket("move"))) throw new Exception("Invalid encrypted input left the peer open.");
        }
        finally { timeout.Cancel(); await IgnoreShutdown(read); }
    }

    private static async Task TestPriorityAndCancellation()
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(15));
        using CancellationTokenSource cancelBulk = new();
        using RawPeer raw = await RawPeer.Create(timeout.Token);
        using MousePeer peer = raw.Peer;
        Task read = peer.ReadLoopAsync(_ => { }, timeout.Token);
        try
        {
            await raw.ReadPacket(timeout.Token);
            peer.ConfigureClipboard(true);
            await raw.SendPacket(new MousePacket("capabilities", Data: 3), timeout.Token);
            await Until(() => peer.ClipboardAvailable, timeout.Token);
            await raw.ReadPacket(timeout.Token);
            raw.Socket.ReceiveBufferSize = 4096;
            const int chunks = 512;
            byte[] data = RandomNumberGenerator.GetBytes(MousePeer.MaxClipboardPlaintext);
            Task[] bulk = Enumerable.Range(0, chunks).Select(_ => peer.SendClipboardAsync(data, cancelBulk.Token)).ToArray();
            await Task.Delay(80, timeout.Token);
            if (bulk.All(task => task.IsCompleted)) throw new Exception("A stalled receiver did not exert clipboard backpressure.");
            if (!peer.Queue(new MousePacket("move", Dx: 123))) throw new Exception("Clipboard traffic filled the input queue.");
            int beforeMouse = 0;
            while (true)
            {
                MousePacket packet = await raw.ReadPacket(timeout.Token);
                if (packet.Kind == "move" && packet.Dx == 123) break;
                if (packet.Kind == "clipboard") beforeMouse++;
                if (beforeMouse >= chunks) throw new Exception("Input waited behind the entire clipboard backlog.");
            }
            cancelBulk.Cancel();
            foreach (Task pending in bulk) await IgnoreShutdown(pending);
            peer.ConfigureClipboard(false);
            if (!peer.Queue(new MousePacket("move", Dx: 321))) throw new Exception("Clipboard cancellation stopped mouse input.");
            while ((await raw.ReadPacket(timeout.Token)) is { } packet)
                if (packet.Kind == "move" && packet.Dx == 321) break;
        }
        finally { cancelBulk.Cancel(); timeout.Cancel(); await IgnoreShutdown(read); }
    }

    private static async Task TestSlowAndFailingSubscribers()
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(12));
        using ManualResetEventSlim release = new();
        using RawPeer raw = await RawPeer.Create(timeout.Token);
        using MousePeer peer = raw.Peer;
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource mouse = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource delivered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        peer.ClipboardReceived += _ => { entered.TrySetResult(); release.Wait(timeout.Token); throw new InvalidOperationException("Subscriber fixture"); };
        peer.ClipboardReceived += _ => delivered.TrySetResult();
        Task read = peer.ReadLoopAsync(packet => { if (packet.Kind == "move") mouse.TrySetResult(); }, timeout.Token);
        try
        {
            await raw.ReadPacket(timeout.Token);
            peer.ConfigureClipboard(true);
            await raw.SendPacket(new MousePacket("capabilities", Data: 3), timeout.Token);
            await Until(() => peer.ClipboardAvailable, timeout.Token);
            await raw.ReadPacket(timeout.Token);
            await raw.SendPacket(raw.Encrypt(new byte[] { 42 }), timeout.Token);
            await entered.Task.WaitAsync(timeout.Token);
            for (int i = 0; i < 24; i++) await raw.SendPacket(raw.Encrypt(new byte[] { (byte)i }), timeout.Token);
            await raw.SendPacket(new MousePacket("move", Dx: 1), timeout.Token);
            await mouse.Task.WaitAsync(TimeSpan.FromSeconds(2), timeout.Token);
            release.Set();
            await delivered.Task.WaitAsync(timeout.Token);
        }
        finally { release.Set(); timeout.Cancel(); await IgnoreShutdown(read); }
    }

    private static async Task TestMalformedFrames()
    {
        foreach (string failure in new[] { "cipher", "empty", "oversized-line", "oversized-mouse", "reflection", "replay", "bad-mac" })
        {
            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
            using RawPeer raw = await RawPeer.Create(timeout.Token);
            using MousePeer peer = raw.Peer;
            Task read = peer.ReadLoopAsync(_ => throw new Exception("Malformed packet reached mouse handling."), timeout.Token);
            try
            {
                await raw.ReadPacket(timeout.Token);
                peer.ConfigureClipboard(true);
                await raw.SendPacket(new MousePacket("capabilities", Data: 3), timeout.Token);
                await Until(() => peer.ClipboardAvailable, timeout.Token);
                if (failure is "reflection" or "replay" or "bad-mac")
                {
                    await raw.SendInvalidPacket(failure, timeout.Token);
                }
                else if (failure == "oversized-line")
                    await raw.Writer.WriteAsync(new string('X', 32769).AsMemory(), timeout.Token); // No newline: allocation must still be bounded.
                else if (failure == "oversized-mouse")
                    await raw.SendPacket(new MousePacket("move", Payload: new string('X', 4096)), timeout.Token);
                else
                    await raw.SendPacket(new MousePacket("clipboard", Payload: failure == "empty" ? "" : Convert.ToBase64String(new byte[32])), timeout.Token);
                await ExpectThrows<IOException>(() => read.WaitAsync(timeout.Token));
                if (peer.Queue(new MousePacket("move"))) throw new Exception("Malformed data left a live peer.");
            }
            finally { timeout.Cancel(); await IgnoreShutdown(read); }
        }
    }

    private static async Task Until(Func<bool> condition, CancellationToken ct)
    {
        while (!condition()) await Task.Delay(10, ct);
    }

    private static async Task ExpectThrows<T>(Func<Task> action) where T : Exception
    {
        try { await action(); }
        catch (T) { return; }
        throw new Exception("Expected " + typeof(T).Name + ".");
    }

    private static async Task IgnoreShutdown(Task task)
    {
        try { await task; }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException) { }
    }

    private sealed class RawPeer : IDisposable
    {
        internal required MousePeer Peer { get; init; }
        internal required TcpClient Socket { get; init; }
        internal required StreamWriter Writer { get; init; }
        internal required StreamReader Reader { get; init; }
        internal required byte[] Key { get; init; }
        internal required Stream Transport { get; init; }
        internal required string Nonce { get; init; }
        private long _sent;
        private long _received;

        internal static async Task<RawPeer> Create(CancellationToken ct)
        {
            TcpListener listener = new(IPAddress.Loopback, MousePeer.Port);
            listener.Start();
            try
            {
                byte[] key = MousePeer.ParseKey(MousePeer.NewKey());
                Task<MousePeer> connecting = MousePeer.ConnectAsync("127.0.0.1", key, ct);
                TcpClient socket = await listener.AcceptTcpClientAsync(ct);
                socket.NoDelay = true;
                var secured = await SecureTransport.AcceptAsync(socket, key, ct);
                StreamReader reader = new(secured.Stream, Encoding.UTF8, leaveOpen: true);
                StreamWriter writer = new(secured.Stream, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
                string nonce = secured.Nonce;
                string proof = await reader.ReadLineAsync(ct) ?? "";
                if (proof != "PROOF|" + Mac(key, "CLIENT|" + nonce)) throw new Exception("Raw fixture pairing proof failed.");
                await writer.WriteLineAsync(("OK|" + Mac(key, "SERVER|" + nonce)).AsMemory(), ct);
                return new RawPeer { Peer = await connecting, Socket = socket, Reader = reader, Writer = writer, Key = key,
                    Transport = secured.Stream, Nonce = nonce };
            }
            finally { listener.Stop(); }
        }

        internal async Task SendPacket(MousePacket packet, CancellationToken ct)
        {
            string encoded = Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(packet));
            long sequence = ++_sent;
            string line = $"{sequence}|{encoded}|{Mac(Key, $"MOUSE3|SERVER|{Nonce}|{sequence}|{encoded}")}";
            await Writer.WriteLineAsync(line.AsMemory(), ct);
        }

        internal async Task<MousePacket> ReadPacket(CancellationToken ct)
        {
            while (true)
            {
                string line = await Reader.ReadLineAsync(ct) ?? throw new IOException("Raw fixture disconnected.");
                string[] parts = line.Split('|');
                if (parts.Length != 3 || long.Parse(parts[0]) != ++_received || parts[2] != Mac(Key, $"MOUSE3|CLIENT|{Nonce}|{_received}|{parts[1]}"))
                    throw new Exception("Raw observer detected invalid authentication or sequence.");
                MousePacket packet = JsonSerializer.Deserialize<MousePacket>(Convert.FromBase64String(parts[1]))!;
                if (packet.Kind != "ping") return packet;
            }
        }

        internal MousePacket Encrypt(byte[] plaintext)
        {
            byte[] packed = new byte[28 + plaintext.Length];
            RandomNumberGenerator.Fill(packed.AsSpan(0, 12));
            using AesGcm aes = new(ClipboardKey(), 16);
            aes.Encrypt(packed.AsSpan(0, 12), plaintext, packed.AsSpan(28), packed.AsSpan(12, 16), Encoding.UTF8.GetBytes("CLIPBOARD1|SERVER|" + Nonce));
            return new MousePacket("clipboard", Payload: Convert.ToBase64String(packed));
        }

        internal async Task SendInvalidPacket(string failure, CancellationToken ct)
        {
            string encoded = Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(new MousePacket("ping")));
            long sequence = failure == "replay" ? _sent : _sent + 1;
            string direction = failure == "reflection" ? "CLIENT" : "SERVER";
            string mac = failure == "bad-mac" ? new string('0', 64) : Mac(Key, $"MOUSE3|{direction}|{Nonce}|{sequence}|{encoded}");
            await Writer.WriteLineAsync($"{sequence}|{encoded}|{mac}".AsMemory(), ct);
        }

        internal byte[] Decrypt(byte[] packed, string direction)
        {
            byte[] plaintext = new byte[packed.Length - 28];
            using AesGcm aes = new(ClipboardKey(), 16);
            aes.Decrypt(packed.AsSpan(0, 12), packed.AsSpan(28), packed.AsSpan(12, 16), plaintext, Encoding.UTF8.GetBytes("CLIPBOARD1|" + direction + "|" + Nonce));
            return plaintext;
        }

        private byte[] ClipboardKey() => HMACSHA256.HashData(Key, Encoding.UTF8.GetBytes("CLIPBOARD1|" + Nonce));
        private static string Mac(byte[] key, string message) => Convert.ToHexString(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(message)));
        public void Dispose() { Peer.Dispose(); Socket.Dispose(); Transport.Dispose(); Reader.Dispose(); Writer.Dispose(); }
    }
}
