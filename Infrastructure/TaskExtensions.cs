namespace DiskTree.Infrastructure;

public static class TaskExtensions
{
    /// <summary>
    /// For "fire and forget" calls (<c>_ = FooAsync()</c>): the task still runs unawaited, but an exception is written to the
    /// crash log instead of being lost. Cancellation is not an error. <paramref name="onError"/> (optional) sees the exception too.
    /// </summary>
    public static void Forget(this Task task, string source, Action<Exception>? onError = null)
    {
        if (task.IsCompletedSuccessfully) return;
        task.ContinueWith(t =>
        {
            var inner = t.Exception!.Flatten().InnerExceptions;
            if (inner.All(e => e is OperationCanceledException)) return;
            var ex = inner.Count == 1 ? inner[0] : t.Exception;
            CrashLog.Write(source, ex);
            try { onError?.Invoke(ex); } catch { }
        }, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);
    }
}
