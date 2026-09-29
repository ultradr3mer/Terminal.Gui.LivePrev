using Microsoft.CodeAnalysis;

namespace TuiPreview.Cli.Preview
{
  /// <summary>What a log line is, which decides its colour.</summary>
  internal enum LogKind
  {
    Normal,
    Meta,
    Success,
    Error,
    Warning,
    Info,
  }

  /// <summary>
  /// A file position within a log line. <see cref="Start"/> and <see cref="Length"/> count
  /// chars; <see cref="LogRow.CharAt"/> maps a mouse position to a char.
  /// </summary>
  internal sealed record LogLink(int Start, int Length, string Path, int Line, int Column)
  {
    public bool Contains(int index) => index >= Start && index < Start + Length;
  }

  /// <summary>
  /// One log line. <see cref="TagLength"/> is how many leading chars (the severity tag and the
  /// diagnostic ID) take the colour of <see cref="Kind"/>; 0 colours the whole line.
  /// </summary>
  internal sealed record LogLine(string Text, LogKind Kind, int TagLength = 0, LogLink? Link = null);

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

    /// <summary>The lines in display order, for colouring them and for finding links.</summary>
    public IReadOnlyList<LogLine> Lines => _logLines;

    /// <summary>The log as plain text, one row per line.</summary>
    public string GetFullLog()
    {
      return string.Join("\n", _logLines.Select(l => l.Text));
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

    private readonly List<LogLine> _logLines = [];
    private int _logSegmentLineCount;
    private Dictionary<DiagnosticSeverity, int> _logSegmentDiagnosticsCount;
    private const int MaxLogLines = 200;
    public Action<string> AppendCompleted;
    private DateTime _lastLogTime;
    private int _logSegmentI;

    /// <summary>
    /// Appends a diagnostic. Its location is recorded as a link that opens the file at that
    /// position in VS Code.
    /// </summary>
    public void AppendLog(Diagnostic d)
    {
      string tag = $"[{d.Severity}] {d.Id}:";
      string text = tag + " ";
      LogLink? link = null;

      if (d.Location.SourceTree is not null)
      {
        FileLinePositionSpan span = d.Location.GetLineSpan();
        int line = span.StartLinePosition.Line + 1;
        int column = span.StartLinePosition.Character + 1;
        string path = MapSourcePath(span.Path);
        string where = $"{Path.GetFileName(path)}({line},{column})";

        // Generated trees, such as the constructor stub, have a name but no file to open.
        if (Path.IsPathRooted(path) && File.Exists(path))
        {
          link = new LogLink(text.Length, where.Length, path, line, column);
        }

        text += where + ": ";
      }

      LogKind kind = d.Severity switch
      {
        DiagnosticSeverity.Error => LogKind.Error,
        DiagnosticSeverity.Warning => LogKind.Warning,
        _ => LogKind.Info,
      };

      _logSegmentDiagnosticsCount[d.Severity]++;
      Append(new LogLine(text + d.GetMessage(), kind, tag.Length, link));
    }

    /// <summary>Appends one line, coloured as a whole by <paramref name="kind"/>.</summary>
    public void AppendLog(string line, LogKind kind = LogKind.Normal)
    {
      Append(new LogLine(line, kind));
    }

    /// <summary>
    /// The URL scheme that opens a file in VS Code. Insiders registers its own scheme, and
    /// the terminal the tool runs in says which of the two it belongs to.
    /// </summary>
    public static string EditorScheme { get; } =
        Environment.GetEnvironmentVariable("TERM_PROGRAM_VERSION")?.EndsWith("-insider", StringComparison.Ordinal) == true
            ? "vscode-insiders"
            : "vscode";

    /// <summary>The <c>vscode://file/…:line:column</c> URL that opens <paramref name="link"/>.</summary>
    public static string EditorUrl(LogLink link)
    {
      // Only the characters that would change the URL's meaning are encoded. VS Code decodes
      // them again, and the drive colon has to stay as it is.
      string encoded = link.Path.Replace('\\', '/')
          .Replace("%", "%25").Replace(" ", "%20").Replace("#", "%23").Replace("?", "%3F");
      string slash = encoded.StartsWith('/') ? string.Empty : "/";
      return $"{EditorScheme}://file{slash}{encoded}:{link.Line}:{link.Column}";
    }

    private void Append(LogLine line)
    {
      _logLines.Add(line);
      _logSegmentLineCount++;

      if (_logLines.Count > MaxLogLines)
      {
        _logLines.RemoveRange(0, _logLines.Count - MaxLogLines);
      }

      AppendCompleted?.Invoke(line.Text);

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
      AppendLog($"> Run start {DateTime.Now:HH:mm:ss}", LogKind.Meta);

      // Reset after the header, so the header itself does not count as a new entry.
      _logSegmentLineCount = 0;

      _logSegmentI = i;

      return (_logSegmentDiagnosticsCount, _logSegmentLineCount, _logSegmentI);
    }
  }
}
