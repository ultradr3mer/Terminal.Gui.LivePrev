// The preview pipeline compiles this file on its own (no project, no csproj), so the
// nullable context has to be opted into explicitly; without it every `?` below is CS8632.
#nullable enable

using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Terminal.Gui.App;
using Terminal.Gui.Input;
using Terminal.Gui.Views;

namespace CoreBridge.Tui;

/// <summary>
/// Code-behind for <c>PrevWin.tui.xaml</c>. The generated half of this partial class owns
/// the controls; this half owns the data they are bound to.
/// <para>
/// The preview pipeline compiles this file alongside the generated one
/// (<c>RenderPipeline.CodeBehindPath</c>). Without it the pipeline synthesises a stub that has
/// nowhere to put a ViewModel, and every <c>{Bind ...}</c> in the XAML has nothing to bind to.
/// </para>
/// </summary>
public partial class PrevWin
{
    /// <summary>
    /// Name-matched by <c>x:DataType="ViewModel"</c>. The generated <c>SetupBindings()</c> emits
    /// <c>ViewModel.CommandList</c> and subscribes to <c>ViewModel.PropertyChanged</c>, so this
    /// property is the single source of truth for both.
    /// </summary>
    public MainViewModel ViewModel { get; }

    /// <summary>
    /// The generated half has a private <c>InitializeComponent()</c> and therefore no usable
    /// constructor, so the public one lives here.
    /// </summary>
    public PrevWin()
    {
        // Assigned before InitializeComponent: SetupBindings() reads ViewModel.CommandList and
        // throws if the source is still null, and ValueChanged fires while the tree is built.
        ViewModel = new MainViewModel();
        InitializeComponent();

        // // Seeded after InitializeComponent. SetupBindings() has already pushed ViewModel's
        // // (empty) Value onto the control, and Source must be set before Value resolves against
        // // it, so this is the first point where the two are consistent. It fires ValueChanged,
        // // which is exactly the path a real user selection takes.
        // CommandListBox.Value = ViewModel.SelectedCommand;
    }

    // ValueChangedEventArgs<string?> is the exact declared type. A non-nullable
    // ValueChangedEventArgs<string> parameter will not bind: delegate parameter types are
    // invariant, so nullability alone is enough to fail overload resolution (CS0123).
    private void OnCommandSelected(object? sender, ValueChangedEventArgs<string?> e)
    {
        // DropDownList has no TwoWayBindings entry, so the generator cannot wire Value back to
        // the ViewModel for us. Pushing it explicitly keeps the ViewModel authoritative and
        // lets the change propagate out through PropertyChanged like any other bound property.
        // NewValue is string? (clearing the box reports null), hence the fallback (CS8601).
        ViewModel.SelectedCommand = e.NewValue ?? string.Empty;
    }

    // Fully qualified, not bare `EventArgs`: `using Terminal.Gui.App;` is required for
    // ValueChangedEventArgs, but it also brings Terminal.Gui.App.EventArgs<T> into scope,
    // which shadows System.EventArgs and then fails to bind (CS0305).
    private void OnNameChanged(object? sender, System.EventArgs e)
    {
        // // Text is string? on TextField, hence the ?? rather than a direct assignment (CS8601).
        // ViewModel.Name = NameField.Text ?? string.Empty;
    }

    private void OnGreetClicked(object? sender, CommandEventArgs e)
    {
        ViewModel.Greeting = $"Hello, {ViewModel.Name}!";
    }

    private void OnExitClicked(object? sender, CommandEventArgs e)
    {
        // The static Application.RequestStop is obsolete, and the preview host's IApplication
        // is not visible from here (this file is compiled against the framework and
        // Terminal.Gui only). View.App resolves the running application from the view tree.
        App?.RequestStop(this);
    }
}

/// <summary>
/// Data for <c>PrevWin</c>. Implements <see cref="INotifyPropertyChanged"/> because the
/// generated <c>OnViewModelPropertyChanged</c> switch is subscribed to that event; without it
/// the generated <c>ViewModel.PropertyChanged += ...</c> line does not compile.
/// </summary>
public sealed class MainViewModel : INotifyPropertyChanged
{
    private string _name = "Anonymous";
    private string _selectedCommand = "Build";
    private string _greeting = "Hello, Anonymous!";

    /// <summary>
    /// Items for the DropDownList. Typed as <see cref="IListDataSource"/> because that is what
    /// <c>Source</c> takes; <see cref="ListWrapper{T}"/> is Terminal.Gui's default adapter that
    /// renders any sequence.
    /// </summary>
    public IListDataSource CommandList { get; } =
        new ListWrapper<string>(new ObservableCollection<string>
        {
            "Build",
            "Test",
            "Publish",
            "Clean"
        });

    public string Name
    {
        get => _name;
        set => Set(ref _name, value);
    }

    public string SelectedCommand
    {
        get => _selectedCommand;
        set => Set(ref _selectedCommand, value);
    }

    /// <summary>Bound to the greeting Label's <c>Text</c>.</summary>
    public string Greeting
    {
        get => _greeting;
        set => Set(ref _greeting, value);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// Raises <see cref="PropertyChanged"/> only when the value actually changed, so the
    /// generated handler does not reassign the control on every keystroke.
    /// </summary>
    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}