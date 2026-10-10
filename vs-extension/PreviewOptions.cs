using Microsoft.VisualStudio.Extensibility;
using Microsoft.VisualStudio.Extensibility.Settings;
using TuiLivePreview.VisualStudio.Core;

namespace TuiLivePreview.VisualStudio;

/// <summary>
/// The machine-wide settings, shown under Tools &gt; Options &gt; TUI Live Preview.
/// <para>
/// The same set as the VS Code extension's <c>tuiLivePreview.*</c> settings, under the same ids,
/// so the documentation for one reads true for the other.
/// </para>
/// </summary>
internal static class PreviewOptions
{
    [VisualStudioContribution]
    public static SettingCategory Category { get; } = new("tuiLivePreview", "TUI Live Preview")
    {
        Description = "Defaults for previews of .tui.cs views. A view can override most of them " +
            "for itself with a \"// @tuipreview\" annotation or a .tui.json sidecar.",
    };

    [VisualStudioContribution]
    public static Setting.String CliPath { get; } = new("cliPath", "Tool path", Category, defaultValue: string.Empty)
    {
        Description = "Path to the tuipreview tool: a tuipreview.exe, a tuipreview.dll (run with " +
            "dotnet), or a .csproj (run with dotnet run). Leave empty to use the copy bundled with " +
            "the extension, or a build of the tool in a folder above the view.",
    };

    [VisualStudioContribution]
    public static Setting.String ClassName { get; } = new("className", "Class name", Category, defaultValue: string.Empty)
    {
        Description = "Override for the class to preview. When empty, the class comes from the " +
            "view's own settings, and failing that from the namespace and class the .tui.cs " +
            "declares. You are only prompted if none of those yield anything.",
    };

    [VisualStudioContribution]
    public static Setting.Integer Width { get; } = new("width", "Render width", Category, defaultValue: 80)
    {
        Description = "Render width in columns. Ignored in interactive mode, which fills the " +
            "console, and overridden by a \"width\" in the view's own settings.",
        Minimum = 1,
    };

    [VisualStudioContribution]
    public static Setting.Integer Height { get; } = new("height", "Render height", Category, defaultValue: 24)
    {
        Description = "Render height in rows. Ignored in interactive mode, which fills the " +
            "console, and overridden by a \"height\" in the view's own settings.",
        Minimum = 1,
    };

    [VisualStudioContribution]
    public static Setting.Boolean Ansi { get; } = new("ansi", "ANSI colour", Category, defaultValue: false)
    {
        Description = "Emit ANSI colour escapes in rendered output (sets TUI_ANSI=1).",
    };

    [VisualStudioContribution]
    public static Setting.Boolean Live { get; } = new("live", "Preview unsaved changes", Category, defaultValue: true)
    {
        Description = "Preview the editor's buffer, not just what is on disk. The buffer is " +
            "mirrored to a scratch file that the preview watches, so edits appear without saving. " +
            "Turn this off when a rendered frame has to be reproducible from the saved file.",
    };

    [VisualStudioContribution]
    public static Setting.Boolean Confirmation { get; } = new("confirmation", "Ask before starting", Category, defaultValue: false)
    {
        Description = "Ask before starting a preview, so a mis-click cannot leave a watch process running.",
    };

    public static async Task<EditorSettings> ReadAsync(VisualStudioExtensibility extensibility, CancellationToken cancellationToken)
    {
        SettingValues values = await extensibility.Settings().ReadEffectiveValuesAsync(
            new[] { Category }, cancellationToken);

        return new EditorSettings
        {
            CliPath = values.ValueOrDefault(CliPath, string.Empty) ?? string.Empty,
            ClassName = values.ValueOrDefault(ClassName, string.Empty) ?? string.Empty,
            Width = values.ValueOrDefault(Width, 80),
            Height = values.ValueOrDefault(Height, 24),
            Ansi = values.ValueOrDefault(Ansi, false),
            Live = values.ValueOrDefault(Live, true),
            Confirmation = values.ValueOrDefault(Confirmation, false),
        };
    }
}
