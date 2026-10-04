using TuiPreview.Cli;

namespace TuiLivePreview.VisualStudio.Core;

/// <summary>
/// The Tools &gt; Options values, read once per command.
/// <para>
/// Per-view settings live with the view (a <c>// @tuipreview</c> annotation or a
/// <c>.tui.json</c> sidecar). These are only the machine-wide defaults beneath them.
/// </para>
/// </summary>
internal sealed record EditorSettings
{
    public string CliPath { get; init; } = string.Empty;

    public string ClassName { get; init; } = string.Empty;

    public int Width { get; init; } = 80;

    public int Height { get; init; } = 24;

    public bool Ansi { get; init; }

    public bool Live { get; init; } = true;

    public bool Confirmation { get; init; }
}

/// <summary>
/// The decisions a preview needs that do not depend on Visual Studio: what the view states for
/// itself, which class to preview, and whether the files it names exist.
/// </summary>
internal static class PreviewPlanner
{
    public const string ViewSuffix = ".tui.cs";

    /// <summary>
    /// True when the path is a file the user can right-click to preview.
    /// <para>
    /// The <c>.tui.cs</c> suffix is what makes this matchable: a bare <c>.cs</c> would put every
    /// source file in scope, and the logic half of a view is exactly the file the preview must not
    /// compile.
    /// </para>
    /// </summary>
    public static bool IsView(string? file) =>
        file is not null && file.EndsWith(ViewSuffix, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Throws a message naming the setting when the view lists a file that does not exist.
    /// <para>
    /// Reported here rather than left to the tool's own file check, which cannot say which of the
    /// view's own settings asked for the missing file.
    /// </para>
    /// </summary>
    public static void Validate(string view, ViewSettings settings)
    {
        foreach (string file in settings.Extra)
        {
            if (!File.Exists(file))
            {
                throw new PreviewException(
                    $"{Path.GetFileName(view)} lists an extra file that does not exist: {file}. " +
                    "Check its \"extra\" annotation or .tui.json.");
            }
        }

        // A reference may be a dll or a directory of them, so both are accepted; anything else is
        // a typo that would otherwise preview a view that cannot compile.
        foreach (string reference in settings.References)
        {
            if (!File.Exists(reference) && !Directory.Exists(reference))
            {
                throw new PreviewException(
                    $"{Path.GetFileName(view)} lists a reference that does not exist: {reference}. " +
                    "Check its \"reference\" annotation or .tui.json.");
            }
        }
    }

    /// <summary>
    /// The class to preview without asking: the view's own settings, then the Tools &gt; Options
    /// override, then the namespace and class the file declares. Null when none of them yield
    /// anything, which is the only case in which the user is prompted.
    /// </summary>
    public static string? ClassName(ViewSettings settings, EditorSettings editor, string text)
    {
        if (!string.IsNullOrWhiteSpace(settings.Class))
        {
            return settings.Class;
        }

        if (!string.IsNullOrWhiteSpace(editor.ClassName))
        {
            return editor.ClassName.Trim();
        }

        return ViewSettings.InferClassNameFromText(text);
    }

    /// <summary>
    /// The settings init would write down: what a preview would use right now, which is what makes
    /// them safe defaults. Writing them changes nothing about how the view renders; it pins it.
    /// </summary>
    public static IReadOnlyList<(string Key, string Value)> Detected(
        ViewSettings settings, EditorSettings editor, string className) =>
    [
        ("class", className),
        ("width", (settings.Width ?? editor.Width).ToString(System.Globalization.CultureInfo.InvariantCulture)),
        ("height", (settings.Height ?? editor.Height).ToString(System.Globalization.CultureInfo.InvariantCulture)),
    ];

    /// <summary>
    /// A new sidecar holding <paramref name="entries"/>, with the optional keys as commented-out
    /// examples. The comments are the documentation: a sidecar is the place for the settings that
    /// do not fit one comment line, and nobody remembers their names.
    /// </summary>
    public static string SidecarText(string view, IEnumerable<(string Key, string Value)> entries)
    {
        var body = new System.Text.StringBuilder();
        body.Append("{\n");
        body.Append($"  // Preview settings for {Path.GetFileName(view)}, read by TUI Live Preview.\n");
        body.Append("  // A \"// @tuipreview key=value\" line in the view overrides any key here.\n");
        foreach (var (key, value) in entries)
        {
            // Numbers stay numbers, because both readers expect "width": 80.
            string json = key == "class" ? System.Text.Json.JsonSerializer.Serialize(value) : value;
            body.Append($"  \"{key}\": {json},\n");
        }

        body.Append("  // \"extra\": [\"Layout/Shared.cs\"],\n");
        body.Append("  // \"reference\": [\"../lib/Controls.dll\"],\n");
        body.Append("  // \"clipath\": \"../tools/tuipreview.dll\",\n");
        body.Append("}\n");
        return body.ToString();
    }

    /// <summary>
    /// A <c>// @tuipreview</c> line holding <paramref name="entries"/>. Values are quoted only when
    /// the annotation grammar needs it: an unquoted value ends at whitespace, a comma or a semicolon.
    /// </summary>
    public static string AnnotationLine(IEnumerable<(string Key, string Value)> entries, string newline) =>
        "// @tuipreview " + string.Join(" ", entries.Select(e =>
            e.Value.IndexOfAny([' ', '\t', ',', ';']) >= 0 ? $"{e.Key}=\"{e.Value}\"" : $"{e.Key}={e.Value}"))
        + newline;
}
