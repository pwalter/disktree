namespace DiskTree.Core.Storage;

/// <summary>
/// Optional for providers that talk to a server over a connection (SFTP). The connection is opened lazily by the first
/// listing anyway; the UI calls <see cref="Connect"/> beforehand (on a background thread) so login, host key and network
/// problems show up as one clear error before a scan starts instead of as "nicht lesbar" folders.
/// <para>
/// The scanner does not pass its <see cref="CancellationToken"/> to <see cref="IStorageProvider.Enumerate{T}"/>; it
/// checks it before each folder. <see cref="CancelWith"/> lets a cancel also abort the requests that are running at
/// that moment (a slow server would otherwise keep the scan alive until its timeout).
/// </para>
/// </summary>
public interface IConnectedStorage : IDisposable
{
    /// <summary>Is a connection open right now?</summary>
    bool IsConnected { get; }

    /// <summary>
    /// Opens the connection (or checks the open one). Throws <see cref="System.IO.IOException"/> (network, host key)
    /// or <see cref="UnauthorizedAccessException"/> (login), <see cref="OperationCanceledException"/> on cancel.
    /// </summary>
    void Connect(CancellationToken ct);

    /// <summary>Closes the connection; the next listing opens a new one.</summary>
    void Disconnect();

    /// <summary>
    /// When <paramref name="ct"/> is cancelled, the requests running at that moment are aborted (they end with
    /// <see cref="OperationCanceledException"/>); later requests open a new connection. Dispose the result when the
    /// operation is over.
    /// </summary>
    IDisposable CancelWith(CancellationToken ct);
}
