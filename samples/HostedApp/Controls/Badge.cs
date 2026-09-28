using Terminal.Gui.Views;

namespace HostedApp.Controls;

/// <summary>
/// A custom control defined in the app. Internal on purpose: the preview reaches it through the
/// InternalsVisibleTo("TuiPreview") that TuiPreview.Host.targets adds.
/// </summary>
internal sealed class Badge : Label
{
    private string _caption = string.Empty;
    private int _count;

    public string Caption
    {
        get => _caption;
        set { _caption = value; Refresh(); }
    }

    public int Count
    {
        get => _count;
        set { _count = value; Refresh(); }
    }

    private void Refresh() => Text = $"◆ {_caption}: {_count}";
}
