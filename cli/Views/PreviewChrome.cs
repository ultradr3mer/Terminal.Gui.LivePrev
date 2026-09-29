using Microsoft.CodeAnalysis;
using Terminal.Gui.Drawing;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;
using TuiControls;

namespace TuiPreview.Cli.Preview;

/// <summary>
/// The logic half of the preview root: the diagnostics log, the log pane that grows and
/// collapses with it, and the rebuild and quit wiring. The layout is in PreviewChrome.tui.cs.
/// </summary>
internal sealed partial class PreviewChrome
{
  private readonly DiagnosticsLog _logWriter;
  private bool _logAutoCollapse = true;
  private readonly LogHighlighter _logHighlighter;
  private bool _scrollLogToEnd;

  // The display rows of the log, wrapped to _wrapWidth.
  private List<LogRow> _logRows = [];
  private int _wrapWidth = -1;

  // The row a press in the log started on, and whether the mouse has left it since.
  private int? _selectAnchor;
  private bool _selectDragged;

  public PreviewChrome()
  {
    InitializeComponent();

    // Set only once: reassigning FixedSize would discard a size the user dragged.
    Splitter.FixedSize = Dim.Func(_ => CalcLogPaneHeight());

    _logWriter = new DiagnosticsLog
    {
      AppendCompleted = OnLogAppendComplete
    };
    _logHighlighter = new LogHighlighter(_logWriter, () => _log.GetAttributeForRole(VisualRole.Normal));

    WireInput();
    WireLog();

    // The builder sets Title after construction.
    TitleChanged += (_, _) => _titleLabel.Text = Title;

    UpdateStatus();
  }

  public event Action? RebuildRequested;

  public event Action? QuitRequested;

  /// <summary>
  /// Maps the path a diagnostic reports to the file its link opens. See
  /// <see cref="DiagnosticsLog.MapSourcePath"/>.
  /// </summary>
  internal Func<string, string> MapSourcePath
  {
    get => _logWriter.MapSourcePath;
    set => _logWriter.MapSourcePath = value;
  }

  private void OnLogAppendComplete(string line)
  {
    // Line indexes would point at other lines once old ones are dropped.
    _logHighlighter.HoveredLine = null;
    _logHighlighter.Selection = null;
    _selectAnchor = null;
    RewrapLog();

    // Before the first layout the viewport has no height yet, so the scroll to the newest
    // line waits for the next layout pass.
    _scrollLogToEnd = true;
    _log.SetNeedsLayout();

    UpdateStatus();
  }

  private void WireLog()
  {
    // Set here, not in the layout half: a previewed .tui.cs may only use the framework,
    // Terminal.Gui and TuiControls, and the highlighter is part of the tool.
    _log.SyntaxHighlighter = _logHighlighter;

    // Code clears its viewport with the code background. The log should look like the rest
    // of the chrome, and the highlighter uses the Normal background for the text as well.
    _log.GettingAttributeForRole += (_, e) =>
    {
      if (e.Role == VisualRole.Code)
      {
        e.Result = _log.GetAttributeForRole(VisualRole.Normal);
        e.Handled = true;
      }
    };

    _log.SubViewsLaidOut += (_, _) =>
    {
      // Wrapped for the width the log had, which changes with the terminal and with the
      // vertical scroll bar coming and going.
      if (_log.Viewport.Width > 0 && _log.Viewport.Width != _wrapWidth)
      {
        RewrapLog();
        _scrollLogToEnd = true;
      }

      if (!_scrollLogToEnd)
      {
        return;
      }

      _scrollLogToEnd = false;
      int bottom = Math.Max(0, _log.GetContentSize().Height - _log.Viewport.Height);
      _log.Viewport = _log.Viewport with { Y = bottom };
    };

    _log.MouseEvent += (_, mouse) => OnLogMouse(mouse);

    _log.MouseLeave += (_, _) => SetHoveredLine(null);

    // Link.OpenUrl refuses every scheme outside this list. The log is the only content that
    // makes editor links, and it makes them only for files that exist.
    Link.SafeSchemes.Add(DiagnosticsLog.EditorScheme);
  }

  /// <summary>
  /// Wraps the log to the current width and shows the rows. Before the first layout there is
  /// no width yet; the lines stay unwrapped until the layout pass wraps them.
  /// </summary>
  private void RewrapLog()
  {
    _wrapWidth = _log.Viewport.Width;
    IReadOnlyList<LogLine> lines = _logWriter.Lines;
    _logRows = LogWrap.Wrap(lines, _wrapWidth > 0 ? _wrapWidth : int.MaxValue);
    _logHighlighter.Rows = _logRows;
    _log.Text = string.Join("\n", _logRows.Select(r => r.Text(lines[r.Line])));
  }

  /// <summary>The link at a content position of the log, if there is one.</summary>
  private LogLink? LinkAt(int row, int column)
  {
    if (row < 0 || row >= _logRows.Count || _logWriter.Lines[_logRows[row].Line] is not { Link: { } link } line)
    {
      return null;
    }

    return link.Contains(_logRows[row].CharAt(line, column)) ? link : null;
  }

  /// <summary>
  /// Mouse handling for the log. A click on a link opens the file. Otherwise pressing and
  /// dragging marks whole log lines, and releasing copies them unwrapped; a click without a
  /// drag copies the line under the mouse. Lines rather than characters because a log is read
  /// and pasted by line. For characters, the terminal's own selection (Shift+drag) still works.
  /// </summary>
  private void OnLogMouse(Mouse mouse)
  {
    if (mouse.Position is not { } pos)
    {
      return;
    }

    int row = _log.Viewport.Y + pos.Y;
    LogLink? link = LinkAt(row, _log.Viewport.X + pos.X);
    int lastRow = _logRows.Count - 1;

    if (mouse.Flags == MouseFlags.LeftButtonPressed)
    {
      if (row < 0 || row > lastRow)
      {
        return;
      }

      int index = _logRows[row].Line;

      // Grabbed, so the drag keeps arriving here when it leaves the log.
      _log.App?.Mouse.GrabMouse(_log);
      _selectAnchor = index;
      _selectDragged = false;
      SetSelection(link is null ? (index, index) : null);
      mouse.Handled = true;
      return;
    }

    if (mouse.Flags == (MouseFlags.LeftButtonPressed | MouseFlags.PositionReport) && _selectAnchor is { } anchor)
    {
      // Past the top or bottom edge, scroll one row, so a selection can reach beyond view.
      if (pos.Y < 0 || pos.Y >= _log.Viewport.Height)
      {
        int y = Math.Clamp(_log.Viewport.Y + Math.Sign(pos.Y), 0, Math.Max(0, lastRow + 1 - _log.Viewport.Height));
        _log.Viewport = _log.Viewport with { Y = y };
      }

      int end = _logRows[Math.Clamp(row, 0, lastRow)].Line;
      _selectDragged |= end != anchor;
      SetSelection((Math.Min(anchor, end), Math.Max(anchor, end)));
      mouse.Handled = true;
      return;
    }

    // Released even without an anchor: new log text drops the anchor during a drag.
    if (mouse.Flags.HasFlag(MouseFlags.LeftButtonReleased) && _log.App?.Mouse.IsGrabbed(_log) == true)
    {
      _log.App.Mouse.UngrabMouse();
    }

    if (mouse.Flags.HasFlag(MouseFlags.LeftButtonReleased) && _selectAnchor is not null)
    {
      _selectAnchor = null;

      // A plain click on a link is left to the click event that follows, which opens it.
      if (_logHighlighter.Selection is { } rows)
      {
        CopyRows(rows.Start, rows.End);
      }

      mouse.Handled = true;
      return;
    }

    if (mouse.Flags.HasFlag(MouseFlags.LeftButtonClicked))
    {
      // After a drag the click belongs to the selection, even when it ends on a link.
      if (link is not null && !_selectDragged)
      {
        Link.OpenUrl(DiagnosticsLog.EditorUrl(link));
      }

      mouse.Handled = true;
      return;
    }

    if (mouse.Flags == MouseFlags.PositionReport)
    {
      SetHoveredLine(link is null ? null : _logRows[row].Line);
    }
  }

  private void CopyRows(int start, int end)
  {
    string text = string.Join(Environment.NewLine,
        _logWriter.Lines.Skip(start).Take(end - start + 1).Select(l => l.Text));
    int count = end - start + 1;

    NewEntryInfo.Text = _log.App?.Clipboard?.TrySetClipboardData(text) == true
        ? $"copied {count} {(count == 1 ? "row" : "rows")}"
        : "could not copy to the clipboard";
  }

  private void SetSelection((int Start, int End)? rows)
  {
    if (_logHighlighter.Selection == rows)
    {
      return;
    }

    _logHighlighter.Selection = rows;
    RestyleLog();
  }

  private void SetHoveredLine(int? index)
  {
    if (_logHighlighter.HoveredLine == index)
    {
      return;
    }

    _logHighlighter.HoveredLine = index;
    RestyleLog();
  }

  private void RestyleLog()
  {
    // Code highlights only when its text, language or highlighter changes. Setting the same
    // highlighter again would be ignored, so it is removed and put back.
    _log.SyntaxHighlighter = null;
    _log.SyntaxHighlighter = _logHighlighter;
  }

  private void WireInput()
  {
    RebuildButton.Action += () => RebuildRequested?.Invoke();
    CollapseButton.Action += () => Splitter.Collapse(SplitterPane.First);
    CollapseAutoButton.ValueChanged += (o, a) => _logAutoCollapse = a.NewValue == CheckState.Checked;
    FullButton.Action += () => Splitter.Collapse(SplitterPane.Second);
    QuitButton.Action += () => QuitRequested?.Invoke();

    AddCommand(Command.Quit, () =>
    {
      QuitRequested?.Invoke();
      return true;
    });

    AddCommand(Command.Accept, () =>
    {
      RebuildRequested?.Invoke();
      return true;
    });
  }

  internal void AppendLog(string v, LogKind kind = LogKind.Normal)
  {
    _logWriter.AppendLog(v, kind);
  }

  internal void AppendLog(Diagnostic d)
  {
    _logWriter.AppendLog(d);
  }

  /// <summary>Starts a new segment, so the counts describe this rebuild only.</summary>
  internal void BeginLogSegment()
  {
    _logWriter.LogBeginSegment(_logWriter.SegmentLineI + 1);
  }

  /// <summary>
  /// Grows with the current segment's lines up to a share of the splitter. A user drag
  /// replaces this with an absolute height.
  /// </summary>
  private int CalcLogPaneHeight(int maxLogPanePercent = 30, int minLogPaneHeight = 3)
  {
    int max = Math.Max(minLogPaneHeight, Splitter.Viewport.Height * maxLogPanePercent / 100);
    int wanted = CurrentSegmentRows() + 1 /* divider */;
    return Math.Clamp(wanted, minLogPaneHeight, max);
  }

  /// <summary>
  /// The display rows of the current segment, its header included. Rows rather than lines,
  /// so a wrapped message still fits. The rows depend only on the width, so sizing the pane
  /// by them does not feed back into the wrap.
  /// </summary>
  private int CurrentSegmentRows()
  {
    if (_logWriter is null)
    {
      return 1;
    }

    int firstLine = _logWriter.Lines.Count - (_logWriter.SegmentLineI + 1 /* segment header */);
    return _logRows.Count(r => r.Line >= firstLine);
  }

  private void UpdateStatus()
  {
    string summary = _logWriter.GetDiagnosticsSummary();
    NewEntryInfo.Text = summary;
    string summaryShort = _logWriter.GetDiagnosticsSummary(20);
    Splitter.FirstCollapsedText = $"Show Log - {summaryShort}";

    string summaryExtra = _logWriter.GetDiagnosticsSummary(10);
    Splitter.SecondCollapsedText = $"Show Preview - {summaryShort}";

    if (_logWriter.GetCurrentSegmentI() == 0)
      return; // Leave Log as initialized

    if (_logWriter.GetMaxSeverity() == DiagnosticSeverity.Error)
    {
      this.Splitter.Expand(_logWriter.SegmentLineI);
      Splitter.First.Height = CalcLogPaneHeight();
    }
    else if (_logAutoCollapse && _logWriter.GetMaxSeverity() < DiagnosticSeverity.Warning)
    {
      this.Splitter.Collapse(SplitterPane.First);
    }

    if (this.IsInitialized)
    {
      Splitter.SetNeedsLayout();
    }
  }

  internal int CheckMinuteEvent(int lastMinutes)
  {
    int curMinutes = _logWriter.GetMinutesSinceLast();
    if (curMinutes == lastMinutes)
      return lastMinutes;

    this.UpdateStatus();
    return curMinutes;
  }
}
