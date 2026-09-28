using CommandLine;
using CommandLine.Text;
using TuiPreview.Host.Preview;

namespace TuiPreview.Host;

/// <summary>
/// Parses the command line and runs one preview mode. Shared by cli.exe and
/// <see cref="TuiPreviewHost.TryRun"/>, so both paths build and render the same way.
/// <para>
/// Runs inside someone else's process when hosted: it never calls <c>Environment.Exit</c>,
/// always returns, and puts back the console state it changes.
/// </para>
/// </summary>
internal static class PreviewRunner
{
  /// <param name="requireProject">True for cli.exe, which reads <c>--project</c> with MSBuild.</param>
  /// <param name="contextFor">Creates the source of the project context, once per run.</param>
  public static int Run(string[] args, bool requireProject, Func<Options, Func<ProjectContext>> contextFor)
  {
    // HelpWriter is disabled so that Fail() stays the single place that renders help,
    // which keeps --help and parse errors on one consistent path.
    using var parser = new Parser(settings => settings.HelpWriter = null);

    var result = parser.ParseArguments<Options>(args);
    return result.MapResult(o => Run(o, requireProject, contextFor), errors => Fail(result, errors));
  }

  private static int Run(Options options, bool requireProject, Func<Options, Func<ProjectContext>> contextFor)
  {
    var problems = new List<string>();

    CheckFile(options.ViewCs ?? string.Empty, "--view", problems);
    if (requireProject)
    {
      CheckFile(options.Project, "--project", problems);
    }
    CheckNonEmpty(options.Class, "--class", problems);

    foreach (string extra in options.Extra ?? Enumerable.Empty<string>())
    {
      CheckFile(extra, "--extra", problems);
    }

    if (problems.Count > 0)
    {
      foreach (var problem in problems)
      {
        Console.Error.WriteLine($"error: {problem}");
      }

      return 1;
    }

    // The rendered screen is box-drawing characters; without this they come out as mojibake.
    var previousEncoding = Console.OutputEncoding;
    Console.OutputEncoding = System.Text.Encoding.UTF8;
    try
    {
      Console.WriteLine($"{"view",-10}: {Path.GetFullPath(options.ViewCs!)}");
      Console.WriteLine($"{"proj",-10}: {(requireProject ? Path.GetFullPath(options.Project) : "(running app)")}");
      Console.WriteLine($"{"class",-10}: {options.Class}");

      return RunPreview(options, contextFor(options));
    }
    finally
    {
      Console.OutputEncoding = previousEncoding;
    }
  }

  private static int RunPreview(Options options, Func<ProjectContext> context)
  {
    string className = options.Class;
    int width = options.Width;
    int height = options.Height;

    // The file actually compiled. This may be a scratch copy of the real file when the
    // editor is mirroring an unsaved buffer, so the two are tracked separately.
    string source = Path.GetFullPath(options.ViewCs!);

    // Title only. Never the scratch copy, whose name carries a content hash.
    string displayName = options.DisplayName is { Length: > 0 } shown
        ? Path.GetFullPath(shown)
        : source;

    // Resolved once, so every rebuild compiles the same set of files.
    string[] extras = (options.Extra ?? Enumerable.Empty<string>())
        .Select(Path.GetFullPath)
        .ToArray();

    Func<RenderPipeline.BuildResult> build =
        () => RenderPipeline.BuildClass(source, className, extras, context());

    if (options.Interactive)
    {
      return InteractiveSession.Run(displayName, source, build);
    }

    if (options.UiSnapshot)
    {
      var ui = new PreviewBuilder(displayName, build);
      ui.Rebuild();

      for (int i = 1; i <= options.UiFlood; i++)
      {
        ui.Chrome.AppendLog($"flood line {i} of {options.UiFlood}");
      }
      Console.WriteLine(ui.Snapshot(width, Math.Max(height, 30)));
      return 0;
    }

    void RenderOnce()
    {
      RenderPipeline.Result result;
      try
      {
        RenderPipeline.BuildResult built = build();
        result = built.Success
            // Capture knows nothing about the build, so the build's diagnostics are carried
            // over; otherwise a successful render would silently drop its warnings.
            ? RenderPipeline.Capture(built.Runnable!, width, height) with
            {
              GeneratorDiagnostics = built.GeneratorDiagnostics,
              CompileDiagnostics = built.CompileDiagnostics,
            }
            : new RenderPipeline.Result(
                false, string.Empty, string.Empty,
                built.GeneratorDiagnostics, built.CompileDiagnostics);
      }
      catch (Exception ex)
      {
        Console.Error.WriteLine($"error: preview threw: {ex.GetType().Name}: {ex.Message}");
        return;
      }

      var log = new DiagnosticsLog(l => Console.WriteLine(l), result.AllDiagnostics);
      Console.WriteLine(log.GetDiagnosticsSummary());

      if (!result.Success)
      {
        Console.Error.WriteLine("error: could not render (see diagnostics above).");
        return;
      }

      if (Environment.GetEnvironmentVariable("TUI_ANSI") == "1")
      {
        Console.Write(result.Ansi);
      }
      else
      {
        Console.WriteLine(result.PlainText);
      }
    }

    if (!options.Watch)
    {
      RenderOnce();
      return 0;
    }

    using var watch = new PollingFileWatch(source);
    watch.Changed += _ => RenderOnce();

    Console.WriteLine($"watching {source}. Press Ctrl+C to stop.");
    RenderOnce();

    // Ctrl+C ends the watch and returns, instead of killing a process that may be the user's app.
    using var stop = new ManualResetEventSlim();
    ConsoleCancelEventHandler onCancel = (_, e) =>
    {
      e.Cancel = true;
      stop.Set();
    };

    Console.CancelKeyPress += onCancel;
    try
    {
      stop.Wait();
    }
    finally
    {
      Console.CancelKeyPress -= onCancel;
    }

    return 0;
  }

  private static void CheckNonEmpty(string value, string option, ICollection<string> problems)
  {
    if (string.IsNullOrWhiteSpace(value))
    {
      problems.Add($"{option} must not be empty.");
    }
  }

  private static void CheckFile(string value, string option, ICollection<string> problems)
  {
    if (string.IsNullOrWhiteSpace(value))
    {
      problems.Add($"{option} must not be empty.");
      return;
    }

    if (!File.Exists(value))
    {
      problems.Add($"{option} file '{value}' does not exist.");
    }
  }

  private static int Fail<T>(ParserResult<T> result, IEnumerable<Error> errors)
  {
    // AutoBuild already renders the parse errors, so DefaultParsingErrorsHandler must not be
    // called here or the ERROR(S) block would be printed twice.
    var help = HelpText.AutoBuild(
        result,
        h =>
        {
          h.Heading = "TuiLivePreview - live preview for a .tui.cs view";
          h.Copyright = string.Empty;
          h.AdditionalNewLineAfterOption = true;
          return h;
        });

    Console.Error.WriteLine(help);

    // Asking for help/version is a successful request, not a usage error.
    var isInfoRequest = errors.Any(e =>
        e.Tag is ErrorType.HelpRequestedError or ErrorType.HelpVerbRequestedError
            or ErrorType.VersionRequestedError);

    return isInfoRequest ? 0 : 1;
  }
}
