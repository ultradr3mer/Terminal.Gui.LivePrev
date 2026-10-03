using Terminal.Gui.App;
using Terminal.Gui.Drivers;

namespace TuiPreview.Cli.Preview;

/// <summary>
/// Runs the interactive preview: a Terminal.Gui application hosting a
/// <see cref="PreviewBuilder"/>, rebuilt whenever the view file changes on disk.
/// </summary>
internal static class InteractiveSession
{
    /// <param name="displayName">The file to show in the title and to find the .previewrc.</param>
    /// <param name="watchedPath">
    /// The file whose mtime drives rebuilds. Usually the same as <paramref name="displayName"/>,
    /// but they diverge when the editor mirrors an unsaved buffer to a scratch copy: the
    /// scratch file is what changes, and that is what has to be polled.
    /// </param>
    /// <param name="build">Produces the view. See <see cref="PreviewBuilder"/>.</param>
    /// <param name="warnings">
    /// Problems with the view's own settings. They were printed before the session started, but
    /// the session takes over the screen, so they are repeated in the log pane to stay visible.
    /// </param>
    public static int Run(string displayName, string watchedPath,
        Func<RenderPipeline.BuildResult> build, IReadOnlyList<string> warnings)
    {
        var builder = new PreviewBuilder(displayName, watchedPath, build);
        var runnable = builder.Chrome;

        foreach (string warning in warnings)
        {
            runnable.AppendLog($"warning: {warning}", LogKind.Warning);
        }

        using IApplication app = Application.Create();
        app.Init(DriverRegistry.Names.ANSI);

        // Timer for refresh and time related status
        SetupTimer(app, builder, runnable, watchedPath);

        builder.Rebuild();

        // The chrome is the Runnable - the full-screen, borderless root app.Run accepts. The
        // builder is not a view: it turns a file into one, and hands it over here.
        app.Run(runnable);
        return 0;
    }

    private static void SetupTimer(IApplication app, PreviewBuilder builder, PreviewChrome runnable, string watchedPath)
    {
       TimeSpan FilePollInterval = TimeSpan.FromMilliseconds(400);

        // Poll on the main loop rather than a background thread, so the view swap already
        // happens on the UI thread and needs no marshalling.
        string? lastSeen = ReadStamp(watchedPath);
        int lastMinute = -1;
        app.AddTimeout(FilePollInterval, () =>
        {
            string current = ReadStamp(watchedPath);
            if (current != lastSeen)
            {
                lastSeen = current;
                builder.Rebuild();
                lastMinute = -1; // -1 forces UpdateStatus 
            }
            
            lastMinute = runnable.CheckMinuteEvent(lastMinute);

            return true; // keep polling
        });
    }

    /// <summary>
    /// Cheap change token: last write time plus length. Size is included because a save
    /// can land within the same mtime tick.
    /// </summary>
    private static string? ReadStamp(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists
                ? $"{info.LastWriteTimeUtc.Ticks}:{info.Length}"
                : null;
        }
        catch (IOException)
        {
            // File mid-replace; try again next tick.
            return null;
        }
    }
}
