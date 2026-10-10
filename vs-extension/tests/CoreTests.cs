using System.Diagnostics;
using TuiLivePreview.VisualStudio.Core;
using TuiPreview.Cli;
using Xunit;

namespace TuiLivePreview.VisualStudio.Tests;

/// <summary>A scratch directory per test, removed afterwards.</summary>
public sealed class TempDir : IDisposable
{
    public TempDir(string name = "tui test")
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"{name} {Guid.NewGuid():N}");
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string Write(string relative, string text = "")
    {
        string file = System.IO.Path.GetFullPath(System.IO.Path.Combine(Path, relative));
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(file)!);
        File.WriteAllText(file, text);
        return file;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}

public class QuoteTests
{
    [Theory]
    [InlineData("plain", "plain")]
    [InlineData(@"C:\Users\First Last\v.tui.cs", @"""C:\Users\First Last\v.tui.cs""")]
    // A trailing backslash would otherwise escape the closing quote and swallow what follows.
    [InlineData(@"C:\lib dir\", @"""C:\lib dir\\""")]
    [InlineData(@"say ""hi""", @"""say \""hi\""""")]
    [InlineData("", @"""""")]
    [InlineData("a&b", @"""a&b""")]
    public void Quotes_for_the_windows_command_line(string value, string expected) =>
        Assert.Equal(expected, PreviewCommand.Quote(value));
}

public class ArgumentTests
{
    private static PreviewRequest Request(string previewPath = @"C:\v\Main.tui.cs") => new()
    {
        Launcher = new Launcher.Dll(@"C:\tool\tuipreview.dll"),
        View = @"C:\v\Main.tui.cs",
        ClassName = "App.Main",
        PreviewPath = previewPath,
        Width = 120,
        Height = 40,
    };

    [Fact]
    public void Render_passes_the_size()
    {
        var args = PreviewCommand.Arguments(Request(), PreviewMode.Render);
        Assert.Equal(
            [@"C:\tool\tuipreview.dll", "--view", @"C:\v\Main.tui.cs", "--class", "App.Main", "--width", "120", "--height", "40"],
            args);
    }

    [Fact]
    public void Watch_passes_the_size_too()
    {
        var args = PreviewCommand.Arguments(Request(), PreviewMode.Watch);
        Assert.Contains("--watch", args);
        Assert.Contains("--width", args);
    }

    [Fact]
    public void Interactive_fills_the_console()
    {
        var args = PreviewCommand.Arguments(Request(), PreviewMode.Interactive);
        Assert.Contains("--interactive", args);
        Assert.DoesNotContain("--width", args);
    }

    [Fact]
    public void A_scratch_copy_carries_the_real_name_for_the_title()
    {
        var args = PreviewCommand.Arguments(Request(@"C:\tmp\Main-abc.tui.cs"), PreviewMode.Render);
        int index = args.IndexOf("--display-name");
        Assert.True(index > 0);
        Assert.Equal(@"C:\v\Main.tui.cs", args[index + 1]);
        Assert.Equal(@"C:\tmp\Main-abc.tui.cs", args[args.IndexOf("--view") + 1]);
    }

    [Fact]
    public void Extra_files_and_references_are_passed_explicitly()
    {
        var request = Request() with { ExtraFiles = [@"C:\v\A.cs", @"C:\v\B.cs"], References = [@"C:\lib\X.dll"] };
        var args = PreviewCommand.Arguments(request, PreviewMode.Render);
        Assert.Equal(@"C:\v\A.cs", args[args.IndexOf("--extra") + 1]);
        Assert.Equal(@"C:\v\B.cs", args[args.IndexOf("--extra") + 2]);
        Assert.Equal(@"C:\lib\X.dll", args[args.IndexOf("--reference") + 1]);
    }

    [Fact]
    public void Projects_are_passed_for_the_tool_to_resolve()
    {
        var request = Request() with { Projects = [@"C:\src\Controls\Controls.csproj"] };
        var args = PreviewCommand.Arguments(request, PreviewMode.Interactive);
        Assert.Equal(@"C:\src\Controls\Controls.csproj", args[args.IndexOf("--project") + 1]);
    }

    [Fact]
    public void Dotnet_run_separates_its_own_arguments_from_the_tools()
    {
        var request = Request() with { Launcher = new Launcher.DotnetRun(@"C:\r\cli\TuiPreview.Cli.csproj") };
        string line = PreviewCommand.CommandLine(request, PreviewMode.Render);
        Assert.StartsWith(@"dotnet run --project C:\r\cli\TuiPreview.Cli.csproj -- --view", line);
    }

    [Fact]
    public void The_script_sets_ansi_and_keeps_a_render_on_screen()
    {
        string render = PreviewCommand.LaunchScript(Request() with { Ansi = true }, PreviewMode.Render);
        Assert.Contains("set \"TUI_ANSI=1\"", render);
        Assert.Contains("\npause", render.Replace("\r\n", "\n"));

        string interactive = PreviewCommand.LaunchScript(Request(), PreviewMode.Interactive);
        Assert.Contains("set \"TUI_ANSI=0\"", interactive);
        Assert.Contains("if errorlevel 1 pause", interactive);
    }

    [Fact]
    public void Percent_signs_survive_the_batch_file()
    {
        var request = Request() with { View = @"C:\100% done\Main.tui.cs", PreviewPath = @"C:\100% done\Main.tui.cs" };
        string script = PreviewCommand.LaunchScript(request, PreviewMode.Render);
        Assert.Contains(@"""C:\100%% done\Main.tui.cs""", script);
        Assert.Contains(@"cd /d ""C:\100%% done""", script);
    }
}

public class ToolLocatorTests
{
    [Fact]
    public void A_views_own_clipath_wins_over_the_setting()
    {
        using var dir = new TempDir();
        string tool = dir.Write("tools/tuipreview.dll");
        string other = dir.Write("other/tuipreview.exe");
        string view = dir.Write("Main.tui.cs");

        var launcher = ToolLocator.Resolve(view, tool, other, bundledDir: null);
        Assert.Equal(new Launcher.Dll(tool), launcher);
    }

    [Fact]
    public void A_missing_clipath_names_where_it_came_from()
    {
        using var dir = new TempDir();
        string view = dir.Write("Main.tui.cs");

        var fromView = Assert.Throws<PreviewException>(() =>
            ToolLocator.Resolve(view, Path.Combine(dir.Path, "nope.dll"), null, null));
        Assert.Contains("Main.tui.cs names a clipath", fromView.Message);

        var fromSetting = Assert.Throws<PreviewException>(() =>
            ToolLocator.Resolve(view, null, Path.Combine(dir.Path, "nope.dll"), null));
        Assert.Contains("\"Tool path\" setting", fromSetting.Message);
    }

    [Fact]
    public void A_build_above_the_view_wins_over_the_bundled_copy()
    {
        using var dir = new TempDir();
        string built = dir.Write("cli/bin/Debug/net10.0/tuipreview.exe");
        dir.Write("ext/tool/tuipreview.dll");
        string view = dir.Write("samples/App/Views/Main.tui.cs");

        var launcher = ToolLocator.Resolve(view, null, null, Path.Combine(dir.Path, "ext"));
        Assert.Equal(new Launcher.Exe(built), launcher);
    }

    [Fact]
    public void The_project_is_run_when_there_is_no_build()
    {
        using var dir = new TempDir();
        string project = dir.Write("cli/TuiPreview.Cli.csproj");
        string view = dir.Write("samples/Main.tui.cs");

        Assert.Equal(new Launcher.DotnetRun(project), ToolLocator.Resolve(view, null, null, null));
    }

    [Fact]
    public void The_bundled_copy_then_the_path()
    {
        using var dir = new TempDir();
        string view = dir.Write("Main.tui.cs");
        Assert.Equal(new Launcher.Exe("tuipreview"), ToolLocator.Resolve(view, null, null, Path.Combine(dir.Path, "ext")));

        string bundled = dir.Write("ext/tool/tuipreview.dll");
        Assert.Equal(new Launcher.Dll(bundled), ToolLocator.Resolve(view, null, null, Path.Combine(dir.Path, "ext")));
    }

    [Fact]
    public void Debug_is_preferred_and_the_newest_framework_first()
    {
        using var dir = new TempDir();
        dir.Write("cli/bin/Release/net10.0/tuipreview.exe");
        dir.Write("cli/bin/Debug/net8.0/tuipreview.exe");
        string newest = dir.Write("cli/bin/Debug/net10.0/tuipreview.dll");

        // As text "net8.0" sorts above "net10.0", which would pick the stale build.
        Assert.Equal(newest, ToolLocator.FindBuiltCli(dir.Path));
    }
}

public class PlannerTests
{
    [Fact]
    public void The_unsaved_annotation_is_the_one_that_applies()
    {
        using var dir = new TempDir();
        string view = dir.Write("Main.tui.cs", "// @tuipreview class=Saved.Name\nclass Main {}");

        var settings = ViewSettings.Read(view, "// @tuipreview class=Unsaved.Name width=100\nclass Main {}");
        Assert.Equal("Unsaved.Name", PreviewPlanner.ClassName(settings, new EditorSettings(), string.Empty));
        Assert.Equal(100, settings.Width);
    }

    [Fact]
    public void The_annotation_overrides_the_sidecar_and_says_so()
    {
        using var dir = new TempDir();
        dir.Write("Main.tui.json", "{ \"class\": \"From.Sidecar\", \"height\": 30, }");
        string view = dir.Write("Main.tui.cs", "// @tuipreview class=From.Annotation\nclass Main {}");

        var settings = ViewSettings.Read(view, File.ReadAllText(view));
        Assert.Equal("From.Annotation", settings.Class);
        Assert.Equal(30, settings.Height);
        Assert.Contains(settings.Warnings, w => w.Contains("overrides \"From.Sidecar\""));
    }

    [Fact]
    public void The_class_falls_back_to_the_setting_then_the_declaration()
    {
        using var dir = new TempDir();
        string text = "namespace App.Views;\npublic sealed partial class Main : Window {}";
        string view = dir.Write("Main.tui.cs", text);
        var settings = ViewSettings.Read(view, text);

        Assert.Equal("From.Setting", PreviewPlanner.ClassName(settings, new EditorSettings { ClassName = " From.Setting " }, text));
        Assert.Equal("App.Views.Main", PreviewPlanner.ClassName(settings, new EditorSettings(), text));
        Assert.Null(PreviewPlanner.ClassName(settings, new EditorSettings(), "// class Commented {}"));
    }

    [Fact]
    public void A_missing_extra_file_names_the_setting()
    {
        using var dir = new TempDir();
        string text = "// @tuipreview extra=Missing.cs\nclass Main {}";
        string view = dir.Write("Main.tui.cs", text);

        var error = Assert.Throws<PreviewException>(() => PreviewPlanner.Validate(view, ViewSettings.Read(view, text)));
        Assert.Contains("extra file that does not exist", error.Message);
    }

    [Fact]
    public void Projects_resolve_against_the_view_and_must_exist()
    {
        using var dir = new TempDir();
        string project = dir.Write("Controls/Controls.csproj", "<Project />");
        string text = "// @tuipreview project=../Controls/Controls.csproj\nclass Main {}";
        string view = dir.Write("App/Main.tui.cs", text);

        var settings = ViewSettings.Read(view, text);
        Assert.Equal(project, Path.GetFullPath(settings.Projects.Single()));
        PreviewPlanner.Validate(view, settings);

        string missing = "// @tuipreview project=Nope.csproj\nclass Main {}";
        var error = Assert.Throws<PreviewException>(() => PreviewPlanner.Validate(view, ViewSettings.Read(view, missing)));
        Assert.Contains("project that does not exist", error.Message);
    }

    [Fact]
    public void A_sidecar_takes_projects_as_a_string_or_a_list()
    {
        using var dir = new TempDir();
        dir.Write("Main.tui.json", "{ \"project\": [\"a/A.csproj\", \"b/B.csproj\"] }");
        string view = dir.Write("Main.tui.cs", "class Main {}");
        Assert.Equal(2, ViewSettings.Read(view, "class Main {}").Projects.Count);

        dir.Write("Main.tui.json", "{ \"project\": \"a/A.csproj\" }");
        Assert.Single(ViewSettings.Read(view, "class Main {}").Projects);
    }

    [Fact]
    public void An_upper_case_view_still_finds_its_sidecar()
    {
        using var dir = new TempDir();
        string sidecar = dir.Write("Main.tui.json", "{ \"class\": \"From.Sidecar\" }");
        string view = dir.Write("Main.TUI.CS", "class Main {}");

        Assert.True(PreviewPlanner.IsView(view));
        Assert.Equal(sidecar, ViewSettings.SidecarPathFor(view));
        Assert.Equal("From.Sidecar", ViewSettings.Read(view, "class Main {}").Class);
        Assert.Null(ViewSettings.SidecarPathFor(Path.Combine(dir.Path, "Main.tui.cs.bak")));
    }

    [Fact]
    public void A_reference_may_be_a_directory()
    {
        using var dir = new TempDir();
        Directory.CreateDirectory(Path.Combine(dir.Path, "lib"));
        string text = "// @tuipreview reference=lib\nclass Main {}";
        string view = dir.Write("Main.tui.cs", text);

        PreviewPlanner.Validate(view, ViewSettings.Read(view, text));
    }

    [Fact]
    public void Init_writes_only_what_the_view_does_not_state()
    {
        using var dir = new TempDir();
        dir.Write("Main.tui.json", "{ \"width\": 132 }");
        string text = "namespace App;\nclass Main {}";
        string view = dir.Write("Main.tui.cs", text);
        var settings = ViewSettings.Read(view, text);

        var detected = PreviewPlanner.Detected(settings, new EditorSettings(), "App.Main");
        Assert.Equal([("class", "App.Main"), ("width", "132"), ("height", "24")], detected);

        var missing = detected.Where(d => !settings.States(d.Key)).Select(d => d.Key);
        Assert.Equal(["class", "height"], missing);
    }

    [Fact]
    public void An_initialised_sidecar_reads_back()
    {
        using var dir = new TempDir();
        string view = dir.Write("Main.tui.cs", "class Main {}");
        File.WriteAllText(
            ViewSettings.SidecarPathFor(view)!,
            PreviewPlanner.SidecarText(view, [("class", "App.Main"), ("width", "100")]));

        var settings = ViewSettings.Read(view, "class Main {}");
        Assert.Empty(settings.Warnings);
        Assert.Equal("App.Main", settings.Class);
        Assert.Equal(100, settings.Width);
    }

    [Fact]
    public void An_initialised_annotation_reads_back()
    {
        string line = PreviewPlanner.AnnotationLine([("class", "App.Main"), ("extra", "With Spaces.cs")], "\n");
        Assert.Equal("// @tuipreview class=App.Main extra=\"With Spaces.cs\"\n", line);

        using var dir = new TempDir();
        dir.Write("With Spaces.cs");
        string view = dir.Write("Main.tui.cs");
        var settings = ViewSettings.Read(view, line + "class Main {}");
        Assert.Equal("App.Main", settings.Class);
        Assert.Equal(Path.Combine(dir.Path, "With Spaces.cs"), settings.Extra.Single());
        Assert.True(ViewSettings.HasAnnotation(line));
    }

    [Theory]
    [InlineData(@"C:\a\Main.tui.cs", true)]
    [InlineData(@"C:\a\Main.TUI.CS", true)]
    [InlineData(@"C:\a\Main.cs", false)]
    [InlineData(@"C:\a\Main.tui.json", false)]
    public void Only_tui_cs_files_are_views(string file, bool expected) =>
        Assert.Equal(expected, PreviewPlanner.IsView(file));
}

public class ScratchTests
{
    [Fact]
    public void One_stable_copy_per_view()
    {
        using var dir = new TempDir();
        var scratch = new ScratchFiles(dir.Path);

        string first = scratch.Mirror(@"C:\a\Main.tui.cs", "one");
        string again = scratch.Mirror(@"C:\a\Main.tui.cs", "two");
        string other = scratch.PathFor(@"C:\b\Main.tui.cs");

        Assert.Equal(first, again);
        Assert.NotEqual(first, other);
        Assert.EndsWith(".tui.cs", first);
        Assert.StartsWith("Main-", Path.GetFileName(first));
        Assert.Equal("two", File.ReadAllText(first));
        Assert.True(scratch.IsMirrored(@"C:\a\Main.tui.cs"));
        Assert.False(scratch.IsMirrored(@"C:\c\Main.tui.cs"));
    }
}

/// <summary>
/// Runs a generated launch script against a real build of the tool, from a folder whose name has
/// a space, an ampersand and a percent sign in it: the characters that break batch files.
/// </summary>
public class LaunchScriptTests
{
    private static string? RepoRoot()
    {
        foreach (string dir in ToolLocator.Ancestors(AppContext.BaseDirectory))
        {
            if (File.Exists(Path.Combine(dir, "cli.slnx")))
            {
                return dir;
            }
        }

        return null;
    }

    [Fact]
    public void A_render_runs_from_an_awkward_path()
    {
        string? root = RepoRoot();
        string? tool = root is null ? null : ToolLocator.FindBuiltCli(root);
        if (tool is null)
        {
            // Needs `dotnet build cli.slnx` first; install-vs-ext.ps1 always builds it.
            return;
        }

        // %TEMP% is a real variable, so an unescaped one would expand to somewhere else entirely.
        using var dir = new TempDir("tui A&B 100% %TEMP%");
        string view = Path.Combine(dir.Path, "Main View.tui.cs");
        File.Copy(Path.Combine(root!, "samples", "SampleApp", "MainView.tui.cs"), view);

        var request = new PreviewRequest
        {
            Launcher = Launcher.For(tool),
            View = view,
            ClassName = "SampleApp.MainView",
            PreviewPath = view,
            Width = 60,
            Height = 12,
        };

        // Where the extension puts it: beside the scratch copies, not beside the view.
        using var scripts = new TempDir("tui scripts");
        string script = Path.Combine(scripts.Path, "launch.cmd");
        File.WriteAllText(script, PreviewCommand.LaunchScript(request, PreviewMode.Render));

        // The same command line the extension uses, minus the window.
        var start = new ProcessStartInfo("cmd.exe", PreviewCommand.ScriptArguments(script))
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
        };

        using var process = Process.Start(start)!;
        process.StandardInput.Close(); // answers the closing pause
        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        Assert.True(process.WaitForExit(TimeSpan.FromMinutes(2)), "the render did not finish");

        string output = stdout.Result + stderr.Result;
        Assert.Contains("SampleApp.MainView", output);
        Assert.Contains(view, output);
        Assert.DoesNotContain("error:", output);
        Assert.DoesNotContain("cannot find the path", output);
    }
}
