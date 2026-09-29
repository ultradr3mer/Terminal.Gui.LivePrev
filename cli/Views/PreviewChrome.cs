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
    // The hovered line index would point at another line once old ones are dropped.
    _logHighlighter.HoveredLine = null;
    _log.Text = _logWriter.GetFullLog();

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
      if (!_scrollLogToEnd)
      {
        return;
      }

      _scrollLogToEnd = false;
      int bottom = Math.Max(0, _log.GetContentSize().Height - _log.Viewport.Height);
      _log.Viewport = _log.Viewport with { Y = bottom };
    };

    _log.MouseEvent += (_, mouse) =>
    {
      if (mouse.Position is not { } pos)
      {
        return;
      }

      int index = _log.Viewport.Y + pos.Y;
      LogLink? link = LinkAt(index, _log.Viewport.X + pos.X);

      if (mouse.Flags.HasFlag(MouseFlags.LeftButtonClicked) && link is not null)
      {
        Link.OpenUrl(DiagnosticsLog.EditorUrl(link));
        mouse.Handled = true;
        return;
      }

      if (mouse.Flags.HasFlag(MouseFlags.PositionReport))
      {
        SetHoveredLine(link is null ? null : index);
      }
    };

    _log.MouseLeave += (_, _) => SetHoveredLine(null);

    // Link.OpenUrl refuses every scheme outside this list. The log is the only content that
    // makes editor links, and it makes them only for files that exist.
    Link.SafeSchemes.Add(DiagnosticsLog.EditorScheme);
  }

  /// <summary>The link at a content position of the log, if there is one.</summary>
  private LogLink? LinkAt(int index, int column)
  {
    if (index < 0 || index >= _logWriter.Lines.Count || _logWriter.Lines[index] is not { Link: { } link } line)
    {
      return null;
    }

    (int start, int end) = link.Columns(line.Text);
    return column >= start && column < end ? link : null;
  }

  private void SetHoveredLine(int? index)
  {
    if (_logHighlighter.HoveredLine == index)
    {
      return;
    }

    _logHighlighter.HoveredLine = index;

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
    int wanted = (_logWriter?.SegmentLineI ?? 0) + 1 /* segment header */ + 1 /* divider */;
    return Math.Clamp(wanted, minLogPaneHeight, max);
  }

  private void UpdateStatus()
  {
    string summary = _logWriter.GetDiagnosticsSummary();
    NewEntryInfo.Text = summary;
    string summaryShort = _logWriter.GetDiagnosticsSummary(20);
    Splitter.FirstCollapsedText = $"↓ Show Log - {summaryShort} ↓";

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
