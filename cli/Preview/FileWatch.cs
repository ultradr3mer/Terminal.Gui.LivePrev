namespace TuiPreview.Cli.Preview;

internal sealed class PollingFileWatch : IDisposable
{
    private readonly string _path;
    private readonly Timer _timer;
    private DateTime _lastWrite;
    private long _lastSize;

    public event Action<string>? Changed;

    public PollingFileWatch(string path, int intervalMs = 500)
    {
        _path = Path.GetFullPath(path);
        _timer = new Timer(_ => Tick(), null, 0, intervalMs);
    }

    private void Tick()
    {
        try
        {
            var info = new FileInfo(_path);
            if (!info.Exists) return;

            // Size is included because a save can land within the same mtime tick.
            if (info.LastWriteTimeUtc != _lastWrite || info.Length != _lastSize)
            {
                var first = _lastWrite == default;
                _lastWrite = info.LastWriteTimeUtc;
                _lastSize = info.Length;
                if (!first) Changed?.Invoke(_path);
            }
        }
        catch (IOException) { /* file mid-replace, try again next tick */ }
    }

    public void Dispose() => _timer.Dispose();
}
