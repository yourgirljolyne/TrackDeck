namespace TrackDeck;

/// <summary>
/// One lock for the keyboard's file buffer, shared by every Deck app and Admiral (a named mutex, so it works across
/// programs). The buffer is global on the keyboard: when two programs used it at once, one could unbind the other's
/// file halfway and its write landed in the wrong file (2026-11-23: DeckMapper's key assignments ended up holding a
/// single action file while Admiral was running). Every file read, write and delete in <see cref="Bragi"/> holds it.
/// </summary>
/// <remarks>
/// A mutex belongs to the thread that took it, and async code moves between threads, so a small dedicated thread
/// takes it and gives it back. If a program dies while holding it, the next one gets it as abandoned and carries on.
/// </remarks>
public static class KeyboardLock
{
    public const string Name = "VanguardAir99.FileBuffer";
    public static TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(15);

    public static async Task<IDisposable> AcquireAsync()
    {
        var got = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new ManualResetEventSlim();
        var thread = new Thread(() =>
        {
            using var m = new Mutex(false, Name);
            bool owned;
            try { owned = m.WaitOne(Timeout); }
            catch (AbandonedMutexException) { owned = true; }
            got.SetResult(owned);
            if (!owned) return;
            release.Wait();
            m.ReleaseMutex();
        }) { IsBackground = true, Name = "keyboard file lock" };
        thread.Start();
        if (!await got.Task)
            throw new BragiException("Another program has been using the keyboard's files for too long. Try again in a moment.");
        return new Held(release);
    }

    private sealed class Held(ManualResetEventSlim release) : IDisposable
    {
        private int _done;
        public void Dispose() { if (Interlocked.Exchange(ref _done, 1) == 0) release.Set(); }
    }
}
