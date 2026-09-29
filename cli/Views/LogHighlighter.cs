using Terminal.Gui.Drawing;
using Attribute = Terminal.Gui.Drawing.Attribute;

namespace TuiPreview.Cli.Preview;

/// <summary>
/// Colours the diagnostics log. Markdown has no syntax for colour, but the Markdown view asks
/// its highlighter for an attribute per style role, so the log gives each role one meaning:
/// strong is an error, emphasis a warning, and a link a file position.
/// </summary>
/// <remarks>
/// The attribute replaces the role's default style instead of adding to it, which is why
/// emphasis can be yellow without also being italic. The view fills in the background.
/// </remarks>
internal sealed class LogHighlighter : ISyntaxHighlighter
{
  // The named 16 colours. The driver sends them as fixed RGB, so a light terminal theme
  // does not adjust them.
  private static readonly Attribute Error = new(new Color(ColorName16.BrightRed), Color.None, TextStyle.Bold);
  private static readonly Attribute Warning = new(new Color(ColorName16.BrightYellow), Color.None, TextStyle.Bold);
  private static readonly Attribute FileLink = new(new Color(ColorName16.BrightCyan), Color.None, TextStyle.Underline);
  private static readonly Attribute Marker = new(new Color(ColorName16.DarkGray), Color.None, TextStyle.None);

  public string ThemeName => "TuiPreview log";

  // No theme background: the log keeps the chrome's own.
  public Color? DefaultBackground => null;

  public Attribute? GetAttributeForScope(MarkdownStyleRole role) => role switch
  {
    MarkdownStyleRole.Strong => Error,
    MarkdownStyleRole.Emphasis => Warning,
    MarkdownStyleRole.Link => FileLink,
    MarkdownStyleRole.ListMarker => Marker,
    _ => null,
  };

  // The log has no code blocks, so there is nothing to tokenise.
  public IReadOnlyList<StyledSegment> Highlight(string code, string? language) =>
      [new StyledSegment(code, MarkdownStyleRole.CodeBlock)];

  public void ResetState()
  {
  }
}
