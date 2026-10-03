using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace SampleApp;

// The layout half: controls and their placement, nothing the preview could not compile on its
// own. What only the app knows goes into MainView.cs.
//
// The @tuipreview annotation is optional. It pins the class the preview instantiates and the
// size it renders at, so the view is previewable the same way from VS Code and from a bare
// command line, and so this file states what it wants rather than leaving it to be inferred.
// @tuipreview class=SampleApp.MainView width=100 height=20
public sealed partial class MainView : Window
{
    private readonly Label _summary = new() { X = 1, Y = 1, Text = "0 items, release build" };
    private readonly Label _badge = new() { X = 1, Y = 3, Text = "◆ Previews: 0" };

    private void InitializeComponent()
    {
        Title = "Sample App";
        Add(_summary, _badge);
    }
}
