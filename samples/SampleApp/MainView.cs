namespace SampleApp;

// The logic half. The preview never compiles this file, so it may use anything the app has:
// here only a list and DEBUG, but equally a service, a package or a database.
public sealed partial class MainView
{
    public MainView()
    {
        InitializeComponent();

        List<string> items = ["alpha", "beta", "gamma"];
        string? build = null;
#if DEBUG
        build = "debug build";
#endif

        _summary.Text = $"{items.Count} items, {build ?? "release build"}";
        _badge.Text = $"◆ Previews: {items.Count}";
    }
}
