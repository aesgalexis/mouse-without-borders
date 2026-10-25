using System.Collections.Concurrent;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace MouseWithOutBorders;

// Never call SendInput on the thread hosting WH_MOUSE_LL. Windows may wait for
// hook delivery inside SendInput; that thread must remain free to pump it.
internal sealed class MouseInputQueue : IDisposable
{
    internal const int StallTimeoutMilliseconds = 1000;
    private readonly BlockingCollection<NativeMouse.Input[]> _pending = new(2048);
    private readonly NativeMouse.InputSender _send;
    private readonly Action<Exception> _error;
    private readonly Thread _worker;
    private long _sendingSince;
    private int _stopped;
    private int _reported;
    private int _outstanding;
    private int _failed;

    internal bool Pending => Volatile.Read(ref _outstanding) != 0;

    internal MouseInputQueue(Action<Exception> error, NativeMouse.InputSender? send = null)
    {
        _error = error;
        _send = send ?? NativeMouse.SendInput;
        _worker = new Thread(Run) { IsBackground = true, Name = "MWB native mouse output" };
        _worker.Start();
    }

    internal uint Enqueue(uint count, NativeMouse.Input[] inputs, int size)
    {
        if (Volatile.Read(ref _stopped) != 0) throw new IOException("Mouse output stopped.");
        Interlocked.Increment(ref _outstanding);
        try
        {
            if (!_pending.TryAdd(inputs.ToArray())) throw new IOException("Mouse output backlog exceeded its limit.");
        }
        catch (Exception ex)
        {
            Interlocked.Decrement(ref _outstanding);
            if (ex is InvalidOperationException) throw new IOException("Mouse output stopped.");
            throw;
        }
        return count;
    }

    private void Run()
    {
        try
        {
            foreach (NativeMouse.Input[] input in _pending.GetConsumingEnumerable())
            {
                if (Volatile.Read(ref _failed) != 0) break;
                Volatile.Write(ref _sendingSince, Environment.TickCount64);
                try
                {
                    if (_send((uint)input.Length, input, Marshal.SizeOf<NativeMouse.Input>()) != input.Length)
                        throw new Win32Exception(Marshal.GetLastWin32Error(),
                            "Windows rejected mouse input. Sharing stopped; local input is available.");
                }
                finally
                {
                    Volatile.Write(ref _sendingSince, 0);
                    Interlocked.Decrement(ref _outstanding);
                }
            }
        }
        catch (Exception ex) { Report(ex); }
    }

    internal void CheckHealth()
    {
        long since = Volatile.Read(ref _sendingSince);
        if (since != 0 && unchecked(Environment.TickCount64 - since) >= StallTimeoutMilliseconds)
            Report(new IOException("Windows mouse output stalled. Sharing stopped to restore local input."));
    }

    private void Report(Exception error)
    {
        Volatile.Write(ref _failed, 1);
        Dispose();
        if (Interlocked.Exchange(ref _reported, 1) == 0) _error(error);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _stopped, 1) != 0) return;
        _pending.CompleteAdding();
        // A native call may be stalled. Never wait on it from a hook, UI or
        // shutdown thread; the hook must be released even in that case.
    }
}
