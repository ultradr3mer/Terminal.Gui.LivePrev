using System.Text;

namespace TuiLivePreview.VisualStudio.Core;

/// <summary>
/// One preview, fully resolved: which file, which class, which tool, which size.
/// <para>
/// The counterpart of the tool's own <c>ResolvedPreview</c>. Everything here is decided before
/// anything is launched, so the command line in the log is the whole truth about what ran.
/// </para>
/// </summary>
internal sealed record PreviewRequest
{
    public required Launcher Launcher { get; init; }

    /// <summary>The .tui.cs the user picked, on disk.</summary>
    public required string View { get; init; }

    public required string ClassName { get; init; }

    /// <summary>
    /// The file the tool should actually read. Usually <see cref="View"/> itself; the scratch copy
    /// when the editor holds the view open, because the tool can only read from disk.
    /// </summary>
    public required string PreviewPath { get; init; }

    /// <summary>Further .cs files to compile with the view, from the view's own settings.</summary>
    public IReadOnlyList<string> ExtraFiles { get; init; } = [];

    /// <summary>Assemblies to add to the tool's default reference set.</summary>
    public IReadOnlyList<string> References { get; init; } = [];

    /// <summary>Render size. Interactive mode fills the console and ignores it.</summary>
    public int Width { get; init; } = 80;

    public int Height { get; init; } = 24;

    /// <summary>Whether the rendered output carries ANSI colour escapes (TUI_ANSI=1).</summary>
    public bool Ansi { get; init; }
}

/// <summary>Turns a <see cref="PreviewRequest"/> into the command that runs it.</summary>
internal static class PreviewCommand
{
    public static string Label(PreviewMode mode) => mode switch
    {
        PreviewMode.Interactive => "TUI Preview (interactive)",
        PreviewMode.Watch => "TUI Preview (watch)",
        _ => "TUI Preview",
    };

    /// <summary>The tool's arguments, unquoted, after the launcher's own prefix.</summary>
    public static List<string> Arguments(PreviewRequest request, PreviewMode mode)
    {
        var args = new List<string>(request.Launcher.Head.Prefix)
        {
            // PreviewPath, not View: this is the scratch copy when the editor holds the view, and
            // it is the only file the tool can actually read.
            "--view", request.PreviewPath,
            "--class", request.ClassName,
        };

        // Only when the compiled file is a scratch copy. The tool uses this for the window title,
        // which must name the real file rather than one with a content hash in it.
        if (!string.Equals(request.PreviewPath, request.View, StringComparison.OrdinalIgnoreCase))
        {
            args.Add("--display-name");
            args.Add(request.View);
        }

        // Passed explicitly rather than left to the tool, so that the logged command is the whole
        // truth about what was compiled, and so a missing file is reported by the extension, which
        // can name the setting that asked for it.
        if (request.ExtraFiles.Count > 0)
        {
            args.Add("--extra");
            args.AddRange(request.ExtraFiles);
        }

        // Additive: the tool always includes the framework, Terminal.Gui and TuiControls, so a view
        // written against a third-party control library keeps all of them.
        if (request.References.Count > 0)
        {
            args.Add("--reference");
            args.AddRange(request.References);
        }

        switch (mode)
        {
            case PreviewMode.Interactive:
                args.Add("--interactive");
                break;

            case PreviewMode.Watch:
                // Watch renders frames just like render does, so it takes the size too.
                args.Add("--watch");
                goto default;

            default:
                args.Add("--width");
                args.Add(request.Width.ToString(System.Globalization.CultureInfo.InvariantCulture));
                args.Add("--height");
                args.Add(request.Height.ToString(System.Globalization.CultureInfo.InvariantCulture));
                break;
        }

        return args;
    }

    /// <summary>The whole command line, quoted for Windows, as logged and as run.</summary>
    public static string CommandLine(PreviewRequest request, PreviewMode mode) =>
        string.Join(" ", new[] { request.Launcher.Head.Program }
            .Concat(Arguments(request, mode))
            .Select(Quote));

    /// <summary>
    /// The batch file that runs a preview in its own console window.
    /// <para>
    /// A console window rather than the Output window because <c>--interactive</c> needs a real
    /// console: it drives Terminal.Gui's driver, reads keys and owns the screen. Visual Studio has
    /// no API for its own terminal, so a window of our own is the only real console on offer, and
    /// render and watch use one too so all three modes look the same.
    /// </para>
    /// <para>
    /// A script rather than a <c>cmd /c "..."</c> line because cmd's quote stripping makes nested
    /// quoting unreliable the moment a path contains a space, and the environment and title would
    /// need yet another layer. In a script every line is parsed on its own.
    /// </para>
    /// </summary>
    public static string LaunchScript(PreviewRequest request, PreviewMode mode)
    {
        var script = new StringBuilder();
        script.AppendLine("@echo off");

        // The rendered screen is box-drawing characters, and the paths may not be ASCII either.
        script.AppendLine("chcp 65001 >nul");
        script.AppendLine($"title {EscapeBatchText($"{Label(mode)} - {Path.GetFileName(request.View)}")}");
        script.AppendLine($"set \"TUI_ANSI={(request.Ansi ? "1" : "0")}\"");
        script.AppendLine($"cd /d {Quote(Path.GetDirectoryName(request.View)!)}");
        script.AppendLine(EscapePercent(CommandLine(request, mode)));

        // A render is one frame that has to stay on screen to be read. Interactive and watch only
        // stop when asked to, so they keep the window open only when something went wrong;
        // otherwise a preview that fails to start would flash and vanish.
        script.AppendLine(mode == PreviewMode.Render ? "pause" : "if errorlevel 1 pause");
        return script.ToString();
    }

    /// <summary>
    /// The arguments that make <c>cmd.exe</c> run <paramref name="script"/>.
    /// <para>
    /// Not left to Windows' own handling of a .cmd file, which runs it as <c>cmd /c "path"</c>: cmd
    /// strips those quotes again whenever the path holds a character like <c>&amp;</c>, and the
    /// script then fails to start. With <c>/s</c> exactly the outer pair is stripped, so the path's
    /// own quotes survive. <c>/d</c> skips AutoRun commands, which have no place in a preview.
    /// </para>
    /// </summary>
    public static string ScriptArguments(string script) => $"/d /s /c \"\"{script}\"\"";

    /// <summary>
    /// Quotes one argument the way the C runtime and .NET split a Windows command line.
    /// <para>
    /// Paths routinely contain spaces ("C:\Users\First Last\..."), and a directory passed as a
    /// reference may end in a backslash, which before a closing quote would escape the quote and
    /// run the rest of the command line into one argument. Backslashes before a quote are doubled
    /// for that reason.
    /// </para>
    /// </summary>
    public static string Quote(string value)
    {
        if (value.Length > 0 && value.IndexOfAny([' ', '\t', '"', '&', '|', '<', '>', '^', '(', ')']) < 0)
        {
            return value;
        }

        var quoted = new StringBuilder("\"");
        int backslashes = 0;
        foreach (char c in value)
        {
            if (c == '\\')
            {
                backslashes++;
                continue;
            }

            if (c == '"')
            {
                quoted.Append('\\', backslashes * 2 + 1);
            }
            else
            {
                quoted.Append('\\', backslashes);
            }

            backslashes = 0;
            quoted.Append(c);
        }

        quoted.Append('\\', backslashes * 2);
        quoted.Append('"');
        return quoted.ToString();
    }

    /// <summary>In a batch file a single % starts a variable, even inside quotes.</summary>
    private static string EscapePercent(string line) => line.Replace("%", "%%");

    /// <summary>Text outside quotes, such as the title: cmd's operators need a caret.</summary>
    private static string EscapeBatchText(string text)
    {
        var escaped = new StringBuilder();
        foreach (char c in text)
        {
            if (c is '^' or '&' or '|' or '<' or '>' or '(' or ')')
            {
                escaped.Append('^');
            }

            escaped.Append(c == '%' ? "%%" : c.ToString());
        }

        return escaped.ToString();
    }
}
