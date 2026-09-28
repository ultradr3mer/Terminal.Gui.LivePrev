using System.Diagnostics;
using System.Globalization;
using Terminal.Gui.App;
using Terminal.Gui.Drawing;
using Terminal.Gui.Drivers;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace TuiPreview.Host.Preview;

/// <summary>
/// Interactive live preview: a log pane on top, the real previewed view underneath.
/// <para>
/// The bottom pane hosts the <em>actual</em> view instance, not a picture of it, so
/// keystrokes reach your TextField and Button. Use the plain render mode
/// (<c>--render</c> without <c>--interactive</c>) when you only want a printed snapshot.
/// </para>
/// <para>Keys — there are no buttons by design:</para>
/// <list type="bullet">
///   <item><description><c>Ctrl+C</c> — quit</description></item>
///   <item><description><c>Ctrl+R</c> / <c>F5</c> — force rebuild</description></item>
///   <item><description><c>Ctrl+Up</c> / <c>Ctrl+Down</c> — grow / shrink the log pane</description></item>
///   <item><description><c>Ctrl+0</c> — reset the log pane height</description></item>
/// </list>
/// </summary>
internal sealed class PreviewBuilder
{


  /// <summary>
  /// How to produce the view under test.
  /// <para>
  /// A delegate rather than a XAML path, so the host does not know or care where the view
  /// comes from. The XAML path goes through the source generator; the C# path compiles a
  /// plain class. Both arrive here as "give me a built view", which is all this window ever
  /// needed from them.
  /// </para>
  /// </summary>
  private readonly Func<RenderPipeline.BuildResult> _build;

  /// <summary>
  /// The file this preview is *about*. Used for the window title and to locate the
  /// .previewrc, never for reading the view - the builder owns that.
  /// <para>
  /// This may differ from the file actually compiled when the editor holds unsaved changes:
  /// <summary>
  /// The file this preview is *about*, used only for the window title. Never for reading the
  /// view: the build delegate owns that.
  /// <para>
  /// It may differ from the file actually compiled, because the editor mirrors an unsaved
  /// buffer to a scratch copy whose name carries a content hash - and that hash must never
  /// reach the screen.
  /// </para>
  /// </summary>
  private readonly string _displayName;

  /// <summary>
  /// The chrome: the full-screen <see cref="PreviewChrome"/> this builder drives.
  /// <para>
  /// The split is deliberate. Everything visual - toolbar, panes, splitter, log, keys - belongs
  /// to the chrome, which is a component and can be previewed on its own. Everything that needs
  /// the build pipeline belongs here, and that is exactly one thing: turning a file into a
  /// runnable view. No layout lives in this class, which is why it is neither previewable nor
  /// testable as a view - and does not need to be.
  /// </para>
  /// </summary>
  public PreviewChrome Chrome { get; } = new();

  private View? _current;

  public PreviewBuilder(string displayName, Func<RenderPipeline.BuildResult> build)
  {
    _displayName = displayName;
    _build = build;

    // The chrome names the view it previews. _displayName is the file the user recognises,
    // which is not necessarily the one compiled: an editor mirrors an unsaved buffer to a
    // scratch copy whose name carries a content hash, and that hash must never be shown.
    Chrome.Title = $"TuiLivePreview - {Path.GetFileName(_displayName)}";

    // The chrome asks; this class answers. One place wires them, so an action cannot be
    // half-wired: it is either a chrome event handled here, or it is not offered.
    Chrome.RebuildRequested += Rebuild;
    Chrome.QuitRequested += () => Chrome.RequestStop();

    Chrome.AppendLog($"Edit {Path.GetFileName(_displayName)} and save to see changes.");
  }

  /// <summary>Test seam: render the whole interactive window (log + view) to a string.</summary>
  public string Snapshot(int width = 80, int height = 30)
  {
    using IApplication app = Application.Create();
    app.StopAfterFirstIteration = true;
    app.Init(DriverRegistry.Names.ANSI);

    // SetScreenSize, not the Cols/Rows setters: assigning those only updates the reported
    // dimensions, and a view laid out with Dim.Fill() against the old buffer comes back
    // cropped to it.
    app.Driver!.SetScreenSize(width, height);
    app.Run(Chrome);
    return app.Driver.ToString();
  }

  /// <summary>Re-reads the view source, rebuilds, and swaps the view in.</summary>
  /// <remarks>
  /// Delegates to <see cref="_build"/>, so this method is identical whether the view came
  /// from a .tui.xaml through the source generator or from a plain .cs compiled on its own.
  /// </remarks>
  public void Rebuild()
  {
    var sw = Stopwatch.StartNew();
    Chrome.BeginLogSegment();
    Chrome.AppendLog($"rebuilding {Path.GetFileName(_displayName)} ...");

    RenderPipeline.BuildResult result;
    try
    {
      result = _build();
    }
    catch (Exception ex)
    {
      ShowFailure($"preview threw: {ex.GetType().Name}: {ex.Message}");
      return;
    }

    foreach (var d in result.AllDiagnostics)
    {
      Chrome.AppendLog(d);
    }

    if (!result.Success || result.Runnable is null)
    {
      ShowFailure(result.HasNoGeneratedCode
          ? "The generator produced no code."
          : "Could not compile the generated code.");
      return;
    }

    SwapView(result.Runnable);
    Chrome.AppendLog($"render in {sw.ElapsedMilliseconds} ms");
  }

  private void SwapView(IRunnable runnable)
  {
    // View.Add only accepts a View, and every .tui.xaml root is a Window, so this always
    // succeeds in practice. Guard anyway rather than throwing inside a rebuild.
    if (runnable is not View view)
    {
      ShowFailure($"'{runnable.GetType().Name}' is not a View and cannot be hosted.");
      return;
    }

    // Hosted as-is, but strip the inner window's own border and title so the user sees
    // their content inside a single frame rather than a window-inside-a-window.
    if (view is Window w)
    {
      w.Title = string.Empty;
      w.BorderStyle = LineStyle.None;
    }

    if (_current is not null)
    {
      Chrome.PreviewPane.Remove(_current);
      _current.Dispose();
    }

    _current = view;
    Chrome.PreviewPane.Add(view);
  }

  /// <summary>No renderable view: show the reason in the bottom pane instead.</summary>
  private void ShowFailure(string message)
  {
    if (_current is not null)
    {
      Chrome.PreviewPane.Remove(_current);
      _current.Dispose();
      _current = null;
    }

    Chrome.PreviewPane.Add(new Label
    {
      X = 1,
      Y = 0,
      Text = message,
      CanFocus = false,
    });
  }
}
