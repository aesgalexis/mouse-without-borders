using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;

namespace MouseWithOutBorders;

internal sealed record MousePacket(
    string Kind,
    int Dx = 0,
    int Dy = 0,
    int Data = 0,
    double Ratio = 0,
    uint Flags = 0,
    long Transfer = 0,
    int Origin = 0,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Payload = null);

/// <summary>
/// One authenticated, encrypted, two-way TLS connection. Each message also has a
/// monotonically increasing sequence number and a direction-bound HMAC.
/// </summary>
internal sealed class MousePeer : IDisposable
{
    internal const int Port = 42187;
    internal const int PairingCodeLength = 32;
    internal const int MaxClipboardPlaintext = 16 * 1024;
    private const string PairingAlphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
    // JSON is carried as base64 on the wire, never inserted into HTML. Avoid
    // expanding '+' in encrypted base64 so a maximum-size chunk always fits.
    private static readonly JsonSerializerOptions WireJson = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private readonly TcpClient _client;
    private readonly Stream _transport;
    private readonly BoundedLineReader _reader;
    private readonly StreamWriter _writer;
    private readonly byte[] _key;
    private readonly string _outgoingContext;
    private readonly string _incomingContext;
    private readonly byte[] _clipboardKey;
    private readonly byte[] _outgoingClipboardAad;
    private readonly byte[] _incomingClipboardAad;
    private readonly object _clipboardGate = new();
    private readonly SemaphoreSlim _outgoingReady = new(0, 1);
    private readonly Channel<ClipboardWrite> _clipboardOutgoing = Channel.CreateBounded<ClipboardWrite>(
        new BoundedChannelOptions(4) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
    private readonly Channel<ClipboardRead> _clipboardIncoming = Channel.CreateBounded<ClipboardRead>(
        new BoundedChannelOptions(8) { SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.Wait });
    private CancellationTokenSource _clipboardGeneration = new();
    private int _clipboardEnabled;
    private int _remoteClipboardCapabilities;
    private int _availabilityNotificationPending;
    private int _availabilityVersion;
    private readonly Channel<MousePacket> _outgoing =
        Channel.CreateBounded<MousePacket>(new BoundedChannelOptions(4096)
        {
            SingleReader = true,
            FullMode = BoundedChannelFullMode.Wait
        });
    private readonly CancellationTokenSource _stopping = new();
    private long _sent;
    private long _received;
    private int _disposed;

    private MousePeer(TcpClient client, Stream transport, BoundedLineReader reader, StreamWriter writer, byte[] key, string nonce, bool isClient)
    {
        _client = client;
        _transport = transport;
        _reader = reader;
        _writer = writer;
        _key = key;
        _outgoingContext = "MOUSE3|" + (isClient ? "CLIENT|" : "SERVER|") + nonce;
        _incomingContext = "MOUSE3|" + (isClient ? "SERVER|" : "CLIENT|") + nonce;
        _clipboardKey = HMACSHA256.HashData(key, Encoding.UTF8.GetBytes("CLIPBOARD1|" + nonce));
        _outgoingClipboardAad = Encoding.UTF8.GetBytes("CLIPBOARD1|" + (isClient ? "CLIENT|" : "SERVER|") + nonce);
        _incomingClipboardAad = Encoding.UTF8.GetBytes("CLIPBOARD1|" + (isClient ? "SERVER|" : "CLIENT|") + nonce);
        Queue(new MousePacket("capabilities", Data: 1));
        _ = WriteLoopAsync();
        _ = HeartbeatLoopAsync();
        _ = ClipboardDeliveryLoopAsync();
    }

    private static (BoundedLineReader reader, StreamWriter writer) OpenStreams(Stream stream)
    {
        return (
            new BoundedLineReader(new StreamReader(stream, new UTF8Encoding(false), false, 4096, true)),
            new StreamWriter(stream, new UTF8Encoding(false), 4096, true) { AutoFlush = true });
    }

    internal static string NormalizeKey(string code) =>
        new(code.Where(c => !char.IsWhiteSpace(c) && c != '-').Select(char.ToUpperInvariant).ToArray());

    internal static byte[] ParseKey(string code)
    {
        string normalized = NormalizeKey(code);
        if (normalized.StartsWith("MWB3", StringComparison.Ordinal) && normalized.Length == PairingCodeLength + 4 &&
            normalized[4..].All(PairingAlphabet.Contains))
            return Encoding.ASCII.GetBytes(normalized[4..]);

        throw new ArgumentException("Enter the receiving PC's new MWB3 pairing code. Update both PCs and pair again; old codes are no longer accepted.");
    }

    internal static string NewKey()
    {
        // 32 independent base-32 characters = 160 random bits. The prefix makes
        // legacy secrets distinguishable even when they use the same alphabet.
        string code = string.Create(PairingCodeLength, 0, (characters, _) =>
        {
            for (int i = 0; i < characters.Length; i++)
                characters[i] = PairingAlphabet[RandomNumberGenerator.GetInt32(PairingAlphabet.Length)];
        });
        return "MWB3-" + string.Join("-", Enumerable.Range(0, 4).Select(i => code.Substring(i * 8, 8)));
    }

    internal static string FormatKey(string code)
    {
        string secret = Encoding.ASCII.GetString(ParseKey(code));
        return "MWB3-" + string.Join("-", Enumerable.Range(0, 4).Select(i => secret.Substring(i * 8, 8)));
    }

    private static string Mac(byte[] key, string message) =>
        Convert.ToHexString(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(message)));

    private static bool SameMac(byte[] key, string message, string actual)
    {
        try
        {
            byte[] received = Convert.FromHexString(actual);
            byte[] expected = HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(message));
            return CryptographicOperations.FixedTimeEquals(received, expected);
        }
        catch (FormatException) { return false; }
    }

    internal static async Task<MousePeer> ConnectAsync(string host, byte[] key, CancellationToken ct)
    {
        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        CancellationToken connecting = deadline.Token;
        TcpClient client = new();
        Stream? transport = null;
        try
        {
            await client.ConnectAsync(host, Port, connecting);
            var secured = await SecureTransport.ConnectAsync(client, key, connecting);
            transport = secured.Stream;
            string nonce = secured.Nonce;
            var (reader, writer) = OpenStreams(transport);

            await writer.WriteLineAsync(("PROOF|" + Mac(key, "CLIENT|" + nonce)).AsMemory(), connecting);
            string response = await reader.ReadLineAsync(4096, connecting) ?? throw new IOException("Receiver disconnected.");
            if (!response.StartsWith("OK|", StringComparison.Ordinal) ||
                !SameMac(key, "SERVER|" + nonce, response["OK|".Length..]))
                throw new UnauthorizedAccessException("Pairing key rejected by receiver.");

            return new MousePeer(client, transport, reader, writer, key, nonce, isClient: true);
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            client.Dispose();
            transport?.Dispose();
            throw new TimeoutException("Receiver connection or pairing timed out after 5 seconds. Check the receiving PC and try again.", ex);
        }
        catch
        {
            client.Dispose();
            transport?.Dispose();
            throw;
        }
    }

    internal static async Task<MousePeer> AcceptAsync(TcpClient client, byte[] key, CancellationToken ct)
    {
        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        Stream? transport = null;
        try
        {
            var secured = await SecureTransport.AcceptAsync(client, key, deadline.Token);
            transport = secured.Stream;
            string nonce = secured.Nonce;
            var (reader, writer) = OpenStreams(transport);
            string proof = await reader.ReadLineAsync(4096, deadline.Token) ?? "";
            if (!proof.StartsWith("PROOF|", StringComparison.Ordinal) ||
                !SameMac(key, "CLIENT|" + nonce, proof["PROOF|".Length..]))
            {
                await writer.WriteLineAsync("DENIED".AsMemory(), deadline.Token);
                throw new UnauthorizedAccessException("Client pairing key rejected.");
            }
            await writer.WriteLineAsync(("OK|" + Mac(key, "SERVER|" + nonce)).AsMemory(), deadline.Token);
            return new MousePeer(client, transport, reader, writer, key, nonce, isClient: false);
        }
        catch
        {
            client.Dispose();
            transport?.Dispose();
            throw;
        }
    }

    internal event Action<ReadOnlyMemory<byte>>? ClipboardReceived;
    internal event Action? ClipboardAvailabilityChanged;

    internal bool ClipboardAvailable => Volatile.Read(ref _disposed) == 0 &&
        Volatile.Read(ref _clipboardEnabled) != 0 && (Volatile.Read(ref _remoteClipboardCapabilities) & 3) == 3;

    internal void ConfigureClipboard(bool enabled)
    {
        lock (_clipboardGate)
        {
            if (Volatile.Read(ref _disposed) != 0 || (_clipboardEnabled != 0) == enabled) return;
            _clipboardEnabled = enabled ? 1 : 0;
            _clipboardGeneration.Cancel();
            _clipboardGeneration.Dispose();
            _clipboardGeneration = new CancellationTokenSource();
            if (!Queue(new MousePacket("capabilities", Data: enabled ? 3 : 1))) Dispose();
        }
        NotifyClipboardAvailability();
    }

    internal async Task SendClipboardAsync(ReadOnlyMemory<byte> plaintext, CancellationToken ct = default)
    {
        if (plaintext.Length is < 1 or > MaxClipboardPlaintext)
            throw new ArgumentOutOfRangeException(nameof(plaintext), "Clipboard chunks must contain 1 to 16384 bytes.");
        CancellationToken generation;
        lock (_clipboardGate)
        {
            if (!ClipboardAvailable) throw new InvalidOperationException("Clipboard sharing is not enabled on both PCs.");
            generation = _clipboardGeneration.Token;
        }
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(ct, generation, _stopping.Token);
        ClipboardWrite pending = new(plaintext.ToArray(), linked.Token);
        await _clipboardOutgoing.Writer.WriteAsync(pending, linked.Token).ConfigureAwait(false);
        WakeWriter();
        await pending.Completion.Task.WaitAsync(linked.Token).ConfigureAwait(false);
    }

    internal bool Queue(MousePacket packet)
    {
        if (packet.Kind == "clipboard" || Volatile.Read(ref _disposed) != 0 || !_outgoing.Writer.TryWrite(packet)) return false;
        WakeWriter();
        return true;
    }

    private void WakeWriter()
    {
        try { _outgoingReady.Release(); }
        catch (SemaphoreFullException) { }
    }

    private async Task WritePacketAsync(MousePacket packet)
    {
        long seq = ++_sent;
        string payload = Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(packet, WireJson));
        string signed = $"{_outgoingContext}|{seq}|{payload}";
        string line = $"{seq}|{payload}|{Mac(_key, signed)}";
        if (line.Length > (packet.Kind == "clipboard" ? 32768 : 4096))
            throw new IOException("Oversized outgoing message.");
        await _writer.WriteLineAsync(line.AsMemory(), _stopping.Token).ConfigureAwait(false);
    }

    private MousePacket EncryptClipboard(byte[] plaintext)
    {
        byte[] encrypted = new byte[12 + 16 + plaintext.Length];
        RandomNumberGenerator.Fill(encrypted.AsSpan(0, 12));
        using AesGcm cipher = new(_clipboardKey, 16);
        cipher.Encrypt(encrypted.AsSpan(0, 12), plaintext, encrypted.AsSpan(28), encrypted.AsSpan(12, 16), _outgoingClipboardAad);
        return new MousePacket("clipboard", Payload: Convert.ToBase64String(encrypted));
    }

    private async Task WriteLoopAsync()
    {
        try
        {
            while (!_stopping.IsCancellationRequested)
            {
                // Drain input and heartbeats before writing one bounded clipboard chunk.
                while (_outgoing.Reader.TryRead(out MousePacket? packet))
                    await WritePacketAsync(packet).ConfigureAwait(false);
                if (_clipboardOutgoing.Reader.TryRead(out ClipboardWrite? clipboard))
                {
                    Task writing;
                    lock (_clipboardGate)
                    {
                        if (clipboard.Cancellation.IsCancellationRequested || !ClipboardAvailable)
                        {
                            clipboard.Completion.TrySetCanceled();
                            continue;
                        }
                        writing = WritePacketAsync(EncryptClipboard(clipboard.Plaintext));
                    }
                    await writing.ConfigureAwait(false);
                    clipboard.Completion.TrySetResult();
                    continue;
                }
                await _outgoingReady.WaitAsync(_stopping.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
        catch (IOException) { Dispose(); }
        catch (ObjectDisposedException) { }
        catch (SocketException) { Dispose(); }
    }

    private async Task HeartbeatLoopAsync()
    {
        try
        {
            while (!_stopping.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(1), _stopping.Token);
                Queue(new MousePacket("ping"));
            }
        }
        catch (OperationCanceledException) { }
    }

    internal async Task ReadLoopAsync(Action<MousePacket> onPacket, CancellationToken ct)
    {
        try { await ReadPacketsAsync(onPacket, ct).ConfigureAwait(false); }
        catch
        {
            Dispose();
            throw;
        }
    }

    private async Task ReadPacketsAsync(Action<MousePacket> onPacket, CancellationToken ct)
    {
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _stopping.Token);
        while (!linked.IsCancellationRequested)
        {
            // Every connected peer emits a heartbeat each second. A dead or
            // unplugged PC must not leave the origin's cursor locked forever.
            using CancellationTokenSource deadline =
                CancellationTokenSource.CreateLinkedTokenSource(linked.Token);
            deadline.CancelAfter(TimeSpan.FromSeconds(5));

            string? line;
            try { line = await _reader.ReadLineAsync(32768, deadline.Token); }
            catch (OperationCanceledException) when (!linked.IsCancellationRequested)
            {
                throw new IOException("Other PC stopped responding (heartbeat timeout).");
            }
            if (line is null) throw new IOException("Other PC disconnected.");

            string[] parts = line.Split('|', 3);
            if (parts.Length != 3 || !long.TryParse(parts[0], out long seq) ||
                seq != _received + 1 || !SameMac(_key, $"{_incomingContext}|{seq}|{parts[1]}", parts[2]))
                throw new IOException("Invalid or replayed mouse message.");

            _received = seq;
            MousePacket? packet;
            try { packet = JsonSerializer.Deserialize<MousePacket>(Convert.FromBase64String(parts[1])); }
            catch (Exception ex) when (ex is JsonException or FormatException)
            { throw new IOException("Invalid mouse packet.", ex); }
            if (packet is null) throw new IOException("Invalid mouse packet.");
            if (packet.Kind != "clipboard" && line.Length > 4096) throw new IOException("Oversized mouse message.");
            if (packet.Kind == "capabilities")
            {
                bool available = ClipboardAvailable;
                Interlocked.Exchange(ref _remoteClipboardCapabilities, packet.Data & 3);
                if (!ClipboardAvailable)
                {
                    lock (_clipboardGate)
                    {
                        _clipboardGeneration.Cancel();
                        _clipboardGeneration.Dispose();
                        _clipboardGeneration = new CancellationTokenSource();
                    }
                }
                if (available != ClipboardAvailable) NotifyClipboardAvailability();
            }
            else if (packet.Kind == "clipboard") ReceiveClipboard(packet);
            else if (packet.Kind != "ping") onPacket(packet);
        }
    }

    private void ReceiveClipboard(MousePacket packet)
    {
        try
        {
            byte[] encrypted = Convert.FromBase64String(packet.Payload ?? "");
            if (encrypted.Length is < 29 or > MaxClipboardPlaintext + 28)
                throw new IOException("Invalid clipboard chunk size.");
            byte[] plaintext = new byte[encrypted.Length - 28];
            using AesGcm cipher = new(_clipboardKey, 16);
            cipher.Decrypt(encrypted.AsSpan(0, 12), encrypted.AsSpan(28), encrypted.AsSpan(12, 16), plaintext, _incomingClipboardAad);
            lock (_clipboardGate)
            {
                if (!ClipboardAvailable) return;
                if (!_clipboardIncoming.Writer.TryWrite(new ClipboardRead(plaintext, _clipboardGeneration.Token)))
                    System.Diagnostics.Trace.WriteLine("Clipboard receive queue is full; clipboard chunk dropped.");
            }
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        { throw new IOException("Invalid encrypted clipboard chunk.", ex); }
    }

    private async Task ClipboardDeliveryLoopAsync()
    {
        try
        {
            await foreach (ClipboardRead received in _clipboardIncoming.Reader.ReadAllAsync(_stopping.Token).ConfigureAwait(false))
            {
                if (!ClipboardAvailable || received.Cancellation.IsCancellationRequested) continue;
                if (ClipboardReceived is not { } handlers) continue;
                foreach (Action<ReadOnlyMemory<byte>> handler in handlers.GetInvocationList())
                {
                    if (!ClipboardAvailable || received.Cancellation.IsCancellationRequested) break;
                    try { handler(received.Plaintext); }
                    catch (Exception ex) { System.Diagnostics.Trace.WriteLine("Clipboard delivery failed: " + ex.Message); }
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    private void NotifyClipboardAvailability()
    {
        Interlocked.Increment(ref _availabilityVersion);
        ScheduleAvailabilityNotification();
    }

    private void ScheduleAvailabilityNotification()
    {
        if (Interlocked.Exchange(ref _availabilityNotificationPending, 1) != 0) return;
        ThreadPool.QueueUserWorkItem(_ =>
        {
            int version = Volatile.Read(ref _availabilityVersion);
            try
            {
                if (ClipboardAvailabilityChanged is not { } handlers) return;
                foreach (Action handler in handlers.GetInvocationList())
                {
                    try { handler(); }
                    catch (Exception ex) { System.Diagnostics.Trace.WriteLine("Clipboard availability callback failed: " + ex.Message); }
                }
            }
            finally
            {
                Volatile.Write(ref _availabilityNotificationPending, 0);
                if (version != Volatile.Read(ref _availabilityVersion)) ScheduleAvailabilityNotification();
            }
        });
    }

    private sealed record ClipboardWrite(byte[] Plaintext, CancellationToken Cancellation)
    {
        internal TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed record ClipboardRead(byte[] Plaintext, CancellationToken Cancellation);

    private sealed class BoundedLineReader(StreamReader reader)
    {
        private readonly char[] _buffer = new char[4096];
        private int _position;
        private int _length;

        internal async Task<string?> ReadLineAsync(int maximum, CancellationToken ct)
        {
            StringBuilder line = new();
            while (true)
            {
                if (_position == _length)
                {
                    _length = await reader.ReadAsync(_buffer.AsMemory(), ct).ConfigureAwait(false);
                    _position = 0;
                    if (_length == 0) return line.Length == 0 ? null : line.ToString().TrimEnd('\r');
                }
                int newline = Array.IndexOf(_buffer, '\n', _position, _length - _position);
                int end = newline < 0 ? _length : newline;
                int count = end - _position;
                if (line.Length + count > maximum) throw new IOException("Oversized peer message.");
                line.Append(_buffer, _position, count);
                _position = end;
                if (newline >= 0)
                {
                    _position++;
                    return line.ToString().TrimEnd('\r');
                }
            }
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _stopping.Cancel();
        _outgoing.Writer.TryComplete();
        _clipboardOutgoing.Writer.TryComplete();
        _clipboardIncoming.Writer.TryComplete();
        lock (_clipboardGate)
        {
            _clipboardEnabled = 0;
            _clipboardGeneration.Cancel();
        }
        _client.Dispose();
        _transport.Dispose();
        NotifyClipboardAvailability();
    }
}
