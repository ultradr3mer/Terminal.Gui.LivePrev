using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.Extensibility;

namespace TuiLivePreview.VisualStudio;

/// <summary>
/// Terminal.Gui Live Preview for Visual Studio: the counterpart of the VS Code extension in
/// <c>vscode-extension/</c>, running the same <c>tuipreview</c> tool.
/// </summary>
[VisualStudioContribution]
internal sealed class TuiLivePreviewExtension : Extension
{
    public override ExtensionConfiguration ExtensionConfiguration => new()
    {
        Metadata = new(
            id: "TuiLivePreview.VisualStudio.clara",
            version: ExtensionAssemblyVersion,
            publisherName: "clara",
            displayName: "Terminal.Gui Live Preview",
            description: "Preview .tui.cs views as real Terminal.Gui views, straight from the Solution Explorer and editor context menus.")
        {
            // .NET 10, like the tool. Only Visual Studio 2026 has a .NET 10 extension host; 2022
            // stops at .NET 8, so it is not a target.
            DotnetTargetVersions = [DotnetTarget.Custom("net10.0")],
            InstallationTargetVersion = "[18.0,)",
        },
    };

    protected override void InitializeServices(IServiceCollection serviceCollection)
    {
        base.InitializeServices(serviceCollection);

        // A singleton, because running previews and mirrored buffers outlive any one command.
        serviceCollection.AddSingleton<PreviewService>();
    }
}
