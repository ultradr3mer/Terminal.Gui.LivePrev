using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace SampleApp;

// The layout half: controls and their placement, nothing the preview could not compile on its
// own. What only the app knows goes into MainView.cs.
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
