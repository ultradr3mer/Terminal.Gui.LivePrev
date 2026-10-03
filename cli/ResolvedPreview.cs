namespace TuiPreview.Cli;

/// <summary>
/// Everything one preview needs, resolved once from the command line and the view's own settings.
/// <para>
/// This exists because the values have more than one origin each and are consumed far apart from
/// where they are decided. Passed as positional parameters instead, a caller has to remember
/// that <c>className</c> precedes the file list and that the two sizes are nullable, and adding a
/// fifth value means changing every signature between the resolution and the render.
/// </para>
/// <para>
/// Fully resolved: a null here would mean "still to be decided", which is the ambiguity this
/// type is here to remove.
/// </para>
/// </summary>
internal sealed record ResolvedPreview
{
  /// <summary>The command line as parsed, for the mode flags that are not resolved away.</summary>
  public required Options Options { get; init; }

  /// <summary>Fully qualified name of the class to instantiate.</summary>
  public required string ClassName { get; init; }

  /// <summary>
  /// The .tui.cs actually compiled, as an absolute path. This is the scratch copy when the
  /// editor is mirroring an unsaved buffer, so it is deliberately not the display name.
  /// </summary>
  public required string Source { get; init; }

  /// <summary>The real file's name, for the window title. Never the scratch copy.</summary>
  public required string DisplayName { get; init; }

  /// <summary>Further .cs files compiled into the same assembly as the view.</summary>
  public required IReadOnlyList<string> SourceFiles { get; init; }

  /// <summary>Assemblies added to the default reference set, as absolute paths.</summary>
  public required IReadOnlyList<string> References { get; init; }

  /// <summary>Render width in columns. Interactive mode fills the terminal and ignores this.</summary>
  public required int Width { get; init; }

  /// <summary>Render height in rows. Interactive mode fills the terminal and ignores this.</summary>
  public required int Height { get; init; }

  /// <summary>
  /// Problems with the view's own settings, already printed to stderr. Carried here as well
  /// because the interactive preview takes over the screen and would wipe them, so it repeats
  /// them in its log pane.
  /// </summary>
  public required IReadOnlyList<string> Warnings { get; init; }

  /// <summary>Which source supplied the view's settings, for the log line. "none" when it stated nothing.</summary>
  public required string SettingsSource { get; init; }
}