namespace Rawr.Core.Services;

/// <summary>
/// Per-file debounced XMP sidecar writer. Schedule(path, data) cancels any
/// pending write for the same file and queues a fresh one ~500 ms later;
/// rapid streams of edits coalesce into a single disk write. Sidecar writes
/// are non-critical — failures are swallowed to keep the UI flowing.
/// </summary>
public sealed class XmpSidecarWriter : IDisposable
{
    private readonly TimeSpan _debounce;
    private readonly object _lock = new();
    private readonly Dictionary<string, (CancellationTokenSource Cts, Task Task)> _pending
        = new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;

    public XmpSidecarWriter(TimeSpan? debounce = null)
    {
        _debounce = debounce ?? TimeSpan.FromMilliseconds(500);
    }

    public void Schedule(string photoPath, XmpData data)
    {
        if (_disposed) return;
        var cts = new CancellationTokenSource();
        lock (_lock)
        {
            if (_pending.TryGetValue(photoPath, out var existing))
                existing.Cts.Cancel();
            var task = RunAsync(photoPath, data, cts);
            _pending[photoPath] = (cts, task);
        }
    }

    private async Task RunAsync(string photoPath, XmpData data, CancellationTokenSource cts)
    {
        try
        {
            await Task.Delay(_debounce, cts.Token).ConfigureAwait(false);
            await Task.Run(() => XmpSidecar.Write(photoPath, data), CancellationToken.None).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { /* superseded by a later Schedule */ }
        catch { /* sidecar write failures are non-critical */ }
        finally
        {
            lock (_lock)
            {
                if (_pending.TryGetValue(photoPath, out var p) && ReferenceEquals(p.Cts, cts))
                    _pending.Remove(photoPath);
            }
            cts.Dispose();
        }
    }

    /// <summary>
    /// Wait for all currently-pending writes to complete (or be cancelled by
    /// later schedules). Returns once the queue is drained.
    /// </summary>
    public async Task FlushAsync()
    {
        Task[] tasks;
        lock (_lock) { tasks = _pending.Values.Select(v => v.Task).ToArray(); }
        if (tasks.Length == 0) return;
        try { await Task.WhenAll(tasks).ConfigureAwait(false); } catch { }
    }

    /// <summary>
    /// Synchronous bounded-wait drain, suitable for shutdown paths that can't
    /// await. Pending writes that exceed the timeout are abandoned.
    /// </summary>
    public void Flush(TimeSpan timeout)
    {
        Task[] tasks;
        lock (_lock) { tasks = _pending.Values.Select(v => v.Task).ToArray(); }
        if (tasks.Length == 0) return;
        try { Task.WaitAll(tasks, timeout); } catch { }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _disposed = true;
            foreach (var p in _pending.Values)
                p.Cts.Cancel();
            _pending.Clear();
        }
    }
}
