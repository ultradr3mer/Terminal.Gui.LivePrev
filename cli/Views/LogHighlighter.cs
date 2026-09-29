using Terminal.Gui.Drawing;
using Attribute = Terminal.Gui.Drawing.Attribute;

namespace TuiPreview.Cli.Preview;

/// <summary>
/// Colours the diagnostics log in the <c>Code</c> view, using the colours PowerShell and
/// <c>dotnet build</c> get in VS Code's default dark terminal palette.
/// </summary>
/// <remarks>
/// <para>
/// <c>Code</c> hands over only the text of a row, but the log knows more: the kind of each
/// line and where its link is. The highlighter relies on how <c>Code</c> calls it: on every
/// change, <see cref="ResetState"/> first, then <see cref="Highlight"/> once per row in order.
/// It counts along and reads the matching <see cref="LogRow"/> and its <see cref="LogLine"/>.
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

  /// <summary>The log line whose link is under the mouse, drawn brighter.</summary>
  public int? HoveredLine { get; set; }

  /// <summary>The marked log lines, inclusive, drawn in reverse video on all their rows.</summary>
  public (int Start, int End)? Selection { get; set; }

  /// <summary>The display rows <c>Code</c> shows, one per line of its text.</summary>
  public IReadOnlyList<LogRow> Rows { get; set; } = [];

  public string ThemeName => "TuiPreview log";

  // No theme background: the log keeps the chrome's own.
  public Color? DefaultBackground => null;

  public void ResetState() => _next = 0;

  public IReadOnlyList<StyledSegment> Highlight(string code, string? language)
  {
    int index = _next++;
    Attribute plain = normal();

    // Out of step with Code, which should not happen. Plain text is still correct text.
    if (index >= Rows.Count || Rows[index] is not { } row || row.Line >= log.Lines.Count
        || row.Text(log.Lines[row.Line]) != code)
    {
      return [Segment(code, plain)];
    }

    LogLine line = log.Lines[row.Line];
    List<StyledSegment> segments = row.Continuation ? [Segment(LogRow.Indent, plain)] : [];

    // The parts are coloured for the whole line and cut to the chars this row shows.
    int from = row.Start;
    int to = row.Start + row.Length;
    foreach ((int start, int end, Attribute attribute) in Parts(line, row.Line, plain))
    {
      int a = Math.Max(start, from);
      int b = Math.Min(end, to);
      if (a < b)
      {
        segments.Add(Segment(line.Text[a..b], attribute));
      }
    }

    // Reverse video keeps each part's colour recognisable inside the selection.
    if (Selection is { } rows && row.Line >= rows.Start && row.Line <= rows.End)
    {
      return segments
          .Select(s => Segment(s.Text, s.Attribute!.Value with { Style = s.Attribute.Value.Style | TextStyle.Reverse }))
          .ToList();
    }

    return segments;
  }

  /// <summary>The coloured parts of a whole log line, as char ranges, end exclusive.</summary>
  private IEnumerable<(int Start, int End, Attribute Attribute)> Parts(LogLine line, int index, Attribute plain)
  {
    Attribute kind = line.Kind switch
    {
      LogKind.Error => plain with { Foreground = ErrorColor, Style = TextStyle.Bold },
      LogKind.Warning => plain with { Foreground = WarningColor },
      LogKind.Success => plain with { Foreground = SuccessColor },
      LogKind.Meta => plain with { Foreground = MetaColor },
      _ => plain,
    };

    // Lines without a tag take their colour as a whole, like dotnet's "succeeded".
    int length = line.Text.Length;
    int tagEnd = line.TagLength > 0 ? line.TagLength : length;
    yield return (0, tagEnd, kind);

    if (line.Link is not { } link)
    {
      yield return (tagEnd, length, plain);
      yield break;
    }

    bool hovered = HoveredLine == index;
    Attribute linkAttr = plain with
    {
      Foreground = hovered ? LinkHoverColor : LinkColor,
      Style = hovered ? TextStyle.Underline | TextStyle.Bold : TextStyle.Underline,
    };

    yield return (tagEnd, link.Start, plain);
    yield return (link.Start, link.Start + link.Length, linkAttr);
    yield return (link.Start + link.Length, length, plain);
  }

  // Attributes carry no URL: Code has no links of its own, the chrome maps clicks instead.
  private static StyledSegment Segment(string text, Attribute attribute) =>
      new(text, MarkdownStyleRole.CodeBlock, attribute: attribute);

  public Attribute? GetAttributeForScope(MarkdownStyleRole role) => null;
}
