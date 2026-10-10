using Microsoft.VisualStudio.Extensibility;
using Microsoft.VisualStudio.Extensibility.Commands;
using TuiLivePreview.VisualStudio.Core;

namespace TuiLivePreview.VisualStudio;

/// <summary>Where the commands appear.</summary>
internal static class Placements
{
    // The command configurations below are read at build time into the extension's metadata, so
    // they may only use constants: no helper methods, and no shared static properties. Hence the
    // repetition in each command.

    /// <summary>guidSHLMainMenu, which owns the shell's standard context menus.</summary>
    public const string ShellMainMenu = "d309f791-903f-11d0-9efc-00a0c911004f";

    /// <summary>IDG_VS_CTXT_ITEM_OPEN: the "Open / Open With" group of a Solution Explorer item.</summary>
    public const uint SolutionExplorerItemOpenGroup = 0x0209;

    /// <summary>IDG_VS_CODEWIN_NAVIGATETOFILE: the "View Code / View Designer" group of the code editor.</summary>
    public const uint CodeEditorNavigateToFileGroup = 0x02B2;

    /// <summary>
    /// Shown only for <c>.tui.cs</c> files. A regex, so the dots are escaped; a bare <c>.cs</c>
    /// would put the menus on every source file, including the logic half of a view, which is
    /// exactly the file the preview must not compile.
    /// </summary>
    public const string ViewFilePattern = @"\.tui\.cs$";

    /// <summary>
    /// Extensions &gt; TUI Live Preview holds every command, the counterpart of the VS Code command
    /// palette, and the only home of Stop All and Show Log.
    /// </summary>
    [VisualStudioContribution]
    public static MenuConfiguration ExtensionsMenu => new("TUI Live Preview")
    {
        Placements = [CommandPlacement.KnownPlacements.ExtensionsMenu],
        Children =
        [
            MenuChild.Command<InteractiveCommand>(),
            MenuChild.Command<RenderCommand>(),
            MenuChild.Command<WatchCommand>(),
            MenuChild.Command<InitCommand>(),
            MenuChild.Separator,
            MenuChild.Command<StopAllCommand>(),
            MenuChild.Command<ShowLogCommand>(),
        ],
    };
}

[VisualStudioContribution]
internal sealed class InteractiveCommand(PreviewService service) : Command
{
    public override CommandConfiguration CommandConfiguration => new("TUI Preview: Interactive (live view)")
    {
        TooltipText = "Run the view as a live Terminal.Gui app in a console window.",
        Placements =
        [
            CommandPlacement.VsctParent(new Guid(Placements.ShellMainMenu), Placements.SolutionExplorerItemOpenGroup, 0x0100),
            CommandPlacement.VsctParent(new Guid(Placements.ShellMainMenu), Placements.CodeEditorNavigateToFileGroup, 0x0100),
        ],
        VisibleWhen = ActivationConstraint.ClientContext(ClientContextKey.Shell.ActiveSelectionFileName, Placements.ViewFilePattern),
    };

    public override Task ExecuteCommandAsync(IClientContext context, CancellationToken cancellationToken) =>
        service.GuardAsync("interactive", () => service.LaunchAsync(PreviewMode.Interactive, context, cancellationToken), cancellationToken);
}

[VisualStudioContribution]
internal sealed class RenderCommand(PreviewService service) : Command
{
    public override CommandConfiguration CommandConfiguration => new("TUI Preview: Render Once")
    {
        TooltipText = "Print one frame of the view as characters.",
        Placements =
        [
            CommandPlacement.VsctParent(new Guid(Placements.ShellMainMenu), Placements.SolutionExplorerItemOpenGroup, 0x0101),
            CommandPlacement.VsctParent(new Guid(Placements.ShellMainMenu), Placements.CodeEditorNavigateToFileGroup, 0x0101),
        ],
        VisibleWhen = ActivationConstraint.ClientContext(ClientContextKey.Shell.ActiveSelectionFileName, Placements.ViewFilePattern),
    };

    public override Task ExecuteCommandAsync(IClientContext context, CancellationToken cancellationToken) =>
        service.GuardAsync("render", () => service.LaunchAsync(PreviewMode.Render, context, cancellationToken), cancellationToken);
}

[VisualStudioContribution]
internal sealed class WatchCommand(PreviewService service) : Command
{
    public override CommandConfiguration CommandConfiguration => new("TUI Preview: Watch (re-render on change)")
    {
        TooltipText = "Re-render the view whenever it changes.",
        Placements =
        [
            CommandPlacement.VsctParent(new Guid(Placements.ShellMainMenu), Placements.SolutionExplorerItemOpenGroup, 0x0102),
            CommandPlacement.VsctParent(new Guid(Placements.ShellMainMenu), Placements.CodeEditorNavigateToFileGroup, 0x0102),
        ],
        VisibleWhen = ActivationConstraint.ClientContext(ClientContextKey.Shell.ActiveSelectionFileName, Placements.ViewFilePattern),
    };

    public override Task ExecuteCommandAsync(IClientContext context, CancellationToken cancellationToken) =>
        service.GuardAsync("watch", () => service.LaunchAsync(PreviewMode.Watch, context, cancellationToken), cancellationToken);
}

[VisualStudioContribution]
internal sealed class InitCommand(PreviewService service) : Command
{
    public override CommandConfiguration CommandConfiguration => new("TUI Preview: Init (create preview settings)")
    {
        TooltipText = "Write the view's class and size to a .tui.json sidecar or a // @tuipreview line.",
        Placements =
        [
            CommandPlacement.VsctParent(new Guid(Placements.ShellMainMenu), Placements.SolutionExplorerItemOpenGroup, 0x0103),
            CommandPlacement.VsctParent(new Guid(Placements.ShellMainMenu), Placements.CodeEditorNavigateToFileGroup, 0x0103),
        ],
        VisibleWhen = ActivationConstraint.ClientContext(ClientContextKey.Shell.ActiveSelectionFileName, Placements.ViewFilePattern),
    };

    public override Task ExecuteCommandAsync(IClientContext context, CancellationToken cancellationToken) =>
        service.GuardAsync("init", () => service.InitAsync(context, cancellationToken), cancellationToken);
}

[VisualStudioContribution]
internal sealed class StopAllCommand(PreviewService service) : Command
{
    public override CommandConfiguration CommandConfiguration => new("Stop All Previews")
    {
        TooltipText = "Close every preview window this extension opened.",
    };

    public override Task ExecuteCommandAsync(IClientContext context, CancellationToken cancellationToken) =>
        service.GuardAsync("stopAll", () => service.StopAllAsync(cancellationToken), cancellationToken);
}

[VisualStudioContribution]
internal sealed class ShowLogCommand(PreviewService service) : Command
{
    public override CommandConfiguration CommandConfiguration => new("Show Log")
    {
        TooltipText = "Open the TUI Preview log. It is also under View > Output, \"TUI Preview\".",
    };

    public override Task ExecuteCommandAsync(IClientContext context, CancellationToken cancellationToken) =>
        service.GuardAsync("showLog", () => service.ShowLogAsync(cancellationToken), cancellationToken);
}
