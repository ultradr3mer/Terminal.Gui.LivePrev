// A previewed view written in plain C#, with no XAML and no source generator involved.
//
// This exists to show the C# path is genuinely independent of the generator rather than a
// thin wrapper over it. Nothing here maps to a XAML element, nothing is generated, and there
// is no schema to lag behind: the controls are constructed by hand, so anything the
// Terminal.Gui assembly exposes is usable, including the ones the XSD does not know about
// (DropDownList, Tabs, BorderView, ...).
//
// The only contract is the one BuildClass enforces:
//   * a public parameterless constructor
//   * derive from a Terminal.Gui top-level (Window implements IRunnable)
//
// Note this file is a NORMAL .cs, not a .tui.cs. It is compiled on its own; it does not
// need a partial half, an InitializeComponent, or a synthesised stub.
#nullable enable

// A --view file is compiled on its own, with no project and no implicit usings, so every
// namespace it touches has to be named here. MainWindow.tui.cs needed the same for the same
// reason; here it is a one-line difference between compiling and not.
using System.Collections.Generic;
using Terminal.Gui.App;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace CoreBridge.Tui;

/// <summary>A counter, to prove interactivity survives the C# path.</summary>
public sealed class CounterView : Window 
{
    private readonly Label _count;
    private readonly Button _increment;

    public CounterView()
    {
        Title = "CounterView - C# only";
        Width = Dim.Fill();
        Height = Dim.Fill();

        _count = new Label
        {
            X = Pos.Center(),
            Y = 1,
            Text = "0",
        };

        _increment = new Button
        {
            X = Pos.Center(),
            Y = 3,
            Text = "Increment",
        };

        // The reason to prefer C# over XAML, in three lines: a real closure over a real
        // field. There is no ViewModel, no binding, no PropertyChanged - just the handler.
        int value = 0;
        _increment.Accepting += (_, _) =>
        {
            value++;
            _count.Text = value.ToString();
        };

        // DropDownList is one of the 15 controls the generator supports but the XSD does not
        // declare, so it cannot be expressed in XAML at all. Here it is just a constructor.
        var commands = new DropDownList
        {
            X = 2,
            Y = 5,
            Width = 24,

            // ObservableCollection, not List: IListDataSource implementations bind to it and
            // raise change notifications. A plain List compiles against a looser overload in
            // some versions and fails against this one (CS1503), which is exactly the kind of
            // friction the generator used to add and that plain C# does not.
            Source = new ListWrapper<string>(
                new System.Collections.ObjectModel.ObservableCollection<string>
                {
                    "Build", "Test", "Publish",
                }),
        };

        Add(_count, _increment, commands);
    }
}
