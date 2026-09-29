using Microsoft.CodeAnalysis;
using System;
using System.Collections.Generic;
using System.Text;

namespace TuiPreview.Cli.Preview
{
  internal class DiagnosticsLog
  {
    public DiagnosticsLog()
    {
      (_logSegmentDiagnosticsCount,
            _logSegmentLineCount,
            _logSegmentI) = this.LogBeginSegment(0);

      AppendCompleted = _ => { };
    }

    /// <summary>
    /// Maps the path a diagnostic reports to the file a link should open. The compiled file may
    /// be a scratch copy of an unsaved buffer, and the link has to lead back to the real one.
    /// </summary>
    public Func<string, string> MapSourcePath { get; set; } = path => path;

    /// <summary>
    /// The log as Markdown: one tight list, one item per line. Items are the only blocks that
    /// follow each other without a blank line. Paragraphs need one to stay apart, and the
    /// Markdown view renders it as an empty row.
    /// </summary>
    public string GetFullLog()
    {
      return string.Join("\n", _logLines);
    }

    public string GetDiagnosticsSummary(int maxLength = 40)
    {
      return DiagnosticSummary.Format(_logSegmentLineCount,
                                    _logSegmentDiagnosticsCount,
                                    _lastLogTime,
                                    maxLength);
    }

    public int GetMinutesSinceLast()
    {
      return (int)DiagnosticSummary.GetAge(_lastLogTime).TotalMinutes; // int ~ floor
    }

    public int GetCurrentSegmentI()
    {
      return _logSegmentI;
    }

    public DiagnosticSeverity GetMaxSeverity()
    {
      return _logSegmentDiagnosticsCount[DiagnosticSeverity.Error] > 0 ? DiagnosticSeverity.Error
      : _logSegmentDiagnosticsCount[DiagnosticSeverity.Warning] > 0 ? DiagnosticSeverity.Warning
      : _logSegmentDiagnosticsCount[DiagnosticSeverity.Info] > 0 ? DiagnosticSeverity.Info
      : _logSegmentDiagnosticsCount[DiagnosticSeverity.Hidden] > 0 ? DiagnosticSeverity.Hidden
      : (DiagnosticSeverity)(-1); // Hidden already 0
    }

    public DiagnosticsLog(Action<string> appendCompleted, IEnumerable<Diagnostic> allDiagnostics) : this()
    {
      AppendCompleted = appendCompleted;

      foreach (var d in allDiagnostics)
      {
        this.AppendLog(d);
      }
    }

    private readonly List<string> _logLines = [];
    private int _logSegmentLineCount;
    private Dictionary<DiagnosticSeverity, int> _logSegmentDiagnosticsCount;
    private const int MaxLogLines = 200;
    public Action<string> AppendCompleted;
    private DateTime _lastLogTime;
    private int _logSegmentI;

    /// <summary>
    /// Appends a diagnostic. Its location becomes a link that opens the file at that position
    /// in VS Code.
    /// </summary>
    public void AppendLog(Diagnostic d)
    {
      string where = d.Location.SourceTree is null ? string.Empty : FormatLocation(d.Location) + ": ";

      // Marked up only when it needs attention, so errors stand out in a long run of info
      // lines. LogHighlighter turns strong into red and emphasis into yellow.
      string severity = Escape($"[{d.Severity}]");
      severity = d.Severity switch
      {
        DiagnosticSeverity.Error => $"**{severity}**",
        DiagnosticSeverity.Warning => $"*{severity}*",
        _ => severity,
      };

      _logSegmentDiagnosticsCount[d.Severity]++;
      // Nested under the line before it, which is the rebuild the diagnostic belongs to.
      AppendMarkdown($"{severity} {Escape(d.Id)}: {where}{Escape(d.GetMessage())}");
    }

    /// <summary>Appends one plain-text line, shown as written rather than read as Markdown.</summary>
    public void AppendLog(string line)
    {
      AppendMarkdown(Escape(line));
    }

    /// <summary>
    /// The URL scheme that opens a file in VS Code. Insiders registers its own scheme, and
    /// the terminal the tool runs in says which of the two it belongs to.
    /// </summary>
    public static string EditorScheme { get; } =
        Environment.GetEnvironmentVariable("TERM_PROGRAM_VERSION")?.EndsWith("-insider", StringComparison.Ordinal) == true
            ? "vscode-insiders"
            : "vscode";

    private string FormatLocation(Location location)
    {
      FileLinePositionSpan span = location.GetLineSpan();
      int line = span.StartLinePosition.Line + 1;
      int column = span.StartLinePosition.Character + 1;
      string path = MapSourcePath(span.Path);
      string text = Escape($"{Path.GetFileName(path)}({line},{column})");

      // Generated trees, such as the constructor stub, have a name but no file to open.
      if (!Path.IsPathRooted(path) || !File.Exists(path))
      {
        return text;
      }

      // Only the characters that would end the link destination or change its meaning are
      // encoded. VS Code decodes them again, and the drive colon has to stay as it is.
      string encoded = path.Replace('\\', '/')
          .Replace("%", "%25").Replace(" ", "%20").Replace("#", "%23").Replace("?", "%3F")
          .Replace("(", "%28").Replace(")", "%29").Replace("<", "%3C").Replace(">", "%3E");
      string slash = encoded.StartsWith('/') ? string.Empty : "/";
      return $"[{text}]({EditorScheme}://file{slash}{encoded}:{line}:{column})";
    }

    /// <summary>
    /// Backslash-escapes ASCII punctuation, which CommonMark allows for every such character.
    /// Compiler messages are full of it, and a generic like <c>List&lt;T&gt;</c> would otherwise be
    /// read as an HTML tag and dropped.
    /// </summary>
    private static string Escape(string text)
    {
      var sb = new StringBuilder(text.Length + 8);
      foreach (char c in text)
      {
        if (char.IsAsciiLetterOrDigit(c) || c == ' ' || !char.IsAscii(c) || char.IsControl(c))
        {
          sb.Append(c);
        }
        else
        {
          sb.Append('\\').Append(c);
        }
      }
      return sb.ToString();
    }

    private void AppendMarkdown(string line)
    {
      _logLines.Add(line);
      _logSegmentLineCount++;

      if (_logLines.Count > MaxLogLines)
      {
        _logLines.RemoveRange(0, _logLines.Count - MaxLogLines);
      }

      AppendCompleted?.Invoke(line);

      _lastLogTime = DateTime.UtcNow;
    }
    /// <summary>Lines appended since the current segment began, not counting its header.</summary>
    public int SegmentLineI => _logSegmentLineCount;

    internal (Dictionary<DiagnosticSeverity, int> diagCount, int segLineCount, int segmentIdx) LogBeginSegment(int i)
    {
      _logSegmentDiagnosticsCount = new Dictionary<DiagnosticSeverity, int>()
      {
        {DiagnosticSeverity.Error, 0},
        {DiagnosticSeverity.Warning, 0},
        {DiagnosticSeverity.Info, 0},
        {DiagnosticSeverity.Hidden, 0},
      };
      AppendLog($"> Run start {DateTime.Now:HH:mm:ss}"); 

      // Reset after the header, so the header itself does not count as a new entry.
      _logSegmentLineCount = 0;

      _logSegmentI = i;

      return (_logSegmentDiagnosticsCount, _logSegmentLineCount, _logSegmentI);
    }
  }
}
