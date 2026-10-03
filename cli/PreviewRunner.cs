using CommandLine;
using CommandLine.Text;
using TuiPreview.Cli.Preview;

namespace TuiPreview.Cli;

/// <summary>
/// Parses the command line and runs one preview mode.
/// <para>
/// Never calls <c>Environment.Exit</c>, always returns, and puts back the console state it
/// changes, so a terminal the tool ran in is left the way it was found.
/// </para>
/// </summary>
internal static class PreviewRunner
{
  public static int Run(string[] args)
  {
    // HelpWriter is disabled so that Fail() stays the single place that renders help,
    // which keeps --help and parse errors on one consistent path.
    using var parser = new Parser(settings => settings.HelpWriter = null);

    var result = parser.ParseArguments<Options>(args);
    return result.MapResult(Run, errors => Fail(result, errors));
  }

  private static int Run(Options options)
  {
    var problems = new List<string>();

    CheckFile(options.ViewCs ?? string.Empty, "--view", problems);

    if (problems.Count > 0)
    {
      return Report(problems);
    }

    // Read before validation, because the view may be what supplies --class. Any warning here is
    // a bad annotation, which must not stop a preview that is otherwise fine.
    var settings = ViewSettings.Read(options.ViewCs!);
    foreach (string warning in settings.Warnings)
    {
      Console.Error.WriteLine($"warning: {warning}");
    }

    // Three steps, in order: the command line, then what the view states for itself, then what
    // the view declares. Inference is last so a view never has to be annotated just to be
    // previewable from a bare command line.
    string? className = options.Class;
    if (string.IsNullOrWhiteSpace(className))
    {
      className = settings.Class ?? ViewSettings.InferClassName(options.ViewCs!);
    }

    if (string.IsNullOrWhiteSpace(className))
    {
      problems.Add(
        $"no class to preview: {Path.GetFileName(options.ViewCs!)} states none and declares none. " +
        "Pass --class, or add \"// @tuipreview class=Namespace.Name\" to the view, " +
        "or a \"class\" key in its .tui.json sidecar.");
    }

    // Extras accumulate rather than override: a --extra on the command line names files the view
    // cannot know about, while the view names files it cannot be previewed without. Same for
    // references.
    //
    // Only the command line values are checked here. The view's own are already absolute, having
    // been resolved against the view's directory, so a relative one in an annotation is valid
    // where a relative one on a command line means "relative to the shell's directory".
    var extras = new List<string>(settings.Extra);
    foreach (string extra in options.Extra ?? Enumerable.Empty<string>())
    {
      CheckFile(extra, "--extra", problems);
      extras.Add(extra);
    }

    var references = new List<string>(settings.References);
    foreach (string reference in options.Reference ?? Enumerable.Empty<string>())
    {
      CheckReference(reference, "--reference", problems);
      references.Add(reference);
    }

    if (problems.Count > 0)
    {
      return Report(problems);
    }

    // The file actually compiled. This may be a scratch copy of the real file when the editor is
    // mirroring an unsaved buffer, so the two are tracked separately.
    string source = Path.GetFullPath(options.ViewCs!);

    // Title only. Never the scratch copy, whose name carries a content hash.
    string displayName = options.DisplayName is { Length: > 0 } shown
      ? Path.GetFullPath(shown)
      : source;

    var preview = new ResolvedPreview
    {
      Options = options,
      ClassName = className!,
      Source = source,
      DisplayName = displayName,
      // View settings are already absolute: ViewSettings resolves them against the view, because a
      // relative path written in a view means "relative to that view". Command line values are
      // relative to wherever the user ran the tool from, which is the only sensible reading of a
      // path typed at a prompt. GetFullPath does the right thing in both cases.
      SourceFiles = extras.Select(Path.GetFullPath).ToArray(),
      References = references.Select(Path.GetFullPath).ToArray(),
      Width = options.Width ?? settings.Width ?? 80,
      Height = options.Height ?? settings.Height ?? 24,
      SettingsSource = settings.Source,
    };

    // The rendered screen is box-drawing characters; without this they come out as mojibake.
    var previousEncoding = Console.OutputEncoding;
    Console.OutputEncoding = System.Text.Encoding.UTF8;
    try
    {
      Console.WriteLine($"{"view",-10}: {preview.Source}");
      Console.WriteLine($"{"class",-10}: {preview.ClassName}");

      if (preview.SettingsSource != "none")
      {
        Console.WriteLine($"{"settings",-10}: from the view ({preview.SettingsSource})");
      }

      if (preview.References.Count > 0)
      {
        Console.WriteLine($"{"references",-10}: {preview.References.Count} added to the defaults");
      }

      return RunPreview(preview);
    }
    finally
    {
      Console.OutputEncoding = previousEncoding;
    }
  }

  private static int Report(List<string> problems)
  {
    foreach (var problem in problems)
    {
      Console.Error.WriteLine($"error: {problem}");
    }

    return 1;
  }

  private static int RunPreview(ResolvedPreview preview)
  {
    Options options = preview.Options;
    int width = preview.Width;
    int height = preview.Height;
    string source = preview.Source;
    string displayName = preview.DisplayName;

    // Closed over the resolved values, so a rebuild in watch or interactive mode compiles
    // exactly what the first build did. Re-reading the view's settings here would let a
    // half-saved annotation change the reference set under a running preview.
    Func<RenderPipeline.BuildResult> build =
      () => RenderPipeline.BuildClass(
        source, preview.ClassName, preview.SourceFiles, preview.References);

    if (options.Interactive)
    {
      return InteractiveSession.Run(displayName, source, build);
    }

    if (options.UiSnapshot)
    {
      var ui = new PreviewBuilder(displayName, source, build);
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

  /// <summary>
  /// A reference may be a dll or a directory of them, so both are accepted here. Only a path
  /// that is neither is refused: a typo is worth catching, because otherwise it silently
  /// previews a view that cannot compile.
  /// </summary>
  private static void CheckReference(string value, string option, ICollection<string> problems)
  {
    if (string.IsNullOrWhiteSpace(value))
    {
      problems.Add($"{option} must not be empty.");
      return;
    }

    if (!File.Exists(value) && !Directory.Exists(value))
    {
      problems.Add($"{option} '{value}' is neither a file nor a directory.");
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
