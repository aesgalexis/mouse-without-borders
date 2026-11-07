using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;

namespace MouseWithOutBorders;

internal static class Program
{
    private static async Task Main()
    {
        string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "clipboard-service-tests"));
        // Reuse one sandbox, including after a failed run. Never touch real clipboard data.
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        Directory.CreateDirectory(root);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(90));
        ConcurrentQueue<string> status = new();
        FakeClipboard first = new(), second = new();
        ClipboardSharingService serviceA = new(message => status.Enqueue("A: " + message), first, Path.Combine(root, "cache-a"));
        ClipboardSharingService serviceB = new(message => status.Enqueue("B: " + message), second, Path.Combine(root, "cache-b"));
        Connection? connection = null;
        string[] retained = [];
        bool passed = false;
        try
        {
            serviceA.SetEnabled(true);
            serviceB.SetEnabled(true);
            connection = await Connection.Create(serviceA, serviceB, timeout.Token);
            await Baseline(connection, timeout.Token);

            first.Copy(new(Text: "Clipboard service test — text ✓"));
            await Until(() => second.Current?.Text == "Clipboard service test — text ✓", timeout.Token);
            await Task.Delay(500, timeout.Token);
            if (first.WriteCount != 0 || second.WriteCount != 1) throw new Exception("Remote text echoed back or was applied more than once.");
            Console.WriteLine("PASS: real peers share text on separate STA workers without echoing.");

            string sources = Path.Combine(root, "sources");
            Directory.CreateDirectory(sources);
            string textFile = Path.Combine(sources, "notes.txt"), emptyFile = Path.Combine(sources, "empty.bin");
            await File.WriteAllTextAsync(textFile, "Files shared through the service.", timeout.Token);
            await File.WriteAllBytesAsync(emptyFile, [], timeout.Token);
            first.Copy(new(Files: [textFile, emptyFile]));
            await Until(() => second.Current?.Files is { Length: 2 }, timeout.Token);
            retained = second.Current!.Files!;
            await AssertFiles([textFile, emptyFile], retained, timeout.Token);
            string[] unicodeFiles = Enumerable.Range(0, ClipboardLimits.MaxFiles)
                .Select(index => Path.Combine(sources, index.ToString("D2") + new string('\u754c', 126))).ToArray();
            foreach (string path in unicodeFiles) await File.WriteAllBytesAsync(path, [], timeout.Token);
            first.Copy(new(Files: unicodeFiles));
            await Until(() => second.Current?.Files is { Length: ClipboardLimits.MaxFiles }, timeout.Token);
            await AssertFiles(unicodeFiles, second.Current!.Files!, timeout.Token);
            retained = retained.Concat(second.Current!.Files!).ToArray();
            int fileWrites = second.WriteCount;
            await Task.Delay(450, timeout.Token);
            if (second.WriteCount != fileWrites || first.WriteCount != 0) throw new Exception("Received files echoed back.");
            Console.WriteLine("PASS: files, empty files and 20 long Unicode filenames are shared without echo.");

            serviceB.SetEnabled(false);
            await Until(() => !connection.First.ClipboardAvailable && !connection.Second.ClipboardAvailable, timeout.Token);
            first.Copy(new(Text: "Copied while disabled"));
            await Task.Delay(400, timeout.Token);
            if (second.WriteCount != fileWrites) throw new Exception("Disabled clipboard sharing applied new content.");
            serviceB.SetEnabled(true);
            await Baseline(connection, timeout.Token);
            await Task.Delay(300, timeout.Token);
            if (second.WriteCount != fileWrites) throw new Exception("Re-enabling sent a stale clipboard snapshot.");
            first.Copy(new(Text: "A fresh copy after enabling"));
            await Until(() => second.Current?.Text == "A fresh copy after enabling", timeout.Token);
            Console.WriteLine("PASS: disable prevents updates and re-enable ignores stale copies.");

            await TestNegativeAcknowledgement(connection, second, timeout.Token);
            Console.WriteLine("PASS: receiver rejects malformed transfer chunks with a negative acknowledgement and no clipboard update.");

            string largeFile = Path.Combine(sources, "large.bin");
            await CreateFile(largeFile, ClipboardLimits.MaxFileBytes, timeout.Token);
            using (ManualResetEventSlim release = new())
            {
                TaskCompletionSource chunk = new(TaskCreationOptions.RunContinuationsAsynchronously);
                int blocked = 0;
                void BlockFirstChunk(ReadOnlyMemory<byte> bytes)
                {
                    if (!IsChunk(bytes) || Interlocked.Exchange(ref blocked, 1) != 0) return;
                    chunk.TrySetResult();
                    release.Wait(timeout.Token);
                }
                connection.Second.ClipboardReceived += BlockFirstChunk;
                try
                {
                    int beforeReplacement = second.WriteCount;
                    first.Copy(new(Files: [largeFile]));
                    await chunk.Task.WaitAsync(timeout.Token);
                    first.Copy(new(Text: "This new local copy replaces the unfinished transfer"));
                    await Task.Delay(350, timeout.Token); // Give the source poll time to cancel the blocked copy.
                    release.Set();
                    await Until(() => second.Current?.Text == "This new local copy replaces the unfinished transfer", timeout.Token);
                    if (second.Writes.Skip(beforeReplacement).Any(content => content.Files is not null))
                        throw new Exception("An old transfer completed after a newer local copy.");
                    await Until(() => !Directory.EnumerateDirectories(Path.Combine(root, "cache-b"), "partial-*", SearchOption.AllDirectories).Any(), timeout.Token);
                }
                finally { release.Set(); connection.Second.ClipboardReceived -= BlockFirstChunk; }
            }
            Console.WriteLine("PASS: a new local copy cancels unfinished file delivery and removes partial files.");

            TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
            void ObserveChunk(ReadOnlyMemory<byte> bytes) { if (IsChunk(bytes)) started.TrySetResult(); }
            connection.Second.ClipboardReceived += ObserveChunk;
            try
            {
                first.Copy(new(Files: [largeFile]));
                await started.Task.WaitAsync(timeout.Token);
                Task<MousePacket> mouse = connection.WaitForMouse(918);
                if (!connection.First.Queue(new MousePacket("move", Dx: 918))) throw new Exception("Bulk clipboard data prevented queuing input.");
                await mouse.WaitAsync(TimeSpan.FromSeconds(2), timeout.Token);
                await Until(() => second.Current?.Files is { Length: 1 } paths && Path.GetFileName(paths[0]) == "large.bin", timeout.Token);
                await AssertFiles([largeFile], second.Current!.Files!, timeout.Token);
            }
            finally { connection.Second.ClipboardReceived -= ObserveChunk; }
            Console.WriteLine("PASS: a complete 25 MB transfer preserves input delivery and checksum integrity.");

            string[] latest = second.Current!.Files!;
            int beforeReconnect = second.WriteCount;
            await connection.DisposeAsync();
            connection = null;
            if (retained.Concat(latest).Any(path => !File.Exists(path))) throw new Exception("Disconnect removed files still referenced by the clipboard.");
            connection = await Connection.Create(serviceA, serviceB, timeout.Token);
            await Baseline(connection, timeout.Token);
            await Task.Delay(300, timeout.Token);
            if (second.WriteCount != beforeReconnect || first.WriteCount != 0) throw new Exception("Reconnect replayed old clipboard content.");
            second.Copy(new(Text: "Reverse direction after reconnect"));
            await Until(() => first.Current?.Text == "Reverse direction after reconnect", timeout.Token);
            await Task.Delay(350, timeout.Token);
            if (first.WriteCount != 1 || second.WriteCount != beforeReconnect) throw new Exception("Reverse sharing echoed after reconnect.");
            if (first.WorkerThreads.Length != 1 || second.WorkerThreads.Length != 1 || first.WorkerThreads[0] == second.WorkerThreads[0])
                throw new Exception("Clipboard adapters did not run on their own STA threads.");
            retained = retained.Concat(latest).ToArray();
            Console.WriteLine("PASS: reconnect preserves completed file paths, skips stale content and shares in reverse.");
            passed = true;
        }
        catch
        {
            foreach (string message in status.Where(message => !string.IsNullOrWhiteSpace(message))) Console.Error.WriteLine(message);
            Console.Error.WriteLine("Clipboard service diagnostics retained in " + root);
            throw;
        }
        finally
        {
            if (connection is not null) await connection.DisposeAsync();
            serviceA.Dispose();
            serviceB.Dispose();
        }
        if (retained.Any(path => !File.Exists(path))) throw new Exception("Disposing services removed completed clipboard files.");
        if (passed)
        {
            // Only remove this run's generated source and cache files.
            string allowedRoot = Path.GetFullPath(AppContext.BaseDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!root.StartsWith(allowedRoot, StringComparison.OrdinalIgnoreCase)) throw new Exception("Unexpected cleanup root.");
            Directory.Delete(root, recursive: true);
        }
        Console.WriteLine("All clipboard service tests passed; the Windows clipboard was never accessed.");
    }

    private static bool IsChunk(ReadOnlyMemory<byte> bytes)
    {
        using JsonDocument json = JsonDocument.Parse(bytes);
        return json.RootElement.TryGetProperty("Message", out JsonElement message) && message.ValueKind == JsonValueKind.Object &&
            message.TryGetProperty("Kind", out JsonElement kind) && kind.GetString() == "chunk";
    }

    private static async Task Baseline(Connection connection, CancellationToken ct)
    {
        await Until(() => connection.First.ClipboardAvailable && connection.Second.ClipboardAvailable, ct);
        await Task.Delay(350, ct);
    }

    private static async Task Until(Func<bool> condition, CancellationToken ct)
    {
        while (!condition()) await Task.Delay(15, ct);
    }

    private static async Task CreateFile(string path, long length, CancellationToken ct)
    {
        byte[] buffer = RandomNumberGenerator.GetBytes(64 * 1024);
        await using FileStream file = new(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read, buffer.Length, FileOptions.Asynchronous);
        while (length > 0)
        {
            int bytes = (int)Math.Min(buffer.Length, length);
            await file.WriteAsync(buffer.AsMemory(0, bytes), ct);
            length -= bytes;
        }
    }

    private static async Task AssertFiles(string[] source, string[] destination, CancellationToken ct)
    {
        if (source.Length != destination.Length) throw new Exception("Received file count changed.");
        for (int index = 0; index < source.Length; index++)
        {
            if (source[index] == destination[index] || Path.GetFileName(source[index]) != Path.GetFileName(destination[index]))
                throw new Exception("File transfer did not produce distinct, correctly named local files.");
            await using FileStream original = File.OpenRead(source[index]);
            await using FileStream received = File.OpenRead(destination[index]);
            if (original.Length != received.Length || !Enumerable.SequenceEqual(await SHA256.HashDataAsync(original, ct), await SHA256.HashDataAsync(received, ct)))
                throw new Exception("Received file checksum or size differs from its source.");
        }
    }

    private static async Task TestNegativeAcknowledgement(Connection connection, FakeClipboard destination, CancellationToken ct)
    {
        Guid transfer = Guid.NewGuid(), beginId = Guid.NewGuid(), chunkId = Guid.NewGuid();
        TaskCompletionSource<bool> beginAck = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<bool> chunkAck = new(TaskCreationOptions.RunContinuationsAsynchronously);
        void ObserveAcknowledgement(ReadOnlyMemory<byte> bytes)
        {
            using JsonDocument document = JsonDocument.Parse(bytes);
            JsonElement envelope = document.RootElement;
            if (!envelope.TryGetProperty("Ack", out JsonElement ack) || !ack.GetBoolean() ||
                !envelope.TryGetProperty("Id", out JsonElement idElement) || !idElement.TryGetGuid(out Guid id)) return;
            bool accepted = !envelope.TryGetProperty("Accepted", out JsonElement result) || result.GetBoolean();
            if (id == beginId) beginAck.TrySetResult(accepted);
            if (id == chunkId) chunkAck.TrySetResult(accepted);
        }
        int writes = destination.WriteCount;
        connection.First.ClipboardReceived += ObserveAcknowledgement;
        try
        {
            ClipboardMessage begin = new("begin", transfer, "text", TotalBytes: 0,
                Sha256: Convert.ToHexString(SHA256.HashData(Array.Empty<byte>())));
            await connection.First.SendClipboardAsync(JsonSerializer.SerializeToUtf8Bytes(new { Id = beginId, Ack = false, Message = begin }), ct);
            if (!await beginAck.Task.WaitAsync(TimeSpan.FromSeconds(5), ct)) throw new Exception("Receiver rejected valid clipboard begin metadata.");
            ClipboardMessage malformed = new("chunk", transfer, Data: new byte[] { 42 });
            await connection.First.SendClipboardAsync(JsonSerializer.SerializeToUtf8Bytes(new { Id = chunkId, Ack = false, Message = malformed }), ct);
            if (await chunkAck.Task.WaitAsync(TimeSpan.FromSeconds(5), ct)) throw new Exception("Receiver positively acknowledged a chunk larger than its declared transfer size.");
            await Task.Delay(200, ct);
            if (destination.WriteCount != writes) throw new Exception("A rejected clipboard transfer reached the clipboard adapter.");
        }
        finally { connection.First.ClipboardReceived -= ObserveAcknowledgement; }
    }

    private sealed class FakeClipboard : IClipboardAdapter
    {
        private readonly object _gate = new();
        private readonly List<ClipboardContent> _writes = [];
        private readonly HashSet<int> _workerThreads = [];
        private uint _sequence = 1;
        private ClipboardContent? _content;
        public uint Sequence { get { lock (_gate) return _sequence; } }
        internal ClipboardContent? Current { get { lock (_gate) return Clone(_content); } }
        internal ClipboardContent[] Writes { get { lock (_gate) return _writes.ToArray(); } }
        internal int WriteCount { get { lock (_gate) return _writes.Count; } }
        internal int[] WorkerThreads { get { lock (_gate) return _workerThreads.ToArray(); } }

        public ClipboardContent? Read()
        {
            lock (_gate) { RecordSta(); return Clone(_content); }
        }
        public void Write(ClipboardContent content)
        {
            lock (_gate) { RecordSta(); _content = Clone(content); _writes.Add(Clone(content)!); _sequence++; }
        }
        internal void Copy(ClipboardContent content)
        {
            lock (_gate) { _content = Clone(content); _sequence++; }
        }
        private void RecordSta()
        {
            if (Thread.CurrentThread.GetApartmentState() != ApartmentState.STA) throw new Exception("Clipboard work left its STA.");
            _workerThreads.Add(Environment.CurrentManagedThreadId);
        }
        private static ClipboardContent? Clone(ClipboardContent? content) => content is null ? null : new(content.Text, content.Files?.ToArray());
    }

    private sealed class Connection : IAsyncDisposable
    {
        private readonly ClipboardSharingService _serviceA, _serviceB;
        private readonly CancellationTokenSource _stopping;
        private readonly ConcurrentDictionary<int, TaskCompletionSource<MousePacket>> _mouse = new();
        private readonly Task _readA, _readB;
        internal MousePeer First { get; }
        internal MousePeer Second { get; }

        private Connection(MousePeer first, MousePeer second, ClipboardSharingService serviceA, ClipboardSharingService serviceB, CancellationToken ct)
        {
            First = first; Second = second; _serviceA = serviceA; _serviceB = serviceB;
            _stopping = CancellationTokenSource.CreateLinkedTokenSource(ct);
            serviceA.Attach(first); serviceB.Attach(second);
            _readA = first.ReadLoopAsync(_ => { }, _stopping.Token);
            _readB = second.ReadLoopAsync(packet => { if (packet.Kind == "move") _mouse.GetOrAdd(packet.Dx, _ => new(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult(packet); }, _stopping.Token);
        }

        internal Task<MousePacket> WaitForMouse(int dx) => _mouse.GetOrAdd(dx, _ => new(TaskCreationOptions.RunContinuationsAsynchronously)).Task;

        internal static async Task<Connection> Create(ClipboardSharingService firstService, ClipboardSharingService secondService, CancellationToken ct)
        {
            TcpListener listener = new(IPAddress.Loopback, MousePeer.Port);
            listener.Start();
            try
            {
                byte[] key = MousePeer.ParseKey(MousePeer.NewKey());
                Task<MousePeer> connecting = MousePeer.ConnectAsync("127.0.0.1", key, ct);
                TcpClient socket = await listener.AcceptTcpClientAsync(ct);
                MousePeer second = await MousePeer.AcceptAsync(socket, key, ct);
                MousePeer first = await connecting;
                return new Connection(first, second, firstService, secondService, ct);
            }
            finally { listener.Stop(); }
        }

        public async ValueTask DisposeAsync()
        {
            _serviceA.Detach(First); _serviceB.Detach(Second);
            _stopping.Cancel(); First.Dispose(); Second.Dispose();
            foreach (Task reader in new[] { _readA, _readB })
            {
                try { await reader; }
                catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException) { }
            }
            _stopping.Dispose();
        }
    }
}
