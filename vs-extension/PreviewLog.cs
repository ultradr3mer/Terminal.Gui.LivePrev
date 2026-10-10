using Microsoft.VisualStudio.Extensibility;
using Microsoft.VisualStudio.Extensibility.Documents;

namespace TuiLivePreview.VisualStudio;

/// <summary>
/// The diagnostics log: the "TUI Preview" pane under View &gt; Output, and a file beside it.
/// <para>
/// A message box is a bad place to find out why a preview did not start, and "nothing happened"
/// is indistinguishable from "the extension never loaded". Every step is logged so the cause is
/// recoverable after the fact. The file exists because the extension cannot bring its Output pane
/// to the front, but it can open a file.
/// </para>
/// </summary>
internal sealed class PreviewLog : IDisposable
{
    private readonly object _gate = new();
    private readonly string _file;
    private OutputChannel? _channel;

    /// <summary>
    /// Writes to the Output pane, chained so lines arrive in the order they were logged. The chain
    /// starts with creating the pane, so lines logged before it exists are not lost.
    /// </summary>
    private Task _pending;

    public PreviewLog(VisualStudioExtensibility extensibility, string file)
    {
        _file = file;

        // A fresh file per session: the log answers "what happened just now", and an ever-growing
        // file would bury that.
        try
        {
            File.WriteAllText(_file, string.Empty);
        }
        catch (IOException)
        {
        }

        _pending = Task.Run(async () => _channel = await CreateChannelAsync(extensibility));
    }

    public void Line(string message)
    {
        string line = $"[{DateTime.Now:HH:mm:ss}] {message}";
        lock (_gate)
        {
            try
            {
                File.AppendAllText(_file, line + Environment.NewLine);
            }
            catch (IOException)
            {
                // The log must never be the reason a preview fails.
            }

            _pending = _pending.ContinueWith(
                async _ =>
                {
                    if (_channel is { } channel)
                    {
                        await channel.WriteLineAsync(line);
                    }
                },
                TaskScheduler.Default).Unwrap();
        }
    }

    /// <summary>
    /// A copy of the log to open in the editor. Older copies are deleted, so showing the log twice
    /// leaves one file behind rather than two.
    /// </summary>
    public string Snapshot()
    {
        string dir = Path.GetDirectoryName(_file)!;
        foreach (string old in Directory.EnumerateFiles(dir, "TUI Preview log *.log"))
        {
            try
            {
                File.Delete(old);
            }
            catch (IOException)
            {
                // Still open in the editor; it goes next time.
            }
        }

        string copy = Path.Combine(dir, $"TUI Preview log {DateTime.Now:HHmmss}.log");
        lock (_gate)
        {
            File.Copy(_file, copy, overwrite: true);
        }

        return copy;
    }

    public void Dispose()
    {
        _channel?.Dispose();
    }

    private static async Task<OutputChannel?> CreateChannelAsync(VisualStudioExtensibility extensibility)
    {
        try
        {
            return await extensibility.Views().Output.CreateOutputChannelAsync("TUI Preview", CancellationToken.None);
        }
        catch (Exception)
        {
            // Without the pane the file log still works.
            return null;
        }
    }
}
