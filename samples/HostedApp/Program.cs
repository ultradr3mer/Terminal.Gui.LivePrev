using Terminal.Gui.App;
using TuiPreview.Host;

// First, before any of the app's own setup: with --tui-preview this runs the preview instead.
if (TuiPreviewHost.TryRun(args, out int code))
{
    return code;
}

using IApplication app = Application.Create();
app.Init();

using var view = new HostedApp.MainView();
app.Run(view);
return 0;
