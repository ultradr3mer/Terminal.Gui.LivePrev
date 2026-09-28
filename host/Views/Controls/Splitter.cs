using Terminal.Gui.App;
using Terminal.Gui.Drawing;
using Terminal.Gui.Input;
using Terminal.Gui.Text;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace TuiPreview.Host.Controls;

/// <summary>Welche Hälfte eines <see cref="Splitter"/> gemeint ist.</summary>
internal enum SplitterPane
{
    /// <summary>Links (<see cref="Orientation.Horizontal"/>) bzw. oben (<see cref="Orientation.Vertical"/>).</summary>
    First,

    /// <summary>Rechts (<see cref="Orientation.Horizontal"/>) bzw. unten (<see cref="Orientation.Vertical"/>).</summary>
    Second
}

/// <summary>
/// Teilt seine Fläche in zwei Hälften mit einer ziehbaren Trennlinie.
/// <see cref="Orientation.Horizontal"/> legt <see cref="First"/> und <see cref="Second"/>
/// nebeneinander (links | rechts), <see cref="Orientation.Vertical"/> übereinander
/// (oben / unten). Die <see cref="FixedPane"/> startet mit <see cref="FixedSize"/>
/// (z. B. <c>20</c> Zellen oder <c>Dim.Percent(30)</c>) und trägt die Trennlinie; die
/// andere füllt den Rest.
/// Inhalte kommen per <c>First.Add(…)</c> bzw. <c>Second.Add(…)</c> hinein.
/// </summary>
/// <remarks>
/// <para>
/// Gezogen wird über die Arrangement-Funktion von Terminal.Gui an der Trennlinie der
/// festen Hälfte. Danach ist ihre Größe absolut (eine Prozent-Angabe gilt nur für den
/// Start); alles andere bleibt deklarativ, damit der Splitter Größenänderungen des
/// Fensters weiter folgt.
/// </para>
/// <para>
/// Mit <see cref="CanCollapseFirst"/> / <see cref="CanCollapseSecond"/> lässt sich eine
/// Hälfte ganz hinausziehen: Sie verschwindet, und an ihrem Rand erscheint ein Button
/// (Standard: Pfeil, <see cref="View.Title"/> der Hälfte, Pfeil), der sie wieder
/// aufklappt. Ohne Erlaubnis behält jede Hälfte mindestens eine Zeile bzw. Spalte.
/// </para>
/// </remarks>
internal class Splitter : View
{
    /// <summary>Dicke der Trennlinie (Rand der festen Hälfte).</summary>
    private const int DividerSize = 1;

    private readonly Button expandButton;
    private Orientation orientation = Orientation.Horizontal;
    private SplitterPane fixedPane = SplitterPane.First;
    private Dim fixedSize = Dim.Percent(30);
    private bool canCollapseFirst;
    private bool canCollapseSecond;
    private SplitterPane? collapsed;

    /// <summary>
    /// Eingeklappt, während die Maus noch zog: Das Loslassen landet auf dem gerade
    /// erschienenen Button und würde als Klick sofort wieder aufklappen.
    /// </summary>
    private bool ignoreDragRelease;

    /// <summary>Zuletzt gesehene (gezogene) Größe der festen Hälfte — nur Änderungen gelten als Ziehen.</summary>
    private Dim? lastSeenSize;

    /// <summary>Letzte Größe, bei der beide Hälften sichtbar waren; damit wird wieder aufgeklappt.</summary>
    private Dim? restoreSize;

    public Splitter()
    {
        Width = Dim.Fill();
        Height = Dim.Fill();
        First = new View { Id = "First" };
        Second = new View { Id = "Second" };
        // Ohne Schatten (null — ShadowStyles.None ließe dessen Rand rechts/unten stehen),
        // damit der Button genau eine Zeile bzw. Spalte am Rand belegt.
        expandButton = new Button { Id = "Expand", Visible = false, ShadowStyle = null };
        expandButton.Accepted += (_, _) => Expand();
        expandButton.Accepting += (_, e) =>
        {
            if (ignoreDragRelease)
            {
                ignoreDragRelease = false;
                e.Handled = true;
            }
        };

        // Ein echter Klick beginnt mit einem Druck auf dem Button selbst.
        expandButton.MouseEvent += (_, mouse) =>
        {
            if (mouse.Flags == MouseFlags.LeftButtonPressed)
            {
                ignoreDragRelease = false;
            }
        };

        // Der Button kommt zuletzt, damit er über der sichtbaren Hälfte liegt.
        Add(First, Second, expandButton);
        ApplyLayout();
    }

    /// <summary>Links bzw. oben.</summary>
    public View First { get; }

    /// <summary>Rechts bzw. unten.</summary>
    public View Second { get; }

    /// <summary><see cref="Orientation.Horizontal"/>: nebeneinander; <see cref="Orientation.Vertical"/>: übereinander.</summary>
    public Orientation Orientation
    {
        get => orientation;
        set
        {
            orientation = value;
            ResetSizes();
            ApplyLayout();
        }
    }

    /// <summary>Die Hälfte mit fester Startgröße; die andere füllt den Rest.</summary>
    public SplitterPane FixedPane
    {
        get => fixedPane;
        set
        {
            fixedPane = value;
            ResetSizes();
            ApplyLayout();
        }
    }

    /// <summary>Startgröße der <see cref="FixedPane"/>, z. B. <c>20</c> (Zellen) oder <c>Dim.Percent(30)</c>.</summary>
    public Dim FixedSize
    {
        get => fixedSize;
        set
        {
            fixedSize = value;
            ResetSizes();
            ApplyLayout();
        }
    }

    /// <summary>Ob <see cref="First"/> ganz hinausgezogen (eingeklappt) werden darf.</summary>
    public bool CanCollapseFirst
    {
        get => canCollapseFirst;
        set
        {
            canCollapseFirst = value;
            if (!value && collapsed == SplitterPane.First)
            {
                Expand();
            }
        }
    }

    /// <summary>Ob <see cref="Second"/> ganz hinausgezogen (eingeklappt) werden darf.</summary>
    public bool CanCollapseSecond
    {
        get => canCollapseSecond;
        set
        {
            canCollapseSecond = value;
            if (!value && collapsed == SplitterPane.Second)
            {
                Expand();
            }
        }
    }

    /// <summary>Button-Text, solange <see cref="First"/> eingeklappt ist; <c>null</c>: Pfeil, Titel, Pfeil.</summary>
    public string? FirstCollapsedText { get; set; }

    /// <summary>Button-Text, solange <see cref="Second"/> eingeklappt ist; <c>null</c>: Pfeil, Titel, Pfeil.</summary>
    public string? SecondCollapsedText { get; set; }

    /// <summary>Die eingeklappte Hälfte, sonst <c>null</c>.</summary>
    public SplitterPane? Collapsed => collapsed;

    /// <summary>Die Hälfte mit fester Größe.</summary>
    public View FixedView => fixedPane == SplitterPane.First ? First : Second;

    /// <summary>Die Hälfte, die den Rest füllt.</summary>
    public View FillView => fixedPane == SplitterPane.First ? Second : First;

    /// <summary>Wird ausgelöst, wenn eine Hälfte ein- oder wieder aufgeklappt wird.</summary>
    public event EventHandler? CollapsedChanged;

    /// <summary>Klappt <paramref name="pane"/> ein — auch ohne <see cref="CanCollapseFirst"/>/<see cref="CanCollapseSecond"/>.</summary>
    public void Collapse(SplitterPane pane)
    {
        if (collapsed == pane)
        {
            return;
        }

        collapsed = pane;
        ApplyLayout();
        CollapsedChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Klappt die eingeklappte Hälfte wieder auf: mit <paramref name="dim"/> als neuer Größe
    /// der festen Hälfte (z. B. passend zum Inhalt), sonst mit ihrer letzten Größe.
    /// </summary>
    public void Expand(Dim? dim = null)
    {
        if (collapsed is null)
        {
            return;
        }

        collapsed = null;
        restoreSize = dim ?? restoreSize;
        lastSeenSize = restoreSize;
        ApplyLayout();
        CollapsedChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Vor jedem Layout: ein Ziehen auswerten (Einklappen oder Begrenzen) und alles außer
    /// der gezogenen Größe wieder deklarativ machen — Ziehen setzt den Frame der festen
    /// Hälfte und damit X, Y, Width und Height absolut.
    /// </summary>
    protected override void OnSubViewLayout(LayoutEventArgs args)
    {
        if (collapsed is null)
        {
            HandleDrag(MainLength(args.OldContentSize));
        }

        if (collapsed is null)
        {
            Reanchor(FixedView);
        }

        base.OnSubViewLayout(args);
    }

    private bool Horizontal => orientation == Orientation.Horizontal;

    private int MainLength(System.Drawing.Size size) => Horizontal ? size.Width : size.Height;

    private void ResetSizes()
    {
        lastSeenSize = null;
        restoreSize = null;
    }

    /// <summary>
    /// Gezogene Größe der festen Hälfte prüfen: auf 0 Inhalt gezogen klappt sie ein, über
    /// die ganze Fläche gezogen die andere — sofern erlaubt, sonst wird begrenzt. Nur eine
    /// geänderte Größe gilt als Ziehen; ein kleineres Fenster klappt nichts ein.
    /// </summary>
    private void HandleDrag(int total)
    {
        var fix = FixedView;
        if (total <= 0 || (Horizontal ? fix.Width : fix.Height) is not DimAbsolute { Size: var size })
        {
            return;
        }

        var dragged = size != lastSeenSize;
        var fixedCollapsible = fixedPane == SplitterPane.First ? canCollapseFirst : canCollapseSecond;
        var fillCollapsible = fixedPane == SplitterPane.First ? canCollapseSecond : canCollapseFirst;
        if (dragged && size <= DividerSize && fixedCollapsible)
        {
            ignoreDragRelease = IsDragging(fix);
            Collapse(fixedPane);
            return;
        }

        if (dragged && size >= total && fillCollapsible)
        {
            ignoreDragRelease = IsDragging(fix);
            Collapse(fixedPane == SplitterPane.First ? SplitterPane.Second : SplitterPane.First);
            return;
        }

        // Beide Hälften behalten mindestens eine Zeile bzw. Spalte Inhalt.
        var clamped = Math.Clamp(size, DividerSize + 1, Math.Max(DividerSize + 1, total - 1));
        if (clamped != size)
        {
            SetMainSize(fix, clamped);
        }

        lastSeenSize = clamped;

        // Während die Maus zieht, bleibt die Größe von vor dem Ziehen gemerkt — sonst klappte
        // eine hinausgezogene Hälfte mit der kleinsten Zwischengröße wieder auf.
        if (!IsDragging(fix))
        {
            restoreSize = clamped;
        }
    }

    /// <summary>Hält die Maus gerade die Trennlinie (den Rand der festen Hälfte) fest?</summary>
    private bool IsDragging(View fix)
    {
        return App?.Mouse is IMouseGrabHandler mouse
            && (mouse.IsGrabbed(fix) || (fix.Border.View is { } border && mouse.IsGrabbed(border)));
    }

    private void ApplyLayout()
    {
        if (collapsed is { } pane)
        {
            ApplyCollapsedLayout(pane);
        }
        else
        {
            ApplySplitLayout();
        }

        SetNeedsLayout();
    }

    private void ApplySplitLayout()
    {
        var (fix, fill) = (FixedView, FillView);
        fix.Visible = true;
        fill.Visible = true;
        expandButton.Visible = false;

        fill.BorderStyle = LineStyle.None;
        fill.Arrangement = ViewArrangement.Fixed;
        fill.SuperViewRendersLineCanvas = false;

        // Die Trennlinie ist der Rand der festen Hälfte auf der Seite zur füllenden hin;
        // gezeichnet wird sie in der LineCanvas des Splitters.
        // Der Titel der Hälfte ist nur für den Aufklapp-Button da, nicht für die Linie.
        fix.BorderStyle = LineStyle.Single;
        fix.Border.Settings &= ~BorderSettings.Title;
        fix.SuperViewRendersLineCanvas = true;
        (fix.Arrangement, fix.Border.Thickness) = (Horizontal, fixedPane) switch
        {
            (true, SplitterPane.First) => (ViewArrangement.RightResizable, new Thickness(0, 0, DividerSize, 0)),
            (true, SplitterPane.Second) => (ViewArrangement.LeftResizable, new Thickness(DividerSize, 0, 0, 0)),
            (false, SplitterPane.First) => (ViewArrangement.BottomResizable, new Thickness(0, 0, 0, DividerSize)),
            _ => (ViewArrangement.TopResizable, new Thickness(0, DividerSize, 0, 0))
        };

        var size = restoreSize ?? fixedSize;
        if (Horizontal)
        {
            fix.Width = size;
            fill.X = fixedPane == SplitterPane.First ? Pos.Right(fix) : 0;
            fill.Y = 0;
            fill.Width = fixedPane == SplitterPane.First ? Dim.Fill() : Dim.Fill(to: fix);
            fill.Height = Dim.Fill();
        }
        else
        {
            fix.Height = size;
            fill.X = 0;
            fill.Y = fixedPane == SplitterPane.First ? Pos.Bottom(fix) : 0;
            fill.Width = Dim.Fill();
            fill.Height = fixedPane == SplitterPane.First ? Dim.Fill() : Dim.Fill(to: fix);
        }

        // Position und Querachse der festen Hälfte setzt dieselbe Logik wie nach dem Ziehen.
        Reanchor(fix);
    }

    /// <summary>
    /// Eingeklappt: die andere Hälfte nimmt alles ein (ohne Trennlinie, nicht ziehbar), und
    /// der Button liegt am Rand der eingeklappten — mittig, Pfeile in Aufklapprichtung.
    /// </summary>
    private void ApplyCollapsedLayout(SplitterPane pane)
    {
        var (hidden, shown) = pane == SplitterPane.First ? (First, Second) : (Second, First);
        hidden.Visible = false;
        shown.Visible = true;
        shown.Arrangement = ViewArrangement.Fixed;
        shown.Border.Thickness = new Thickness(0);
        (shown.X, shown.Y, shown.Width, shown.Height) = (0, 0, Dim.Fill(), Dim.Fill());

        var atStart = pane == SplitterPane.First;
        var arrow = (Horizontal, atStart) switch
        {
            (true, true) => "→",
            (true, false) => "←",
            (false, true) => "↓",
            _ => "↑"
        };
        var name = string.IsNullOrEmpty(hidden.Title) ? hidden.Id : hidden.Title;
        expandButton.Text = (atStart ? FirstCollapsedText : SecondCollapsedText) ?? $"{arrow} {name} {arrow}";
        expandButton.TextDirection = Horizontal ? TextDirection.TopBottom_LeftRight : TextDirection.LeftRight_TopBottom;

        // Genau eine Zeile bzw. Spalte, damit der Button am Rand sitzt (ohne Schatten-Rand).
        expandButton.Width = Horizontal ? 1 : Dim.Auto(DimAutoStyle.Text);
        expandButton.Height = Horizontal ? Dim.Auto(DimAutoStyle.Text) : 1;
        expandButton.X = Horizontal ? (atStart ? 0 : Pos.AnchorEnd()) : Pos.Center();
        expandButton.Y = Horizontal ? Pos.Center() : (atStart ? 0 : Pos.AnchorEnd());
        expandButton.Visible = true;
    }

    private void SetMainSize(View view, int cells)
    {
        if (Horizontal)
        {
            view.Width = cells;
        }
        else
        {
            view.Height = cells;
        }
    }

    /// <summary>
    /// Feste Hälfte an ihren Rand heften: die zweite ans Ende (AnchorEnd), die erste an 0;
    /// die Querachse füllt. Gesetzt wird nur, was abweicht — sonst löste jeder Durchlauf
    /// ein neues Layout aus.
    /// </summary>
    private void Reanchor(View fix)
    {
        var atEnd = fixedPane == SplitterPane.Second;
        if (Horizontal)
        {
            SetPos(fix, atEnd, horizontal: true);
            if (fix.Y is not PosAbsolute { Position: 0 })
            {
                fix.Y = 0;
            }

            if (fix.Height is not DimFill)
            {
                fix.Height = Dim.Fill();
            }
        }
        else
        {
            SetPos(fix, atEnd, horizontal: false);
            if (fix.X is not PosAbsolute { Position: 0 })
            {
                fix.X = 0;
            }

            if (fix.Width is not DimFill)
            {
                fix.Width = Dim.Fill();
            }
        }
    }

    private static void SetPos(View fix, bool atEnd, bool horizontal)
    {
        var current = horizontal ? fix.X : fix.Y;
        var wanted = atEnd
            ? current is PosAnchorEnd ? null : Pos.AnchorEnd()
            : current is PosAbsolute { Position: 0 } ? null : Pos.Absolute(0);
        if (wanted is null)
        {
            return;
        }

        if (horizontal)
        {
            fix.X = wanted;
        }
        else
        {
            fix.Y = wanted;
        }
    }
}
