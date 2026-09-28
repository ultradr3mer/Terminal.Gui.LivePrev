using Microsoft.CodeAnalysis;
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

  public PreviewChrome()
  {
    InitializeComponent();

    // Set only once: reassigning FixedSize would discard a size the user dragged.
    Splitter.FixedSize = Dim.Func(_ => CalcLogPaneHeight());

    _logWriter = new DiagnosticsLog
    {
      AppendCompleted = OnLogAppendComplete
    };

    WireInput();

    // The builder sets Title after construction.
    TitleChanged += (_, _) => _titleLabel.Text = Title;

    UpdateStatus();
  }

  public event Action? RebuildRequested;

  public event Action? QuitRequested;

  private void OnLogAppendComplete(string line)
  {
    _log.Text = _logWriter.GetFullLog();

    try
    {
      _log.MoveEnd();
    }
    catch (InvalidOperationException)
    {
      // Layout has not run yet; the first draw positions it.
    }

    UpdateStatus();
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

  internal void AppendLog(string v)
  {
    _logWriter.AppendLog(v);
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
