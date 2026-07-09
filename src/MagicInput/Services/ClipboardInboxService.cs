namespace MagicInput.Services;

public sealed class ClipboardInboxService : IDisposable
{
    private const int MaxTextBytes = 4 * 1024 * 1024;
    private readonly object _gate = new();
    private readonly HashSet<string> _queuedPaths = new(StringComparer.OrdinalIgnoreCase);
    private FileSystemWatcher? _watcher;
    private bool _disposed;

    public event EventHandler<ClipboardInboxItemEventArgs>? ClipboardTextReceived;
    public event EventHandler<string>? StatusChanged;

    public static string InboxDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MagicInput", "clipboard-inbox");

    public string Status { get; private set; } = "Clipboard inbox is off.";

    public void Start()
    {
        ThrowIfDisposed();

        if (_watcher != null)
        {
            return;
        }

        Directory.CreateDirectory(InboxDirectory);
        _watcher = new FileSystemWatcher(InboxDirectory, "*.clip")
        {
            IncludeSubdirectories = false,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size
        };
        _watcher.Created += (_, e) => QueueFile(e.FullPath);
        _watcher.Changed += (_, e) => QueueFile(e.FullPath);
        _watcher.Renamed += (_, e) => QueueFile(e.FullPath);
        _watcher.Error += (_, e) => SetStatus("Clipboard inbox watcher failed: " + e.GetException().Message);
        _watcher.EnableRaisingEvents = true;

        SetStatus("Listening for Mac clipboard text.");
        foreach (var path in Directory.EnumerateFiles(InboxDirectory, "*.clip"))
        {
            QueueFile(path);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _watcher?.Dispose();
        _watcher = null;
    }

    private void QueueFile(string path)
    {
        if (_disposed)
        {
            return;
        }

        lock (_gate)
        {
            if (!_queuedPaths.Add(path))
            {
                return;
            }
        }

        _ = Task.Run(() => ProcessFileAsync(path));
    }

    private async Task ProcessFileAsync(string path)
    {
        try
        {
            var text = await ReadTextWhenReadyAsync(path).ConfigureAwait(false);
            ClipboardTextReceived?.Invoke(this, new ClipboardInboxItemEventArgs(text, path));
            TryDelete(path);
            SetStatus($"Received clipboard text at {DateTime.Now:t}.");
        }
        catch (Exception ex)
        {
            SetStatus("Clipboard import failed: " + ex.Message);
        }
        finally
        {
            lock (_gate)
            {
                _queuedPaths.Remove(path);
            }
        }
    }

    private static async Task<string> ReadTextWhenReadyAsync(string path)
    {
        for (var attempt = 0; attempt < 30; attempt++)
        {
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
                if (stream.Length > MaxTextBytes)
                {
                    throw new InvalidOperationException("clipboard text is larger than 4 MB");
                }

                using var reader = new StreamReader(stream, detectEncodingFromByteOrderMarks: true);
                return await reader.ReadToEndAsync().ConfigureAwait(false);
            }
            catch (IOException) when (attempt < 29)
            {
                await Task.Delay(100).ConfigureAwait(false);
            }
            catch (UnauthorizedAccessException) when (attempt < 29)
            {
                await Task.Delay(100).ConfigureAwait(false);
            }
        }

        throw new IOException("clipboard inbox file stayed locked");
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch
        {
            // A stale inbox file is harmless and can be overwritten by the next import.
        }
    }

    private void SetStatus(string status)
    {
        Status = status;
        StatusChanged?.Invoke(this, status);
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(ClipboardInboxService));
        }
    }
}

public sealed class ClipboardInboxItemEventArgs : EventArgs
{
    public ClipboardInboxItemEventArgs(string text, string sourcePath)
    {
        Text = text;
        SourcePath = sourcePath;
    }

    public string Text { get; }
    public string SourcePath { get; }
}
