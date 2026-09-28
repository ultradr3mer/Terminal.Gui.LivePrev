using HostedApp.Controls;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace HostedApp;

// Uses what only the app's own build knows: an internal control, implicit usings (List<T>),
// nullable annotations and DEBUG.
public sealed class MainView : Window
{
    public MainView()
    {
        Title = "Hosted App";

        List<string> items = ["alpha", "beta", "gamma"];
        string? build = null;
#if DEBUG
        build = "debug build";
#endif

        Add(new Label { X = 1, Y = 1, Text = $"{items.Count} items, {build ?? "release build"}" });
        Add(new Badge { X = 1, Y = 3, Caption = "Previews", Count = items.Count });
    }
}
