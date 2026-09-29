using System.Text;
using Terminal.Gui.Text;

namespace TuiPreview.Cli.Preview;

/// <summary>
/// One display row of the log: the chars <see cref="Start"/> to <see cref="Start"/> +
/// <see cref="Length"/> of log line <see cref="Line"/>. A long line takes several rows, and
/// every row after its first is indented.
/// </summary>
internal sealed record LogRow(int Line, int Start, int Length, bool Continuation)
{
  public const string Indent = "  ";

  public string Text(LogLine line) => (Continuation ? Indent : string.Empty) + line.Text.Substring(Start, Length);

  /// <summary>
  /// The char index in the log line at <paramref name="column"/> of this row, or -1 for the
  /// indent and the space past the end.
  /// </summary>
  public int CharAt(LogLine line, int column)
  {
    int x = Continuation ? Indent.GetColumns() : 0;
    int index = Start;

    foreach (Rune rune in line.Text.AsSpan(Start, Length).EnumerateRunes())
    {
      int width = Math.Max(rune.GetColumns(), 1);
      if (column >= x && column < x + width)
      {
        return index;
      }

      x += width;
      index += rune.Utf16SequenceLength;
    }

    return -1;
  }
}

/// <summary>
/// Word-wraps the log to a width. <c>Code</c> cannot wrap, and a log pane that scrolls
/// sideways hides the end of every long message.
/// </summary>
internal static class LogWrap
{
  public static List<LogRow> Wrap(IReadOnlyList<LogLine> lines, int width)
  {
    List<LogRow> rows = [];
    for (int i = 0; i < lines.Count; i++)
    {
      WrapLine(rows, i, lines[i].Text, width);
    }

    return rows;
  }

  private static void WrapLine(List<LogRow> rows, int line, string text, int width)
  {
    int start = 0;
    bool continuation = false;

    do
    {
      int room = Math.Max(1, width - (continuation ? LogRow.Indent.GetColumns() : 0));
      int end = FitEnd(text, start, room);

      // Break after the last space that fits, unless that would leave the row empty; a word
      // longer than the row is cut instead.
      if (end < text.Length)
      {
        int space = text.LastIndexOf(' ', end - 1, end - start);
        if (space > start)
        {
          end = space + 1;
        }
      }

      rows.Add(new LogRow(line, start, end - start, continuation));
      start = end;

      // A continuation row starts at the next word, not at the spaces before it.
      while (start < text.Length && text[start] == ' ')
      {
        start++;
      }

      continuation = true;
    }
    while (start < text.Length);
  }

  /// <summary>The end of the longest run from <paramref name="start"/> that fits in <paramref name="room"/> columns, at least one rune.</summary>
  private static int FitEnd(string text, int start, int room)
  {
    int columns = 0;
    int end = start;

    foreach (Rune rune in text.AsSpan(start).EnumerateRunes())
    {
      columns += Math.Max(rune.GetColumns(), 1);
      if (columns > room && end > start)
      {
        break;
      }

      end += rune.Utf16SequenceLength;
    }

    return end;
  }
}
