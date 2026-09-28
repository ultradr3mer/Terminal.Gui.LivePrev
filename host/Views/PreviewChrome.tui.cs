// Previewable on its own, so keep the constructor parameterless:
//   cli.exe --view host\Preview\PreviewChrome.tui.cs --project host\TuiPreview.Host.csproj --class TuiPreview.Host.Preview.PreviewChrome

#nullable enable

using Microsoft.CodeAnalysis;
using System;
using System.Collections.Generic;
using Terminal.Gui.App;
using Terminal.Gui.Drawing;
using Terminal.Gui.Drivers;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace TuiPreview.Host.Preview;

/// <summary>
/// The preview root. A borderless <c>Runnable</c> rather than a <c>Window</c>, so the previewed
/// view gets the whole terminal.
/// </summary>
internal sealed class PreviewChrome : Runnable
{
  public const string TitleText = "TuiLivePreview";

#pragma warning disable CS0618
  private readonly TextView _log;
#pragma warning restore CS0618

  private DiagnosticsLog _logWriter;
  private bool _logAutoCollapse = true;

  // Clickable because VS Code's terminal does not deliver every key.
  public readonly Shortcut RebuildButton = new() { Title = "⟳ Rebuild", Key = Key.F5 };
  public readonly Shortcut CollapseButton = new() { Title = "⇱ Hide Log", Key = Key.F10 };
  public readonly CheckBox CollapseAutoButton = new() { Title = "Auto", Value = CheckState.Checked };
  public readonly Shortcut FullButton = new() { Title = "⤢ Full", Key = Key.F10 };
  public readonly Shortcut QuitButton = new() { Title = "✕ Quit", Key = Key.C.WithCtrl };

  public readonly Label NewEntryInfo = new();
  // public readonly Label ErrorCount = new();

  // A plain View, not a Bar: a Bar replaces items that don't fit with an overflow arrow.
  private readonly View _toolbarRow = new()
  {
    X = 0,
    Y = Pos.Align(Alignment.Start),
    Width = Dim.Fill(),
    Height = ToolbarHeight,
    BorderStyle = LineStyle.None,
    CanFocus = false,
  };

  private const int ToolbarHeight = 1;

  // A Runnable has no border to draw its title on.
  private readonly Label _titleLabel = new() { CanFocus = false };

  public View LogPane { get; }

  public View PreviewPane { get; }

  public Controls.Splitter Splitter { get; }

  public const string DefaultPreviewPlaceholderText =
      "No view hosted yet.\n\nThe previewed view appears here.";

  private readonly Label _previewPlaceholder;

  public PreviewChrome()
  {
    Title = "TuiLivePreview";
    Width = Dim.Fill();
    Height = Dim.Fill();
    ViewportSettings = ViewportSettingsFlags.Transparent;

    Splitter = new Controls.Splitter
    {
      Y = Pos.Align(Alignment.Start),
      Height = Dim.Fill(),
      Orientation = Terminal.Gui.ViewBase.Orientation.Vertical,
      FixedPane = Controls.SplitterPane.First,
      // Set only once: reassigning FixedSize would discard a size the user dragged.
      FixedSize = Dim.Func(_ => CalcLogPaneHeight()),
      ViewportSettings = ViewportSettingsFlags.Transparent,
      CanCollapseFirst = true,
      CanCollapseSecond = true
    };

    LogPane = new View
    {
      Width = Dim.Fill(),
      Id = "Log",
      Height = Dim.Fill(),
      ViewportSettings = ViewportSettingsFlags.Transparent,
    };

    // TextView is obsolete in favour of Terminal.Gui.Editor, which is overkill for a read-only log.
#pragma warning disable CS0618
    _log = new TextView
    {
      X = 0,
      Y = Pos.Align(Alignment.Start),
      Width = Dim.Fill(),
      Height = Dim.Fill(),
      ReadOnly = true,
      // Without wrap, MoveEnd scrolls sideways and clips the start of every line.
      WordWrap = true,
      CanFocus = false,
      BorderStyle = LineStyle.None,
      ViewportSettings = ViewportSettingsFlags.HasVerticalScrollBar,
    };
    _log.VerticalScrollBar.VisibilityMode = ScrollBarVisibilityMode.Auto;
#pragma warning restore CS0618

    LogPane.Add(_toolbarRow, _log);
    Splitter.First.Add(LogPane);

    PreviewPane = new View
    {
      X = 0,
      Y = 0,
      Width = Dim.Fill(),
      Id = "Preview",
      Height = Dim.Fill(),
      ViewportSettings =
            ViewportSettingsFlags.HasVerticalScrollBar |
            ViewportSettingsFlags.HasHorizontalScrollBar |
            ViewportSettingsFlags.AllowXGreaterThanContentWidth |
            ViewportSettingsFlags.AllowYGreaterThanContentHeight,
    };

    Splitter.Second.Add(PreviewPane);

    _previewPlaceholder = new Label
    {
      X = 2,
      Y = 0,
      Text = DefaultPreviewPlaceholderText,
      CanFocus = false,
    };
    PreviewPane.Add(_previewPlaceholder);

    BuildToolbar();
    WireInput();

    _logWriter = new DiagnosticsLog
    {
      AppendCompleted = this.OnLogAppendComplete
    };

    // The builder sets Title after construction.
    TitleChanged += (_, _) => _titleLabel.Text = Title;
    _titleLabel.Text = Title;

    this.Add(Splitter);
    UpdateStatus();
  }

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

  public event Action? RebuildRequested;

  public event Action? QuitRequested;

  private void WireInput()
  {
    RebuildButton.Action += () => RebuildRequested?.Invoke();
    CollapseButton.Action += () => Splitter.Collapse(Controls.SplitterPane.First);
    CollapseAutoButton.ValueChanged += (o, a) => _logAutoCollapse = a.NewValue == CheckState.Checked;
    FullButton.Action += () => Splitter.Collapse(Controls.SplitterPane.Second);
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

    if(_logWriter.GetCurrentSegmentI() == 0)
      return; // Leave Log as initialized

    if (_logWriter.GetMaxSeverity() == DiagnosticSeverity.Error)
    {
      this.Splitter.Expand(_logWriter.SegmentLineI);
      Splitter.First.Height = CalcLogPaneHeight();
    }
    else if (_logAutoCollapse && _logWriter.GetMaxSeverity() < DiagnosticSeverity.Warning)
    {
      this.Splitter.Collapse(Controls.SplitterPane.First);
    }

    if (this.IsInitialized)
    {
      Splitter.SetNeedsLayout(); 
    }
  }

  private void BuildToolbar()
  {
    View[] items = [_titleLabel, RebuildButton, CollapseAutoButton, CollapseButton, FullButton, QuitButton, NewEntryInfo /* , ErrorCount */];
    foreach (View item in items)
    {
      item.X = Pos.Align(Alignment.Start);
      item.Y = 0;
    } 

    _toolbarRow.Add(items);
  }

  internal int CheckMinuteEvent(int lastMinutes)
  {
    int curMinutes = _logWriter.GetMinutesSinceLast();
    if(curMinutes == lastMinutes)
      return lastMinutes;

    this.UpdateStatus();
    return curMinutes;
  }
}

