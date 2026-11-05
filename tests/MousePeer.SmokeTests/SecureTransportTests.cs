using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;

namespace MouseWithOutBorders;

internal static class SecureTransportTests
{
    internal static async Task RunAsync()
    {
        await TestCertificatePin(substitute: true);
        await TestCertificatePin(substitute: false);
        await TestForgedGreeting();
        await TestClientProofRequired();
        await TestStalledTlsClient();
        await TestWireProtection();
        Console.WriteLine("PASS: TLS 1.2 compatibility, certificate substitution, forged greeting, invalid client proof and stalled TLS client; TCP confidentiality and tamper rejection.");
    }

    private static async Task TestCertificatePin(bool substitute)
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(12));
        using TcpListener listener = new(IPAddress.Loopback, MousePeer.Port);
        listener.Start();
        byte[] key = MousePeer.ParseKey(MousePeer.NewKey());
        Task<MousePeer> connecting = MousePeer.ConnectAsync("127.0.0.1", key, timeout.Token);
        using TcpClient socket = await listener.AcceptTcpClientAsync(timeout.Token);
        using ECDsa identity = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using X509Certificate2 generated = new CertificateRequest("CN=Impostor", identity, HashAlgorithmName.SHA256)
            .CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(1));
        using X509Certificate2 certificate = X509CertificateLoader.LoadPkcs12(generated.Export(X509ContentType.Pkcs12), null);
        // An authenticated/replayed pin cannot authorize a different certificate.
        string nonce = new('A', 48);
        string pin = substitute ? new string('B', 64) : certificate.GetCertHashString(HashAlgorithmName.SHA256);
        string signed = $"MOUSE3|{nonce}|{pin}";
        await socket.GetStream().WriteAsync(Encoding.ASCII.GetBytes(signed + "|" + Mac(key, signed) + "\n"), timeout.Token);
        using SslStream tls = new(socket.GetStream(), leaveInnerStreamOpen: true);
        Task handshake = tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
        {
            ServerCertificate = certificate, EnabledSslProtocols = SslProtocols.Tls12
        }, timeout.Token);
        if (substitute)
        {
            await Expect<UnauthorizedAccessException>(connecting);
            try { await handshake; }
            catch (Exception ex) when (ex is AuthenticationException or IOException) { }
        }
        else
        {
            await handshake;
            if (tls.SslProtocol != SslProtocols.Tls12 || !tls.IsEncrypted || !tls.IsSigned)
                throw new Exception("TLS 1.2 peer did not establish an encrypted, authenticated channel.");
            using StreamReader reader = new(tls, leaveOpen: true);
            using StreamWriter writer = new(tls, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
            string? proof = await reader.ReadLineAsync(timeout.Token);
            if (proof != "PROOF|" + Mac(key, "CLIENT|" + nonce)) throw new Exception("TLS 1.2 client proof failed.");
            await writer.WriteLineAsync(("OK|" + Mac(key, "SERVER|" + nonce)).AsMemory(), timeout.Token);
            using MousePeer peer = await connecting;
        }
    }

    private static async Task TestForgedGreeting()
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(12));
        using TcpListener listener = new(IPAddress.Loopback, MousePeer.Port);
        listener.Start();
        byte[] key = MousePeer.ParseKey(MousePeer.NewKey());
        Task<MousePeer> connecting = MousePeer.ConnectAsync("127.0.0.1", key, timeout.Token);
        using TcpClient socket = await listener.AcceptTcpClientAsync(timeout.Token);
        string signed = $"MOUSE3|{new string('A', 48)}|{new string('B', 64)}";
        await socket.GetStream().WriteAsync(Encoding.ASCII.GetBytes(signed + "|" + new string('0', 64) + "\n"), timeout.Token);
        await Expect<UnauthorizedAccessException>(connecting);
        if (await socket.GetStream().ReadAsync(new byte[1], timeout.Token) != 0)
            throw new Exception("Client sent data to an unauthenticated greeting.");
    }

    private static async Task TestClientProofRequired()
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(12));
        using TcpListener listener = new(IPAddress.Loopback, MousePeer.Port);
        listener.Start();
        byte[] key = MousePeer.ParseKey(MousePeer.NewKey());
        using TcpClient socket = new();
        await socket.ConnectAsync(IPAddress.Loopback, MousePeer.Port, timeout.Token);
        using TcpClient accepted = await listener.AcceptTcpClientAsync(timeout.Token);
        Task<MousePeer> accepting = MousePeer.AcceptAsync(accepted, key, timeout.Token);
        var secured = await SecureTransport.ConnectAsync(socket, key, timeout.Token);
        using SslStream tls = secured.Stream;
        using StreamWriter writer = new(tls, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
        await writer.WriteLineAsync(("PROOF|" + new string('0', 64)).AsMemory(), timeout.Token);
        await Expect<UnauthorizedAccessException>(accepting);
    }

    private static async Task TestStalledTlsClient()
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(12));
        using TcpListener listener = new(IPAddress.Loopback, MousePeer.Port);
        listener.Start();
        using TcpClient client = new();
        await client.ConnectAsync(IPAddress.Loopback, MousePeer.Port, timeout.Token);
        using TcpClient accepted = await listener.AcceptTcpClientAsync(timeout.Token);
        Task<MousePeer> accepting = MousePeer.AcceptAsync(accepted, MousePeer.ParseKey(MousePeer.NewKey()), timeout.Token);
        await Expect<OperationCanceledException>(accepting);
        if (timeout.IsCancellationRequested) throw new Exception("Receiver did not enforce its own TLS deadline.");
        using StreamReader reader = new(client.GetStream());
        if (!(await reader.ReadLineAsync(timeout.Token))!.StartsWith("MOUSE3|", StringComparison.Ordinal) ||
            await reader.ReadLineAsync(timeout.Token) is not null)
            throw new Exception("Stalled TLS pairing left its connection open.");
    }

    private static async Task TestWireProtection()
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(15));
        using TcpListener front = new(IPAddress.Loopback, MousePeer.Port);
        using TcpListener back = new(IPAddress.Loopback, 0);
        front.Start(); back.Start();
        byte[] key = MousePeer.ParseKey(MousePeer.NewKey());
        Task<MousePeer> connecting = MousePeer.ConnectAsync("127.0.0.1", key, timeout.Token);
        using TcpClient proxyClient = await front.AcceptTcpClientAsync(timeout.Token);
        using TcpClient proxyServer = new();
        await proxyServer.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)back.LocalEndpoint).Port, timeout.Token);
        using TcpClient accepted = await back.AcceptTcpClientAsync(timeout.Token);
        using MemoryStream capture = new();
        int corrupt = 0;
        int corrupted = 0;
        async Task Pump(Stream source, Stream destination, bool towardServer)
        {
            byte[] buffer = new byte[8192];
            while (true)
            {
                int count = await source.ReadAsync(buffer, timeout.Token);
                if (count == 0) break;
                lock (capture) capture.Write(buffer, 0, count);
                if (towardServer && Interlocked.Exchange(ref corrupt, 0) == 1)
                {
                    buffer[count - 1] ^= 0x40;
                    Interlocked.Exchange(ref corrupted, 1);
                }
                await destination.WriteAsync(buffer.AsMemory(0, count), timeout.Token);
            }
        }
        Task forward = Pump(proxyClient.GetStream(), proxyServer.GetStream(), true);
        Task reverse = Pump(proxyServer.GetStream(), proxyClient.GetStream(), false);
        using MousePeer receiver = await MousePeer.AcceptAsync(accepted, key, timeout.Token);
        using MousePeer sender = await connecting;
        TaskCompletionSource<MousePacket> moved = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<byte[]> copied = new(TaskCreationOptions.RunContinuationsAsynchronously);
        bool injected = false;
        receiver.ClipboardReceived += value => copied.TrySetResult(value.ToArray());
        Task receive = receiver.ReadLoopAsync(packet =>
        {
            if (packet.Dx == 999) injected = true;
            moved.TrySetResult(packet);
        }, timeout.Token);
        Task readSender = sender.ReadLoopAsync(_ => { }, timeout.Token);
        try
        {
            string marker = "wire-confidential-" + Guid.NewGuid().ToString("N");
            MousePacket original = new("move", Dx: 1267, Dy: -574, Payload: marker);
            sender.Queue(original);
            if (await moved.Task.WaitAsync(timeout.Token) != original) throw new Exception("TLS changed mouse input.");
            receiver.ConfigureClipboard(true); sender.ConfigureClipboard(true);
            while (!sender.ClipboardAvailable) await Task.Delay(10, timeout.Token);
            byte[] clipboard = Encoding.UTF8.GetBytes(marker + "-clipboard");
            await sender.SendClipboardAsync(clipboard, timeout.Token);
            if (!(await copied.Task.WaitAsync(timeout.Token)).SequenceEqual(clipboard)) throw new Exception("TLS changed clipboard data.");
            byte[] wire;
            lock (capture) wire = capture.ToArray();
            foreach (string plaintext in new[] { marker, "PROOF|", "OK|", Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(original)) })
                if (wire.AsSpan().IndexOf(Encoding.UTF8.GetBytes(plaintext)) >= 0)
                    throw new Exception("Sensitive mouse, clipboard or pairing proof was visible on TCP.");

            Interlocked.Exchange(ref corrupt, 1);
            sender.Queue(new MousePacket("move", Dx: 999));
            await Expect<IOException>(receive.WaitAsync(timeout.Token));
            if (Volatile.Read(ref corrupted) != 1 || injected || receiver.Queue(new MousePacket("move")))
                throw new Exception("Tampered TLS input was delivered or left a live receiver.");
        }
        finally
        {
            timeout.Cancel(); sender.Dispose(); receiver.Dispose();
            await IgnoreShutdown(receive); await IgnoreShutdown(readSender);
            await IgnoreShutdown(forward); await IgnoreShutdown(reverse);
        }
    }

    private static string Mac(byte[] key, string message) => Convert.ToHexString(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(message)));

    private static async Task Expect<T>(Task task) where T : Exception
    {
        try { await task; }
        catch (T) { return; }
        throw new Exception("Expected " + typeof(T).Name);
    }

    private static async Task IgnoreShutdown(Task task)
    {
        try { await task; }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException) { }
    }
}
