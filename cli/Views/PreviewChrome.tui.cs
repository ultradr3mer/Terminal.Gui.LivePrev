// The layout half of the preview root. Previewable on its own like any other view:
//   tuipreview --view cli\Views\PreviewChrome.tui.cs --class TuiPreview.Cli.Preview.PreviewChrome
// The logic half (the log, the rebuild and quit wiring) is PreviewChrome.cs.

using Terminal.Gui.Drawing;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;
using TuiControls;

namespace TuiPreview.Cli.Preview;

/// <summary>
/// The preview root. A borderless <c>Runnable</c> rather than a <c>Window</c>, so the previewed
/// view gets the whole terminal.
/// </summary>
internal sealed partial class PreviewChrome : Runnable
{
  public const string TitleText = "TuiLivePreview";

  public const string DefaultPreviewPlaceholderText =
      "No view hosted yet.\n\nThe previewed view appears here.";

  private const int ToolbarHeight = 1; 

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

  // A Runnable has no border to draw its title on.
  private readonly Label _titleLabel = new() { CanFocus = false };

  // Markdown rather than a TextView, so a diagnostic's file position can be a clickable link.
  // Clicks reach it without focus, and staying unfocusable keeps the keys for the preview.
  private readonly Markdown _log = new()
  {
    X = 0,
    Y = Pos.Align(Alignment.Start),
    Width = Dim.Fill(),
    Height = Dim.Fill(),
    CanFocus = false,
    ShowCopyButtons = false,
    BorderStyle = LineStyle.None,
    ViewportSettings = ViewportSettingsFlags.HasVerticalScrollBar,
  };

  public View LogPane { get; } = new()
  {
    Width = Dim.Fill(),
    Id = "Log",
    Height = Dim.Fill(),
    ViewportSettings = ViewportSettingsFlags.Transparent,
  };

  public View PreviewPane { get; } = new()
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

  public Splitter Splitter { get; } = new()
  {
    Y = Pos.Align(Alignment.Start),
    Height = Dim.Fill(),
    Orientation = Orientation.Vertical,
    FixedPane = SplitterPane.First,
    // A static height for the layout alone. The logic half replaces it with one that grows
    // with the log.
    FixedSize = 3,
    ViewportSettings = ViewportSettingsFlags.Transparent,
    CanCollapseFirst = true,
    CanCollapseSecond = true
  };

  private readonly Label _previewPlaceholder = new()
  {
    X = 2,
    Y = 0,
    Text = DefaultPreviewPlaceholderText,
    CanFocus = false,
  };

  private void InitializeComponent()
  {
    Title = TitleText;
    Width = Dim.Fill();
    Height = Dim.Fill();
    ViewportSettings = ViewportSettingsFlags.Transparent;

    _log.VerticalScrollBar.VisibilityMode = ScrollBarVisibilityMode.Auto;

    LogPane.Add(_toolbarRow, _log);
    Splitter.First.Add(LogPane);

    PreviewPane.Add(_previewPlaceholder);
    Splitter.Second.Add(PreviewPane);

    BuildToolbar();
    _titleLabel.Text = Title;

    Add(Splitter);
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
}
