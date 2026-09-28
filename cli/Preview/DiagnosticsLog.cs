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

    public string GetFullLog()
    {
      return string.Join(Environment.NewLine, _logLines);
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

    /// <summary>Appends one timestamped line to the log and scrolls it into view.</summary>
    public void AppendLog(Diagnostic d)
    {
      FileLinePositionSpan span = d.Location.GetLineSpan();
      string where = d.Location.SourceTree is null
          ? string.Empty
          : $"{Path.GetFileName(d.Location.SourceTree.FilePath)}" +
            $"({span.StartLinePosition.Line + 1},{span.StartLinePosition.Character + 1}): ";

      _logSegmentDiagnosticsCount[d.Severity]++;
      AppendLog($"  [{d.Severity}] {d.Id}: {where}{d.GetMessage()}");
    }

    /// <summary>Appends one timestamped line to the log and scrolls it into view.</summary>
    public void AppendLog(string line)
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
      AppendLog($"Run start {DateTime.Now:HH:mm:ss}");

      // Reset after the header, so the header itself does not count as a new entry.
      _logSegmentLineCount = 0;

      _logSegmentI = i;

      return (_logSegmentDiagnosticsCount, _logSegmentLineCount, _logSegmentI);
    }
  }
}
