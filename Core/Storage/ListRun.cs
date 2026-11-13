using System.Collections.Concurrent;

namespace DiskTree.Core.Storage;

/// <summary>
/// One scan (or one search, one "Dateien" row) as the provider sees it: the token that cancels it, how many requests
/// it has sent so far, and a place for state that a provider keeps for the length of the run (e.g. a listing it has
/// fetched in one go). The local disk ignores it; remote providers (S3, SFTP) pass <see cref="Cancellation"/> into
/// their requests and call <see cref="BeforeRequest"/> before each one.
/// <para>
/// A cost limit (<see cref="RequestLimit"/>) pauses the run when it is reached: the next requests wait until the
/// user decided (<see cref="Resume"/> or cancelling the token). <see cref="LimitReached"/> is raised once on the thread
/// that hit the limit.
/// </para>
/// </summary>
public sealed class ListRun
{
    private readonly ManualResetEventSlim _open = new(initialState: true);
    private readonly object _gate = new();
    private long _requests;

    public ListRun(CancellationToken cancellation = default, long requestLimit = 0)
    {
        Cancellation = cancellation;
        RequestLimit = requestLimit;
    }

    /// <summary>Cancels the run (Esc, closing the tab): providers pass it into their network calls.</summary>
    public CancellationToken Cancellation { get; }

    /// <summary>Requests sent so far (all threads).</summary>
    public long Requests => Interlocked.Read(ref _requests);

    /// <summary>The run pauses when this many requests were sent (0 = no limit).</summary>
    public long RequestLimit { get; private set; }

    /// <summary>The limit was reached: requests wait until <see cref="Resume"/> or until the run is cancelled.</summary>
    public bool IsPaused => !_open.IsSet;

    /// <summary>Raised once per limit (on a worker thread), with the run paused.</summary>
    public event Action<ListRun>? LimitReached;

    /// <summary>State a provider keeps for this run (keyed by a name of its own choosing).</summary>
    public ConcurrentDictionary<string, object> State { get; } = new();

    /// <summary>
    /// Call before sending a request: waits while the run is paused, counts the request and pauses the run when it
    /// reaches the limit. Throws <see cref="OperationCanceledException"/> once the run is cancelled.
    /// </summary>
    public void BeforeRequest()
    {
        bool raise;
        while (true)
        {
            Cancellation.ThrowIfCancellationRequested();
            if (!_open.IsSet) _open.Wait(Cancellation);
            // Check and count under one lock: a request that passed the wait just before another one hit the limit
            // must wait as well instead of slipping through.
            lock (_gate)
            {
                if (!_open.IsSet) continue;
                long n = Interlocked.Increment(ref _requests);
                raise = RequestLimit > 0 && n == RequestLimit;
                if (raise) _open.Reset();
                break;
            }
        }
        if (raise) LimitReached?.Invoke(this);
    }

    /// <summary>Carries on after the limit; the next pause comes at <paramref name="nextLimit"/> requests (0 = never).</summary>
    public void Resume(long nextLimit = 0)
    {
        lock (_gate)
        {
            RequestLimit = nextLimit;
            _open.Set();
        }
    }
}
