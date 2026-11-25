using System.Collections.Concurrent;
using System.IO;

namespace RtFileMonitor;

internal readonly record struct FileStamp(DateTime ModifiedUtc, long Size)
{
    public static FileStamp? Read(string path)
    {
        var file = new FileInfo(path);
        return file.Exists ? new FileStamp(file.LastWriteTimeUtc, file.Length) : null;
    }
}

internal sealed class FolderMonitor(
    string root, Action<string, FileStamp?, bool> changed, Action<string?> status,
    bool sessionsOnly = false) : IDisposable
{
    private readonly CancellationTokenSource stop = new();
    private readonly ConcurrentDictionary<string, Signal> pending = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, FileStamp> snapshot = new(StringComparer.OrdinalIgnoreCase);
    private readonly record struct Signal(long At, bool Created, bool Removed);
    private Task? worker;
    private int rescan;
    private int watcherFailed;

    public void Start() => worker = Task.Run(RunAsync);

    private async Task RunAsync()
    {
        FileSystemWatcher? watcher = null;
        bool baselineReady = false;
        string? lastError = null;
        var nextRecoveryScan = 0L;
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(150));
            do
            {
                try
                {
                    if (!Directory.Exists(root))
                        throw new DirectoryNotFoundException("The folder is unavailable.");
                    if (Interlocked.Exchange(ref watcherFailed, 0) != 0)
                    {
                        watcher?.Dispose();
                        watcher = null;
                        Interlocked.Exchange(ref rescan, 1);
                    }
                    if (watcher is null)
                        watcher = Watch();
                    if (!baselineReady || Interlocked.Exchange(ref rescan, 0) != 0
                        || Environment.TickCount64 >= nextRecoveryScan)
                    {
                        Scan(baselineReady);
                        baselineReady = true;
                        nextRecoveryScan = Environment.TickCount64 + 5000;
                    }
                    Drain();
                    if (lastError is not null)
                        status(lastError = null);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
                {
                    watcher?.Dispose();
                    watcher = null;
                    Interlocked.Exchange(ref rescan, 1);
                    if (lastError != error.Message)
                        status(lastError = error.Message);
                    await Task.Delay(1000, stop.Token);
                }
            }
            while (await timer.WaitForNextTickAsync(stop.Token));
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        finally { watcher?.Dispose(); }
    }

    private FileSystemWatcher Watch()
    {
        var watcher = new FileSystemWatcher(root)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName |
                NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.CreationTime
        };
        watcher.Created += (_, e) => Queue(e.FullPath, created: true);
        watcher.Changed += (_, e) => Queue(e.FullPath);
        watcher.Deleted += (_, e) => Queue(e.FullPath, removed: true);
        watcher.Renamed += (_, e) =>
        {
            Queue(e.OldFullPath, removed: true);
            Queue(e.FullPath, created: true);
        };
        watcher.Error += (_, _) => Interlocked.Exchange(ref watcherFailed, 1);
        try
        {
            watcher.EnableRaisingEvents = true;
            return watcher;
        }
        catch
        {
            watcher.Dispose();
            throw;
        }
    }

    private void Queue(string path, bool created = false, bool removed = false)
    {
        var now = Environment.TickCount64;
        pending.AddOrUpdate(path, new Signal(now, created, removed),
            (_, prior) => new Signal(now, prior.Created || created, prior.Removed || removed));
    }

    private bool Includes(string path) => !sessionsOnly ||
        string.Equals(Path.GetExtension(path), ".jsonl", StringComparison.OrdinalIgnoreCase);

    internal static IEnumerable<string> EnumerateFiles(string root, string pattern = "*") =>
        Directory.EnumerateFiles(root, pattern, new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = false,
            AttributesToSkip = FileAttributes.ReparsePoint,
            MatchCasing = MatchCasing.CaseInsensitive
        });

    private void Scan(bool report)
    {
        var files = new Dictionary<string, FileStamp>(StringComparer.OrdinalIgnoreCase);
        foreach (string path in EnumerateFiles(root))
        {
            if (Includes(path) && FileStamp.Read(path) is { } stamp)
                files[path] = stamp;
        }
        if (report)
        {
            foreach (var (path, stamp) in files)
            {
                if (!pending.ContainsKey(path))
                    Report(path, stamp, false);
            }
            foreach (var path in snapshot.Keys.ToArray())
            {
                if (!files.ContainsKey(path) && !pending.ContainsKey(path))
                    Report(path, null, false);
            }
        }
        else
        {
            foreach (var (path, stamp) in files)
                snapshot[path] = stamp;
        }
    }

    private void Drain()
    {
        foreach (var pair in pending)
        {
            if (Environment.TickCount64 - pair.Value.At < 300 || !pending.TryRemove(pair))
                continue;
            var (path, signal) = pair;
            if (signal.Removed || (signal.Created && Directory.Exists(path)))
                Interlocked.Exchange(ref rescan, 1);
            if (Includes(path))
                Report(path, FileStamp.Read(path), signal.Created || signal.Removed, watcherSignal: true);
        }
    }

    private void Report(string path, FileStamp? stamp, bool explicitChange, bool watcherSignal = false)
    {
        if (stamp is { } current)
        {
            bool existed = snapshot.TryGetValue(path, out var previous);
            snapshot[path] = current;
            if (!existed || previous != current || (sessionsOnly && watcherSignal))
                changed(path, current, !existed || explicitChange);
        }
        else if (snapshot.Remove(path))
            changed(path, null, false);
    }

    public void Dispose()
    {
        stop.Cancel();
        if (worker is { } task)
            _ = task.ContinueWith(_ => stop.Dispose(), TaskScheduler.Default);
        else
            stop.Dispose();
    }
}
