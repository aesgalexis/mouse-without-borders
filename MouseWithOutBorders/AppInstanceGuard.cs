namespace MouseWithOutBorders;

internal sealed class AppInstanceGuard : IDisposable
{
    private readonly Mutex _mutex;
    internal bool Acquired { get; }

    internal AppInstanceGuard(string name = @"Local\MouseWithOutBorders-79DBA1BD")
    {
        // An exited process can leave the named object alive. Object existence
        // is not ownership: acquire the mutex, including abandoned ownership.
        _mutex = new Mutex(false, name);
        try { Acquired = _mutex.WaitOne(0); }
        catch (AbandonedMutexException) { Acquired = true; }
    }

    public void Dispose()
    {
        if (Acquired) _mutex.ReleaseMutex();
        _mutex.Dispose();
    }
}
