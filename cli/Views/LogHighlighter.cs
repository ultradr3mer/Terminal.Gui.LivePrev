using Terminal.Gui.Drawing;
using Attribute = Terminal.Gui.Drawing.Attribute;

namespace TuiPreview.Cli.Preview;

/// <summary>
/// Colours the diagnostics log in the <c>Code</c> view, using the colours PowerShell and
/// <c>dotnet build</c> get in VS Code's default dark terminal palette.
/// </summary>
/// <remarks>
/// <para>
/// <c>Code</c> hands over only the text of a line, but the log knows more: the kind of each
/// line and where its link is. The highlighter relies on how <c>Code</c> calls it: on every
/// change, <see cref="ResetState"/> first, then <see cref="Highlight"/> once per line in order.
/// It counts along and reads the matching <see cref="LogLine"/>.
/// </para>
/// <para>
/// Every segment carries an explicit attribute. <c>Code</c> draws those as they are, so the
/// background is the log view's own and not the code background.
/// </para>
/// </remarks>
internal sealed class LogHighlighter(DiagnosticsLog log, Func<Attribute> normal) : ISyntaxHighlighter
{
  // The driver sends colours as fixed RGB, so a light terminal theme does not adjust them.
  private static readonly Color ErrorColor = new(0xF1, 0x4C, 0x4C);     // ansiBrightRed
  private static readonly Color WarningColor = new(0xE5, 0xE5, 0x10);   // ansiYellow
  private static readonly Color SuccessColor = new(0x0D, 0xBC, 0x79);   // ansiGreen
  private static readonly Color MetaColor = new(0x66, 0x66, 0x66);      // ansiBrightBlack
  private static readonly Color LinkColor = new(0x11, 0xA8, 0xCD);      // ansiCyan
  private static readonly Color LinkHoverColor = new(0x29, 0xB8, 0xDB); // ansiBrightCyan

  private int _next;

  /// <summary>The line whose link is under the mouse, drawn brighter.</summary>
  public int? HoveredLine { get; set; }

  public string ThemeName => "TuiPreview log";

  // No theme background: the log keeps the chrome's own.
  public Color? DefaultBackground => null;

  public void ResetState() => _next = 0;

  public IReadOnlyList<StyledSegment> Highlight(string code, string? language)
  {
    int index = _next++;
    Attribute plain = normal();

    // Out of step with Code, which should not happen. Plain text is still correct text.
    if (index >= log.Lines.Count || log.Lines[index].Text != code)
    {
      return [Segment(code, plain)];
    }

    LogLine line = log.Lines[index];
    Attribute kind = line.Kind switch
    {
      LogKind.Error => plain with { Foreground = ErrorColor, Style = TextStyle.Bold },
      LogKind.Warning => plain with { Foreground = WarningColor },
      LogKind.Success => plain with { Foreground = SuccessColor },
      LogKind.Meta => plain with { Foreground = MetaColor },
      _ => plain,
    };

    // Lines without a tag take their colour as a whole, like dotnet's "succeeded".
    int tagEnd = line.TagLength > 0 ? line.TagLength : code.Length;
    List<StyledSegment> segments = [Segment(code[..tagEnd], kind)];

    if (line.Link is { } link)
    {
      bool hovered = HoveredLine == index;
      Attribute linkAttr = plain with
      {
        Foreground = hovered ? LinkHoverColor : LinkColor,
        Style = hovered ? TextStyle.Underline | TextStyle.Bold : TextStyle.Underline,
      };

      segments.Add(Segment(code[tagEnd..link.Start], plain));
      segments.Add(Segment(code.Substring(link.Start, link.Length), linkAttr));
      segments.Add(Segment(code[(link.Start + link.Length)..], plain));
    }
    else
    {
      segments.Add(Segment(code[tagEnd..], plain));
    }

    return segments;
  }

  // Attributes carry no URL: Code has no links of its own, the chrome maps clicks instead.
  private static StyledSegment Segment(string text, Attribute attribute) =>
      new(text, MarkdownStyleRole.CodeBlock, attribute: attribute);

  public Attribute? GetAttributeForScope(MarkdownStyleRole role) => null;
}
