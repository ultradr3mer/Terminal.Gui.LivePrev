using System.Collections.Immutable;
using System.Reflection;
using System.Runtime.Loader;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;
using Terminal.Gui.App;
using Terminal.Gui.Drivers;

namespace TuiPreview.Host.Preview;

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

        /// <summary>True when the generator produced no C# at all (parse/validation rejected the XAML).</summary>
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
    /// Builds a view from a plain C# file, with no XAML and no source generator involved.
    /// <para>
    /// The file is expected to be a complete class deriving from a Terminal.Gui top-level
    /// (<c>Window</c>, <c>Runnable</c>, or anything implementing <see cref="IRunnable"/>) with a
    /// public parameterless constructor, and it is compiled on its own. There is no generated
    /// half and no synthesised stub: the class does not have to be <c>partial</c>, and nothing is
    /// written to disk.
    /// </para>
    /// <para>
    /// This is the only build path left. The XAML generator, its test host and both XSDs were
    /// removed: the schema lagged the control list it was generated from, and an attribute it did
    /// not recognise was dropped with only a warning, so a view that looked right in the editor
    /// could silently be missing controls at runtime.
    /// </para>
    /// </summary>
    /// <param name="sourcePath">The .tui.cs file defining the view.</param>
    /// <param name="className">Fully qualified name of the class to instantiate.</param>
    /// <param name="extraSourcePaths">
    /// Further files compiled into the same assembly - a ViewModel, a partial half split out of a
    /// large view, or a shared control the view depends on. A single file is the common case and
    /// the default, but a view that references a type declared elsewhere cannot compile without
    /// this: the contract is one class, not one file.
    /// </param>
    /// <param name="project">
    /// The project the view belongs to: its references, usings and language settings. Null, or a
    /// context that could not be read, compiles against the host's own assemblies instead.
    /// </param>
    public static BuildResult BuildClass(string sourcePath, string className,
        IReadOnlyList<string>? extraSourcePaths = null, ProjectContext? project = null)
    {
        bool useProject = project is { IsHostFallback: false };

        var parseOptions = new CSharpParseOptions(
            useProject ? project!.LanguageVersion : LanguageVersion.Latest,
            preprocessorSymbols: useProject ? project!.PreprocessorSymbols : null);
        var trees = new List<SyntaxTree>
        {
            CSharpSyntaxTree.ParseText(
                File.ReadAllText(sourcePath), parseOptions, path: sourcePath)
        };

        foreach (string extra in extraSourcePaths ?? Array.Empty<string>())
        {
            trees.Add(CSharpSyntaxTree.ParseText(
                File.ReadAllText(extra), parseOptions, path: extra));
        }

        // The project's implicit and explicit <Using> items, exactly what the SDK would generate
        // into obj/*.GlobalUsings.g.cs. Without them a view that compiles in its project fails
        // here with CS0246 on the first bare EventHandler or List<T>.
        if (useProject && project!.GlobalUsingsSource.Length > 0)
        {
            trees.Add(CSharpSyntaxTree.ParseText(
                project.GlobalUsingsSource, parseOptions, path: "TuiPreview.GlobalUsings.g.cs"));
        }

        return CompileAndInstantiate(trees, className,
            project?.Diagnostics ?? ImmutableArray<Diagnostic>.Empty,
            useProject ? project : null);
    }

    /// <summary>
    /// Renders an already-built view to characters, as plain text and as ANSI.
    /// <para>
    /// The frame-capture half of <see cref="Run"/>, separated so that a caller holding a
    /// <see cref="BuildResult"/> from either entry point (the XAML generator or
    /// <see cref="BuildClass"/>) can render it without going back through a build.
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
    /// <para>
    /// Shared by the XAML and the C# entry points so the two cannot drift: identical reference
    /// set, identical load strategy, identical diagnostics filtering. The only thing that
    /// differs between them is which syntax trees arrive here.
    /// </para>
    /// </summary>
    private static BuildResult CompileAndInstantiate(
        List<SyntaxTree> trees, string className, ImmutableArray<Diagnostic> projectDiags,
        ProjectContext? project)
    {
        // Project diagnostics are warnings about the reference set, not generator output, so
        // they travel with the compile diagnostics and never make HasNoGeneratedCode true.
        var genDiags = ImmutableArray<Diagnostic>.Empty;

        IEnumerable<MetadataReference> references = project is null
            ? BuildReferences()
            : project.CompileReferences.Select(ReferenceFor);

        CSharpCompilation compilation = CSharpCompilation.Create(
            // Fixed on purpose: a project grants the preview its internals with
            // <InternalsVisibleTo Include="TuiPreview" />, which matches on this name.
            assemblyName: "TuiPreview",
            syntaxTrees: trees,
            references: references,
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
                .WithNullableContextOptions(project?.Nullable ?? NullableContextOptions.Disable)
                // CS0436: a type in source also exists in a referenced assembly. That is the
                // point when previewing a file of the project itself - the source copy wins, so
                // the preview shows the edit rather than the last build.
                .WithSpecificDiagnosticOptions(new Dictionary<string, ReportDiagnostic>
                {
                    ["CS0436"] = ReportDiagnostic.Suppress,
                }));

        using var peStream = new MemoryStream();
        EmitResult emit = compilation.Emit(peStream);

        ImmutableArray<Diagnostic> compileDiags = projectDiags.AddRange(emit.Diagnostics
            .Where(d => d.Severity is DiagnosticSeverity.Error or DiagnosticSeverity.Warning));

        if (!emit.Success)
        {
            return new BuildResult(false, null, genDiags, compileDiags);
        }

        // --- load and instantiate ---
        peStream.Position = 0;
        // isCollectible: the context is intentionally NOT unloaded before the view is disposed.
        // The interactive host swaps views in and out, and unloading under a live view would
        // fault. The context is left to the GC once the host itself goes away.
        var alc = new AssemblyLoadContext("tui-preview", isCollectible: true);

        // Resolving fires only after the default context has failed, so whatever the host
        // already runs - Terminal.Gui above all - stays shared with the view. Only the project's
        // own assemblies and its NuGet packages come from here. Loaded from bytes so the user's
        // bin folder is never locked and the project can be rebuilt while the preview runs.
        if (project is not null)
        {
            alc.Resolving += (context, name) =>
                name.Name is { } simple &&
                project.RuntimeAssemblies.TryGetValue(simple, out string? path) &&
                File.Exists(path)
                    ? context.LoadFromStream(new MemoryStream(File.ReadAllBytes(path)))
                    : null;
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

    /// <summary>
    /// The fallback reference set, used when the project could not be read: the running
    /// framework's assemblies plus Terminal.Gui - everything the host itself was built against.
    /// <para>
    /// The host's own assembly is included. A file under preview that also exists in it (the
    /// chrome, the splitter) does not conflict in a way that matters: Roslyn binds to the source
    /// copy and reports CS0436, which the compilation suppresses.
    /// </para>
    /// </summary>
    private static IEnumerable<MetadataReference> BuildReferences()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") is string tpa)
        {
            foreach (string path in tpa.Split(Path.PathSeparator))
            {
                if (path.Length > 0 && File.Exists(path) && seen.Add(path))
                {
                    yield return ReferenceFor(path);
                }
            }
        }

        // Terminal.Gui sits next to the running binary thanks to the PackageReference.
        string tui = Path.Combine(AppContext.BaseDirectory, "Terminal.Gui.dll");
        if (File.Exists(tui) && seen.Add(tui))
        {
            yield return ReferenceFor(tui);
        }
    }

    /// <summary>References already read, keyed by path and invalidated by a changed file.</summary>
    private static readonly Dictionary<string, (string Stamp, MetadataReference Reference)> ReferenceCache =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// A reference read into memory rather than mapped from disk, so the project's bin folder is
    /// never held open and the project can be rebuilt while the preview runs. Cached, because a
    /// project easily has two hundred references and a live preview rebuilds on every keystroke.
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

