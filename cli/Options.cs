using CommandLine;

namespace TuiPreview.Cli;

/// <summary>
/// Command line contract of the <c>tuipreview</c> tool:
///   --view "path" [--class "Namespace.Name"] [--interactive | --watch]
/// <para>
/// <c>--class</c> is optional because a view may declare it itself, in a <c>// @tuipreview</c>
/// annotation or a sibling <c>.tui.json</c>. A command line that repeats what the view already
/// says is a second place for the two to drift apart.
/// </para>
/// </summary>
internal sealed class Options
{
  // No short name on --view: -w is already --watch, and -v would read as "verbose".
  [Option("view", Required = true, HelpText = "Path to a .tui.cs file defining the view.")]
  public string? ViewCs { get; set; }

  [Option('c', "class", Required = false, HelpText = "Name of the class to preview. Optional: a // @tuipreview annotation or a sibling .tui.json in the view may state it. Overrides either.")]
  public string? Class { get; set; }

  [Option("display-name", Required = false, HelpText = "The real file a mirrored copy came from, used only for the window title. Pass it when --view is a scratch copy whose name the user should never see.")]
  public string? DisplayName { get; set; }

  [Option("extra", Required = false, HelpText = "Further .cs files compiled together with --view, against the same reference set (the framework, Terminal.Gui and TuiControls). For layout-only helpers, not the logic half of the view. Space-separated for several.")]
  public IEnumerable<string>? Extra { get; set; }

  [Option("reference", Required = false, HelpText = "Assemblies added to the default reference set, for a view that uses a control library beyond Terminal.Gui and TuiControls. A dll, or a directory holding one. Space-separated for several. Additive: the defaults are always included.")]
  public IEnumerable<string>? Reference { get; set; }

  [Option("project", Required = false, HelpText = "Projects whose build output is added to the reference set, for a view that uses a control library from the same solution. The project is not built: its output must exist. Space-separated for several. Additive, like --reference.")]
  public IEnumerable<string>? Project { get; set; }

  // Accepted for older command lines; rendering is the default anyway.
  [Option('r', "render", Required = false, HelpText = "Compile and render the view to characters in this terminal. This is the default.")]
  public bool Render { get; set; }

  [Option('w', "watch", Required = false, HelpText = "With --render, re-render whenever the view file changes.")]
  public bool Watch { get; set; }

  [Option('i', "interactive", Required = false, HelpText = "Live TUI preview: log pane on top, the real view below. Ctrl+C quits.")]
  public bool Interactive { get; set; }

  [Option("ui-snapshot", Required = false, Hidden = true, HelpText = "Developer test seam: render the interactive window to characters and exit.")]
  public bool UiSnapshot { get; set; }

  [Option("ui-flood", Required = false, Hidden = true, HelpText = "Developer test seam with --ui-snapshot: append N log lines to exercise scrolling.")]
  public int UiFlood { get; set; }

  // Nullable so that "not passed on the command line" is distinguishable from an explicit value.
  // With a plain int default of 80/24 the tool could not tell a default apart from a request, and
  // a view's own width would never take effect.
  [Option("width", Required = false, HelpText = "Render width in columns (default 80, or the view's own width when it states one).")]
  public int? Width { get; set; }

  [Option("height", Required = false, HelpText = "Render height in rows (default 24, or the view's own height when it states one).")]
  public int? Height { get; set; }
}
