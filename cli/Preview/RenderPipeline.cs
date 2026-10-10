using System.Collections.Immutable;
using System.Reflection;
using System.Runtime.Loader;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Emit;
using Terminal.Gui.App;
using Terminal.Gui.Drivers;
using Terminal.Gui.ViewBase;

namespace TuiPreview.Cli.Preview;

/// <summary>
/// Compiles a view file in memory, loads it into a collectible context and renders it:
///   .tui.cs -> Roslyn Emit -> AssemblyLoadContext -> Terminal.Gui -> string
/// No <c>dotnet build</c> of a throwaway project, no temp files on disk.
/// </summary>
/// <remarks>
/// This is our own harness, not a copy of upstream's <c>AppTestHelper</c>. That type reaches for
/// <c>ApplicationImpl</c>, <c>IOutput</c> and <c>SizeMonitorImpl</c>, all of which are
/// <c>internal</c> and only reachable through its <c>InternalsVisibleTo</c> grant to
/// "UnitTests.Parallelizable". Everything used here is public API, so we do not depend on it.
/// </remarks>
internal static class RenderPipeline
{
    /// <summary>Result of one preview attempt: either a rendered screen or the reason we could not produce one.</summary>
    public sealed record Result(
        bool Success,
        string PlainText,
        string Ansi,
        ImmutableArray<Diagnostic> GeneratorDiagnostics,
        ImmutableArray<Diagnostic> CompileDiagnostics)
    {
        public IEnumerable<Diagnostic> AllDiagnostics =>
            GeneratorDiagnostics.Concat(CompileDiagnostics);

        /// <summary>True when nothing reached the compiler. No step produces these today.</summary>
        public bool HasNoGeneratedCode =>
            GeneratorDiagnostics.Any(d => d.Severity == DiagnosticSeverity.Error);
    }

    /// <summary>
    /// A compiled, instantiated view plus the diagnostics from building it. This is what the
    /// interactive host embeds; <see cref="Run"/> is the string-rendering wrapper over it.
    /// </summary>
    public sealed record BuildResult(
        bool Success,
        IRunnable? Runnable,
        ImmutableArray<Diagnostic> GeneratorDiagnostics,
        ImmutableArray<Diagnostic> CompileDiagnostics)
    {
        public IEnumerable<Diagnostic> AllDiagnostics =>
            GeneratorDiagnostics.Concat(CompileDiagnostics);

        public bool HasNoGeneratedCode =>
            GeneratorDiagnostics.Any(d => d.Severity == DiagnosticSeverity.Error);
    }

    /// <summary>
    /// Builds the layout half of a view, the way TerminalGuiDesigner builds a .Designer.cs.
    /// <para>
    /// A view is split in two, like a designer file and its code-behind. <c>X.tui.cs</c> holds
    /// <c>partial class X</c> with its controls and an <c>InitializeComponent()</c> that
    /// composes them, and uses nothing but the framework, Terminal.Gui and TuiControls.
    /// <c>X.cs</c> holds the real constructor, which calls <c>InitializeComponent()</c> and then
    /// wires up the logic with whatever dependencies the app has. Only the first half is
    /// compiled here, together with a stub constructor that does nothing but lay out, so the
    /// preview never needs the app, its packages or its build.
    /// </para>
    /// <para>
    /// A self-contained view that declares its own parameterless constructor gets no stub and is
    /// compiled as it is.
    /// </para>
    /// </summary>
    /// <param name="sourcePath">The .tui.cs file defining the view.</param>
    /// <param name="className">Fully qualified name of the class to instantiate.</param>
    /// <param name="extraSourcePaths">
    /// Further files compiled into the same assembly, against the same references: a layout
    /// split out of a large view, or a small control the view is being written alongside.
    /// </param>
    /// <param name="extraReferences">
    /// Assemblies added to <see cref="DefaultReferences"/>, for a view written against a control
    /// library beyond Terminal.Gui and TuiControls. A dll, or a directory holding one.
    /// </param>
    public static BuildResult BuildClass(string sourcePath, string className,
        IReadOnlyList<string> extraSourcePaths, IReadOnlyList<string>? extraReferences = null)
    {
        var parseOptions = new CSharpParseOptions(LanguageVersion.Latest);
        SyntaxTree view = CSharpSyntaxTree.ParseText(
            File.ReadAllText(sourcePath), parseOptions, path: sourcePath);
        var trees = new List<SyntaxTree>
        {
            view,
            // What ImplicitUsings gives a Microsoft.NET.Sdk project. A view written in such a
            // project leans on these, and in one without them they are merely unused.
            CSharpSyntaxTree.ParseText(ImplicitUsings, parseOptions, path: "TuiPreview.GlobalUsings.g.cs"),
        };

        foreach (string extra in extraSourcePaths)
        {
            trees.Add(CSharpSyntaxTree.ParseText(
                File.ReadAllText(extra), parseOptions, path: extra));
        }

        if (ConstructorStub(view, className) is { } stub)
        {
            trees.Add(CSharpSyntaxTree.ParseText(stub, parseOptions, path: "TuiPreview.Constructor.g.cs"));
        }

        return CompileAndInstantiate(trees, className, extraReferences);
    }

    /// <summary>
    /// The stand-in for the logic half: a constructor that only calls
    /// <c>InitializeComponent()</c>, as the designer's <c>GetGenerateNewViewCode</c> writes it.
    /// Null when the class has no <c>InitializeComponent()</c> to call, or already has a
    /// parameterless constructor of its own.
    /// </summary>
    private static string? ConstructorStub(SyntaxTree view, string className)
    {
        int dot = className.LastIndexOf('.');
        string ns = dot < 0 ? string.Empty : className[..dot];
        string name = className[(dot + 1)..];

        ClassDeclarationSyntax? declaration = view.GetRoot()
            .DescendantNodes()
            .OfType<ClassDeclarationSyntax>()
            .FirstOrDefault(c => c.Identifier.ValueText == name);

        if (declaration is null)
        {
            return null;
        }

        bool hasInitialize = declaration.Members
            .OfType<MethodDeclarationSyntax>()
            .Any(m => m.Identifier.ValueText == "InitializeComponent" && m.ParameterList.Parameters.Count == 0);

        bool hasConstructor = declaration.Members
            .OfType<ConstructorDeclarationSyntax>()
            .Any(c => c.ParameterList.Parameters.Count == 0 && !c.Modifiers.Any(SyntaxKind.StaticKeyword));

        if (!hasInitialize || hasConstructor)
        {
            return null;
        }

        // No modifiers and no base list: a partial part may leave both to the other parts.
        string type = $"partial class {name} {{ public {name}() {{ InitializeComponent(); }} }}";
        return ns.Length == 0 ? type : $"namespace {ns} {{ {type} }}";
    }

    private const string ImplicitUsings = """
        global using global::System;
        global using global::System.Collections.Generic;
        global using global::System.IO;
        global using global::System.Linq;
        global using global::System.Net.Http;
        global using global::System.Threading;
        global using global::System.Threading.Tasks;
        """;

    /// <summary>
    /// Renders an already-built view to characters, as plain text and as ANSI.
    /// <para>
    /// Separate from <see cref="BuildClass"/> so that a caller holding a
    /// <see cref="BuildResult"/> can render it without going back through a build.
    /// </para>
    /// </summary>
    public static Result Capture(IRunnable runnable, int width, int height)
    {
        // One capture, not one per field: CaptureFrame runs a real main-loop iteration, so
        // calling it twice would run the application twice and render two separate frames.
        (string plain, string ansi) = CaptureFrame(runnable, width, height);

        return new Result(
            Success: true,
            PlainText: plain,
            Ansi: ansi,
            GeneratorDiagnostics: ImmutableArray<Diagnostic>.Empty,
            CompileDiagnostics: ImmutableArray<Diagnostic>.Empty);
    }

    /// <summary>
    /// Compiles the given sources into a collectible context and instantiates
    /// <paramref name="className"/>.
    /// </summary>
    private static BuildResult CompileAndInstantiate(
        List<SyntaxTree> trees, string className, IReadOnlyList<string>? extraReferences)
    {
        var genDiags = ImmutableArray<Diagnostic>.Empty;

        CSharpCompilation compilation = CSharpCompilation.Create(
            // Fixed, so a diagnostic or a stack trace always names the preview the same way.
            assemblyName: "TuiPreview",
            syntaxTrees: trees,
            references: DefaultReferences(extraReferences),
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
                // Annotations: a view may write string? whether or not its project enables
                // nullable, and the log is not flooded with flow warnings it would not get there.
                .WithNullableContextOptions(NullableContextOptions.Annotations)
                // CS0436: a type in source also exists in a referenced assembly. That is the
                // point when previewing a control from TuiControls itself - the source copy
                // wins, so the preview shows the edit rather than the last build.
                .WithSpecificDiagnosticOptions(new Dictionary<string, ReportDiagnostic>
                {
                    ["CS0436"] = ReportDiagnostic.Suppress,
                }));

        using var peStream = new MemoryStream();
        EmitResult emit = compilation.Emit(peStream);

        ImmutableArray<Diagnostic> compileDiags = emit.Diagnostics
            .Where(d => d.Severity is DiagnosticSeverity.Error or DiagnosticSeverity.Warning)
            .ToImmutableArray();

        if (!emit.Success)
        {
            return new BuildResult(false, null, genDiags, compileDiags);
        }

        // --- load and instantiate ---
        peStream.Position = 0;
        // isCollectible: the context is intentionally NOT unloaded before the view is disposed.
        // The interactive host swaps views in and out, and unloading under a live view would
        // fault. The context is left to the GC once the host itself goes away.
        // The default context resolves Terminal.Gui, TuiControls and the framework, which the
        // tool has already loaded, so Terminal.Gui stays shared with the view.
        var alc = new AssemblyLoadContext("tui-preview", isCollectible: true);

        // An assembly the view asked for through --reference is referenced by the compiler but
        // loaded by nobody, and the default context only finds what the tool itself has loaded.
        // Without this the view compiles cleanly and then throws FileNotFoundException on the
        // first line that touches a type from that assembly, which reads like a bug in the view.
        // Resolved from the same paths the compiler was given, so the two cannot disagree.
        Dictionary<string, string> requested = new(StringComparer.OrdinalIgnoreCase);
        foreach (string path in ExpandReferences(extraReferences))
        {
            requested[Path.GetFileNameWithoutExtension(path)] = path;
        }

        if (requested.Count > 0)
        {
            alc.Resolving += (context, name) =>
            {
                // Only the assemblies the view asked for. Anything else is Terminal.Gui's own
                // dependency graph, which the default context has already, so returning null
                // defers to it rather than guessing at a path.
                return requested.TryGetValue(name.Name ?? string.Empty, out string? path)
                    ? context.LoadFromAssemblyPath(path)
                    : null;
            };
        }

        Assembly asm = alc.LoadFromStream(peStream);

        Type? type = asm.GetType(className);

        object? instance;
        try
        {
            instance = type is null ? null : Activator.CreateInstance(type);
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            // The view's constructor threw. Rethrow what it threw, not the reflection wrapper,
            // whose message ("Exception has been thrown by the target of an invocation") is all
            // the log would otherwise show.
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
            throw;
        }

        if (instance is not IRunnable runnable)
        {
            return new BuildResult(false, null, genDiags, compileDiags);
        }

        return new BuildResult(true, runnable, genDiags, compileDiags);
    }

    /// <summary>
    /// Runs a single main-loop iteration and returns the screen as plain text and as ANSI.
    /// Mirrors <c>AppTestHelper.AssertAnsiSnapshot</c>, minus the golden-file comparison.
    /// </summary>
    private static (string Plain, string Ansi) CaptureFrame(IRunnable runnable, int width, int height)
    {
        // Detach the driver from the real console, as Terminal.Gui's own offline renderer
        // (MarkdownView.RenderToAnsi) does. Attached, the ANSI driver sends a CSI 18t size query
        // at Init, and the console's reply is parsed during the first iteration and resized the
        // screen to the console's size, overriding SetScreenSize below: every frame came out at
        // the console's 120x30 whatever was requested. Detached, nothing is queried, nothing is
        // read from stdin, and no escape sequences leak into the captured stdout.
        // TerminalDevice caches its detection process-wide, which is harmless here because
        // render-once never shares a process with an interactive session.
        string? previousDisableIO = Environment.GetEnvironmentVariable("DisableRealDriverIO");
        Environment.SetEnvironmentVariable("DisableRealDriverIO", "1");

        try
        {
            using IApplication app = Application.Create();

            // Render one frame and stop, rather than entering the interactive loop.
            app.StopAfterFirstIteration = true;

            app.Init(DriverRegistry.Names.ANSI);

            // SetScreenSize, not the Cols/Rows setters. Assigning Cols/Rows only updates the
            // reported dimensions; the driver's backing screen buffer is a separate allocation, so
            // a view sized with Dim.Fill() laid out against the old buffer and the frame came back
            // cropped to it (a 24-row request produced 9 rows, clipping controls placed lower down).
            app.Driver!.SetScreenSize(width, height);

            app.Run(runnable);

            return (app.Driver.ToString(), app.Driver.ToAnsi());
        }
        finally
        {
            Environment.SetEnvironmentVariable("DisableRealDriverIO", previousDisableIO);
        }
    }

    /// <summary>
    /// What a <c>.tui.cs</c> may use: the shared framework, Terminal.Gui and TuiControls, the
    /// same short list TerminalGuiDesigner compiles its designer files against. A view that
    /// reaches for anything else fails with CS0246 here instead of quietly dragging the app's
    /// dependencies into its layout.
    /// <para>
    /// This is the default set. Anything the view asks for through <c>--reference</c> is added
    /// to it rather than replacing it, so a view written against a third-party control library
    /// still has Terminal.Gui and TuiControls available.
    /// </para>
    /// </summary>
    private static IEnumerable<MetadataReference> DefaultReferences(
        IReadOnlyList<string>? extraReferences = null)
    {
        // The framework only: the tool's own packages (Roslyn, CommandLineParser) are trusted
        // platform assemblies too, but they sit next to the tool, not in the runtime directory.
        string runtimeDir = Path.GetFullPath(
            System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory());

        if (AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") is string tpa)
        {
            foreach (string path in tpa.Split(Path.PathSeparator))
            {
                if (path.Length > 0 && File.Exists(path) &&
                    Path.GetFullPath(path).StartsWith(runtimeDir, StringComparison.OrdinalIgnoreCase))
                {
                    yield return ReferenceFor(path);
                }
            }
        }

        yield return ReferenceFor(typeof(View).Assembly.Location);
        yield return ReferenceFor(typeof(TuiControls.Splitter).Assembly.Location);

        // An assembly the view asked for beyond the default set. Emitted last so a duplicate of
        // Terminal.Gui or TuiControls is shadowed by the copy the tool itself loaded: the view
        // must bind to the same View type the tool instantiates it as, or the cast below fails.
        foreach (string path in ExpandReferences(extraReferences))
        {
            yield return ReferenceFor(path);
        }
    }

    /// <summary>
    /// The assemblies named by <c>--reference</c>, resolved to files that exist.
    /// <para>
    /// A directory is expanded by taking the dlls inside it, because that is how a NuGet package
    /// or a build output is normally pointed at. An empty or absent directory yields nothing
    /// rather than an error: the caller has already reported the path it could not use, and a
    /// second complaint here would only bury the first.
    /// </para>
    /// </summary>
    private static IEnumerable<string> ExpandReferences(IReadOnlyList<string>? references)
    {
        foreach (string reference in references ?? Array.Empty<string>())
        {
            if (reference.Length == 0)
            {
                continue;
            }

            string full = Path.GetFullPath(reference);

            if (Directory.Exists(full))
            {
                foreach (string dll in Directory.EnumerateFiles(full, "*.dll"))
                {
                    yield return dll;
                }
            }
            else if (File.Exists(full))
            {
                yield return full;
            }
        }
    }

    /// <summary>References already read, keyed by path and invalidated by a changed file.</summary>
    private static readonly Dictionary<string, (string Stamp, MetadataReference Reference)> ReferenceCache =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// A reference read into memory rather than mapped from disk, so no bin folder is held open
    /// and TuiControls can be rebuilt while the preview runs. Cached, because the framework alone
    /// is a hundred and fifty references and a live preview rebuilds on every keystroke.
    /// </summary>
    private static MetadataReference ReferenceFor(string path)
    {
        var info = new FileInfo(path);
        string stamp = $"{info.LastWriteTimeUtc.Ticks}:{info.Length}";

        lock (ReferenceCache)
        {
            if (ReferenceCache.TryGetValue(path, out var cached) && cached.Stamp == stamp)
            {
                return cached.Reference;
            }

            MetadataReference reference = MetadataReference.CreateFromImage(
                File.ReadAllBytes(path), filePath: path);
            ReferenceCache[path] = (stamp, reference);
            return reference;
        }
    }
}

