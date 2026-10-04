namespace TuiLivePreview.VisualStudio.Core;

/// <summary>The three ways a preview can be launched; they map 1:1 onto the tool's own flags.</summary>
internal enum PreviewMode
{
    Render,
    Watch,
    Interactive,
}

/// <summary>
/// How the <c>tuipreview</c> tool will be invoked: as an executable (a build, or the dotnet tool
/// on PATH), as a framework-dependent dll run by <c>dotnet</c> (the copy bundled with the
/// extension), or through <c>dotnet run</c> on its project.
/// </summary>
internal abstract record Launcher
{
    public sealed record Exe(string Command) : Launcher;

    public sealed record Dll(string Path) : Launcher;

    public sealed record DotnetRun(string Project) : Launcher;

    /// <summary>The program and the arguments that come before the tool's own.</summary>
    public (string Program, IReadOnlyList<string> Prefix) Head => this switch
    {
        Exe exe => (exe.Command, []),
        Dll dll => ("dotnet", [dll.Path]),
        DotnetRun run => ("dotnet", ["run", "--project", run.Project, "--"]),
        _ => throw new InvalidOperationException($"unknown launcher {GetType().Name}"),
    };

    public string Describe() => this switch
    {
        Exe exe => exe.Command,
        Dll dll => $"dotnet {dll.Path}",
        DotnetRun run => $"dotnet run {run.Project}",
        _ => GetType().Name,
    };

    /// <summary>A launcher for a path to the tool, by what kind of file it is.</summary>
    public static Launcher For(string file)
    {
        if (file.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
        {
            return new DotnetRun(file);
        }

        // A dll is not executable by itself; it runs under the dotnet host.
        if (file.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
        {
            return new Dll(file);
        }

        return new Exe(file);
    }
}

/// <summary>A failure whose message is meant for the user as it stands.</summary>
internal sealed class PreviewException(string message) : Exception(message);

/// <summary>
/// Finds how to run the <c>tuipreview</c> tool.
/// <para>
/// Preference order: the view's own <c>clipath</c>, then the Tools &gt; Options setting, then a
/// build of the tool in a folder above the view, then <c>dotnet run</c> on the tool's project,
/// then the copy bundled with the extension, then the dotnet tool on PATH.
/// </para>
/// <para>
/// A build above the view wins over the bundled copy so that someone working on the tool previews
/// with their own changes. Visual Studio has no notion of a workspace folder the way VS Code does,
/// so "the repo" is found by walking up from the view, which is also more precise: a solution can
/// pull in views from anywhere, and the tool that belongs with a view is the one in its own tree.
/// </para>
/// <para>
/// The view's own project is never built. A <c>.tui.cs</c> depends on nothing but Terminal.Gui
/// and TuiControls, which the tool ships itself.
/// </para>
/// </summary>
internal static class ToolLocator
{
    public static Launcher Resolve(string view, string? viewCliPath, string? settingCliPath, string? bundledDir)
    {
        // The view's own path outranks the setting: a view that names its tool is saying "preview
        // me with this", which is a more specific claim than a machine-wide default.
        string configured = (viewCliPath ?? settingCliPath ?? string.Empty).Trim();
        if (configured.Length > 0)
        {
            if (!File.Exists(configured))
            {
                // Named differently depending on where it came from, because the fix differs: a
                // wrong setting is edited in Tools > Options, a wrong annotation in the view.
                throw new PreviewException(viewCliPath is not null
                    ? $"{Path.GetFileName(view)} names a clipath that does not exist: {configured}"
                    : $"The TUI Live Preview \"Tool path\" setting does not exist: {configured}");
            }

            return Launcher.For(configured);
        }

        var roots = Ancestors(Path.GetDirectoryName(Path.GetFullPath(view))!).ToList();

        foreach (string root in roots)
        {
            if (FindBuiltCli(root) is { } built)
            {
                return Launcher.For(built);
            }
        }

        foreach (string root in roots)
        {
            string project = Path.Combine(root, "cli", "TuiPreview.Cli.csproj");
            if (File.Exists(project))
            {
                return new Launcher.DotnetRun(project);
            }
        }

        // Published into tool/ by install-vs-ext.ps1 before packaging, so it ships in the VSIX.
        if (bundledDir is not null)
        {
            string bundled = Path.Combine(bundledDir, "tool", "tuipreview.dll");
            if (File.Exists(bundled))
            {
                return new Launcher.Dll(bundled);
            }
        }

        // `dotnet tool install -g TuiPreview.Cli` puts it on PATH. If it is not there either, the
        // console window says so, which is clearer than anything we could guess at here.
        return new Launcher.Exe("tuipreview");
    }

    /// <summary>The directory and every directory above it, nearest first.</summary>
    public static IEnumerable<string> Ancestors(string dir)
    {
        for (DirectoryInfo? current = new(dir); current is not null; current = current.Parent)
        {
            yield return current.FullName;
        }
    }

    /// <summary>
    /// The version in a target framework folder name, so net10.0 sorts above net8.0. As text it
    /// would not, and a stale net8.0 build would win over the current one.
    /// </summary>
    private static Version FrameworkVersion(string tfm)
    {
        var match = System.Text.RegularExpressions.Regex.Match(tfm, @"^net(\d+)\.(\d+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return match.Success
            ? new Version(int.Parse(match.Groups[1].Value), int.Parse(match.Groups[2].Value))
            : new Version(0, 0);
    }

    /// <summary>
    /// Looks for cli/bin/&lt;config&gt;/&lt;tfm&gt;/tuipreview.exe.
    /// <para>
    /// Debug is preferred over Release because that is what someone iterating on the tool wants,
    /// and the newest target framework first so a multi-targeted build does not resolve to a
    /// stale output.
    /// </para>
    /// </summary>
    public static string? FindBuiltCli(string root)
    {
        string binRoot = Path.Combine(root, "cli", "bin");
        if (!Directory.Exists(binRoot))
        {
            return null;
        }

        IEnumerable<string> configs = ["Debug", "Release"];
        try
        {
            configs = configs.Concat(Directory.GetDirectories(binRoot).Select(d => Path.GetFileName(d)));
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        foreach (string config in configs.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            string configDir = Path.Combine(binRoot, config);
            if (!Directory.Exists(configDir))
            {
                continue;
            }

            string[] tfms;
            try
            {
                tfms = Directory.GetDirectories(configDir)
                    .Select(d => Path.GetFileName(d))
                    .Where(d => d.StartsWith("net", StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(FrameworkVersion)
                    .ToArray();
            }
            catch (IOException)
            {
                continue;
            }

            foreach (string tfm in tfms)
            {
                string exe = Path.Combine(configDir, tfm, "tuipreview.exe");
                if (File.Exists(exe))
                {
                    return exe;
                }

                string dll = Path.Combine(configDir, tfm, "tuipreview.dll");
                if (File.Exists(dll))
                {
                    return dll;
                }
            }
        }

        return null;
    }
}
