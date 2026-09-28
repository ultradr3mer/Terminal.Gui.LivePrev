using CommandLine;

namespace TuiPreview.Cli;

/// <summary>
/// Command line contract of the <c>tuipreview</c> tool:
///   --view "path" --class "Namespace.Name" [--interactive | --watch]
/// </summary>
internal sealed class Options
{
  // No short name on --view: -w is already --watch, and -v would read as "verbose".
  [Option("view", Required = true, HelpText = "Path to a .tui.cs file defining the view.")]
  public string? ViewCs { get; set; }

  [Option('c', "class", Required = true, HelpText = "Name of the class to preview.")]
  public string Class { get; set; } = string.Empty;

  [Option("display-name", Required = false, HelpText = "The real file a mirrored copy came from, used only for the window title. Pass it when --view is a scratch copy whose name the user should never see.")]
  public string? DisplayName { get; set; }

  [Option("extra", Required = false, HelpText = "Further .cs files compiled together with --view, against the same reference set (the framework, Terminal.Gui and TuiControls). For layout-only helpers, not the logic half of the view. Space-separated for several.")]
  public IEnumerable<string>? Extra { get; set; }

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

  [Option("width", Required = false, HelpText = "Render width in columns (default 80).")]
  public int Width { get; set; } = 80;

  [Option("height", Required = false, HelpText = "Render height in rows (default 24).")]
  public int Height { get; set; } = 24;
}
