using System.Collections.Immutable;
using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Terminal.Gui.ViewBase;

namespace TuiPreview.Host.Preview;

/// <summary>
/// What a view needs from the project it belongs to, so it compiles the way that project would
/// compile it: the references (framework, NuGet packages, project references, and the project's
/// own built assembly), the implicit and explicit <c>&lt;Using&gt;</c> items, and the
/// <c>Nullable</c> and <c>LangVersion</c> settings.
/// <para>
/// Read from MSBuild rather than reconstructed by hand, because MSBuild is the only thing that
/// resolves package versions, reference assemblies and project references the same way the real
/// build does. The query runs the <c>ResolveReferences</c> target and nothing more, so it neither
/// compiles the project nor builds its project references.
/// </para>
/// </summary>
internal sealed class ProjectContext
{
    /// <summary>Paths compiled against, in MSBuild's order.</summary>
    public ImmutableArray<string> CompileReferences { get; }

    /// <summary>
    /// Simple assembly name to the file that implements it at runtime. Consulted only after the
    /// host's default context fails, so anything the host already loaded stays shared.
    /// </summary>
    public IReadOnlyDictionary<string, string> RuntimeAssemblies { get; }

    /// <summary><c>global using</c> directives for the project's <c>&lt;Using&gt;</c> items.</summary>
    public string GlobalUsingsSource { get; }

    public NullableContextOptions Nullable { get; }

    public LanguageVersion LanguageVersion { get; }

    /// <summary>The project's <c>DefineConstants</c>, so <c>#if DEBUG</c> in a view matches the real build.</summary>
    public ImmutableArray<string> PreprocessorSymbols { get; }

    /// <summary>Problems reading the project. Warnings only: the preview still builds.</summary>
    public ImmutableArray<Diagnostic> Diagnostics { get; }

    /// <summary>
    /// True when the project could not be read. The pipeline then compiles against the host's own
    /// assemblies, exactly as it did before projects were read at all.
    /// </summary>
    public bool IsHostFallback { get; }

    private ProjectContext(
        ImmutableArray<string> compileReferences,
        IReadOnlyDictionary<string, string> runtimeAssemblies,
        string globalUsingsSource,
        NullableContextOptions nullable,
        LanguageVersion languageVersion,
        ImmutableArray<string> preprocessorSymbols,
        ImmutableArray<Diagnostic> diagnostics,
        bool isHostFallback)
    {
        CompileReferences = compileReferences;
        RuntimeAssemblies = runtimeAssemblies;
        GlobalUsingsSource = globalUsingsSource;
        Nullable = nullable;
        LanguageVersion = languageVersion;
        PreprocessorSymbols = preprocessorSymbols;
        Diagnostics = diagnostics;
        IsHostFallback = isHostFallback;
    }

    // Not an analyzer: the descriptor only labels diagnostics the host reports itself, so
    // analyzer release tracking (AnalyzerReleases.*.md) does not apply.
#pragma warning disable RS2008
    private static readonly DiagnosticDescriptor ProjectWarning = new(
        id: "TUIP001",
        title: "Project could not be fully read",
        messageFormat: "{0}",
        category: "TuiPreview",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true);
#pragma warning restore RS2008

    private static Diagnostic Warn(string message) =>
        Diagnostic.Create(ProjectWarning, Location.None, message);

    private static ProjectContext HostFallback(string reason) =>
        new(ImmutableArray<string>.Empty,
            new Dictionary<string, string>(),
            string.Empty,
            NullableContextOptions.Disable,
            LanguageVersion.Latest,
            ImmutableArray<string>.Empty,
            [Warn($"{reason} Compiling against the preview host's assemblies only.")],
            isHostFallback: true);

#pragma warning disable RS2008
    private static readonly DiagnosticDescriptor MetadataWarning = new(
        id: "TUIP002",
        title: "Build metadata missing",
        messageFormat: "{0}",
        category: "TuiPreview",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true);
#pragma warning restore RS2008

    /// <summary>What the SDK's ImplicitUsings adds for Microsoft.NET.Sdk, used when the app carries no metadata.</summary>
    private static readonly string[] SdkDefaultUsings =
    [
        "System", "System.Collections.Generic", "System.IO", "System.Linq",
        "System.Net.Http", "System.Threading", "System.Threading.Tasks",
    ];

    /// <summary>
    /// The context of the app this code runs in. The runtime has already resolved every
    /// reference from the app's .deps.json into the trusted platform assemblies, and the default
    /// load context loads them, so there is nothing to query and nothing to resolve. Usings and
    /// language settings come from the <c>AssemblyMetadata</c> that TuiPreview.Host.targets
    /// writes at build time.
    /// </summary>
    public static ProjectContext FromRunningApp(Assembly entry)
    {
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();

        var references = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string tpa = AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string ?? string.Empty;
        foreach (string path in tpa.Split(Path.PathSeparator).Append(entry.Location))
        {
            if (path.Length > 0 && File.Exists(path) && seen.Add(Path.GetFileNameWithoutExtension(path)))
            {
                references.Add(path);
            }
        }

        Dictionary<string, string?> metadata = entry
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .Where(a => a.Key.StartsWith("TuiPreview.", StringComparison.Ordinal))
            .GroupBy(a => a.Key)
            .ToDictionary(g => g.Key, g => g.Last().Value);

        var usings = new StringBuilder();
        if (metadata.TryGetValue("TuiPreview.Usings", out string? encoded))
        {
            // One entry per <Using> item: "Namespace|Alias|Static".
            foreach (string entryText in (encoded ?? string.Empty).Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                string[] parts = entryText.Split('|');
                usings.AppendLine(GlobalUsing(
                    parts[0].Trim(),
                    parts.Length > 1 ? parts[1].Trim() : string.Empty,
                    parts.Length > 2 && string.Equals(parts[2].Trim(), "true", StringComparison.OrdinalIgnoreCase)));
            }
        }
        else
        {
            diagnostics.Add(Diagnostic.Create(MetadataWarning, Location.None,
                $"{entry.GetName().Name} carries no TuiPreview build metadata, so its usings and language " +
                "settings are unknown. Import TuiPreview.Host.targets (a PackageReference does this itself). " +
                "Using the SDK's default implicit usings."));
            foreach (string ns in SdkDefaultUsings)
            {
                usings.AppendLine(GlobalUsing(ns, string.Empty, isStatic: false));
            }
        }

        NullableContextOptions nullable =
            Enum.TryParse(metadata.GetValueOrDefault("TuiPreview.Nullable"), ignoreCase: true, out NullableContextOptions n)
                ? n
                : NullableContextOptions.Disable;

        LanguageVersion languageVersion =
            LanguageVersionFacts.TryParse(metadata.GetValueOrDefault("TuiPreview.LangVersion"), out LanguageVersion lv)
                ? lv
                : LanguageVersion.Latest;

        return new ProjectContext(
            references.ToImmutableArray(),
            new Dictionary<string, string>(),
            usings.ToString(),
            nullable,
            languageVersion,
            SplitSymbols(metadata.GetValueOrDefault("TuiPreview.DefineConstants")),
            diagnostics.ToImmutable(),
            isHostFallback: false);
    }

    private static string GlobalUsing(string ns, string alias, bool isStatic) =>
        alias.Length > 0
            ? $"global using {alias} = global::{ns};"
            : isStatic
                ? $"global using static global::{ns};"
                : $"global using global::{ns};";

    private static ImmutableArray<string> SplitSymbols(string? defineConstants) =>
        (defineConstants ?? string.Empty)
            .Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToImmutableArray();

    /// <summary>Queries MSBuild for <paramref name="projectPath"/>. Never throws.</summary>
    public static ProjectContext Load(string projectPath)
    {
        try
        {
            JsonElement? json = Query(projectPath, targetFramework: null, out string failure);
            if (json is null)
            {
                return HostFallback(failure);
            }

            // A multi-targeted project has no TargetPath in its outer build. Pick one framework
            // (the host's, when the project offers it) and ask again for that inner build.
            string targetPath = Property(json.Value, "TargetPath");
            string frameworks = Property(json.Value, "TargetFrameworks");
            if (targetPath.Length == 0 && frameworks.Length > 0)
            {
                string[] all = frameworks.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                string chosen = all.FirstOrDefault(f => f == HostFramework) ?? all[0];
                json = Query(projectPath, chosen, out failure);
                if (json is null)
                {
                    return HostFallback(failure);
                }
            }

            return FromJson(projectPath, json.Value);
        }
        catch (Exception ex)
        {
            return HostFallback($"Reading {Path.GetFileName(projectPath)} failed: {ex.Message}.");
        }
    }

    /// <summary>The TFM the host itself runs on, e.g. <c>net10.0</c>.</summary>
    private static string HostFramework => $"net{Environment.Version.Major}.{Environment.Version.Minor}";

    private static ProjectContext FromJson(string projectPath, JsonElement json)
    {
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        string projectName = Path.GetFileName(projectPath);

        string targetPath = Property(json, "TargetPath");
        string targetDir = Property(json, "TargetDir");

        // --- compile references -------------------------------------------------------------
        //
        // Terminal.Gui is swapped for the host's copy. The previewed view is added into the
        // chrome's view tree, so both must agree on one Terminal.Gui; at runtime the host's
        // always wins, and compiling against a different one would only move the failure from
        // a compile error to a MissingMethodException.
        string hostTui = typeof(View).Assembly.Location;
        var compile = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (string path in ItemPaths(json, "ReferencePath"))
        {
            string name = Path.GetFileNameWithoutExtension(path);
            if (string.Equals(name, "Terminal.Gui", StringComparison.OrdinalIgnoreCase))
            {
                CheckTerminalGuiVersion(path, diagnostics);
                continue;
            }

            if (seen.Add(name))
            {
                compile.Add(path);
            }
        }

        if (seen.Add("Terminal.Gui"))
        {
            compile.Add(hostTui);
        }

        // The project's own assembly, for the types the view uses from it. A view that is itself
        // part of the project is compiled from source as well; the source copy wins (CS0436,
        // which the pipeline suppresses), so the preview still shows the edit, not the last build.
        if (targetPath.Length > 0 && File.Exists(targetPath))
        {
            if (seen.Add(Path.GetFileNameWithoutExtension(targetPath)))
            {
                compile.Add(targetPath);
            }
        }
        else
        {
            diagnostics.Add(Warn(
                $"{projectName} has not been built yet ({(targetPath.Length > 0 ? targetPath : "no TargetPath")}), " +
                "so types declared in it are unavailable. Build it once, or pass their files with --extra."));
        }

        // --- runtime assemblies ---------------------------------------------------------------
        //
        // The build output first: it holds the project's own assembly and the outputs of its
        // project references. The copy-local items then fill in what a class library does not
        // copy - its NuGet dependencies, which stay in the package cache.
        //
        // Both item types are needed. RuntimeCopyLocalItems lists the NuGet runtime assets and
        // is populated for every project; ReferenceCopyLocalPaths adds project-reference outputs,
        // but for a class library (CopyLocalLockFileAssemblies=false) it is empty, so reading it
        // alone leaves every package unresolvable at runtime.
        var runtime = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (targetDir.Length > 0 && Directory.Exists(targetDir))
        {
            foreach (string dll in Directory.EnumerateFiles(targetDir, "*.dll"))
            {
                runtime.TryAdd(Path.GetFileNameWithoutExtension(dll), dll);
            }
        }

        foreach (JsonElement item in Items(json, "RuntimeCopyLocalItems").Concat(Items(json, "ReferenceCopyLocalPaths")))
        {
            string path = Metadata(item, "FullPath", Metadata(item, "Identity", string.Empty));

            // Satellite resources and RID-specific assets live in subfolders; only top-level
            // assemblies are resolved by simple name.
            string dest = Metadata(item, "DestinationSubPath", Path.GetFileName(path));
            if (!path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ||
                dest.Contains('/') || dest.Contains('\\'))
            {
                continue;
            }

            runtime.TryAdd(Path.GetFileNameWithoutExtension(path), path);
        }

        if (targetPath.Length > 0)
        {
            runtime[Path.GetFileNameWithoutExtension(targetPath)] = targetPath;
        }

        // --- language settings ------------------------------------------------------------------
        var usings = new StringBuilder();
        foreach (JsonElement item in Items(json, "Using"))
        {
            string ns = Metadata(item, "Identity", string.Empty);
            if (ns.Length == 0)
            {
                continue;
            }

            string alias = Metadata(item, "Alias", string.Empty);
            bool isStatic = string.Equals(Metadata(item, "Static", string.Empty), "true", StringComparison.OrdinalIgnoreCase);

            usings.AppendLine(GlobalUsing(ns, alias, isStatic));
        }

        NullableContextOptions nullable = Enum.TryParse(Property(json, "Nullable"), ignoreCase: true, out NullableContextOptions n)
            ? n
            : NullableContextOptions.Disable;

        LanguageVersion languageVersion =
            LanguageVersionFacts.TryParse(Property(json, "LangVersion"), out LanguageVersion lv)
                ? lv
                : LanguageVersion.Latest;

        return new ProjectContext(
            compile.ToImmutableArray(),
            runtime,
            usings.ToString(),
            nullable,
            languageVersion,
            SplitSymbols(Property(json, "DefineConstants")),
            diagnostics.ToImmutable(),
            isHostFallback: false);
    }

    private static void CheckTerminalGuiVersion(string projectCopy, ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        Version? host = typeof(View).Assembly.GetName().Version;
        Version? project;
        try
        {
            project = AssemblyName.GetAssemblyName(projectCopy).Version;
        }
        catch (Exception)
        {
            return;
        }

        if (host is not null && project is not null && host != project)
        {
            diagnostics.Add(Warn(
                $"The project references Terminal.Gui {project}, but the preview runs {host}. " +
                "The view is compiled and run against the preview's copy."));
        }
    }

    /// <summary>Runs the MSBuild query and returns its JSON, or null with the reason.</summary>
    private static JsonElement? Query(string projectPath, string? targetFramework, out string failure)
    {
        var psi = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
        };

        foreach (string arg in new[]
                 {
                     "msbuild", Path.GetFullPath(projectPath), "-nologo",
                     "-target:ResolveReferences", "-p:BuildProjectReferences=false",
                     "-getProperty:TargetPath", "-getProperty:TargetDir", "-getProperty:TargetFrameworks",
                     "-getProperty:Nullable", "-getProperty:LangVersion", "-getProperty:DefineConstants",
                     "-getItem:ReferencePath", "-getItem:RuntimeCopyLocalItems",
                     "-getItem:ReferenceCopyLocalPaths", "-getItem:Using",
                 })
        {
            psi.ArgumentList.Add(arg);
        }

        if (targetFramework is not null)
        {
            psi.ArgumentList.Add($"-p:TargetFramework={targetFramework}");
        }

        using Process process = Process.Start(psi)
            ?? throw new InvalidOperationException("could not start dotnet");

        // Both streams concurrently: a full stderr pipe would otherwise block the child forever.
        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();

        if (!process.WaitForExit(TimeSpan.FromSeconds(60)))
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            failure = $"MSBuild did not answer for {Path.GetFileName(projectPath)} within 60 s.";
            return null;
        }

        string output = stdout.Result;
        if (process.ExitCode != 0)
        {
            // MSBuild writes its errors to stdout. The last line is usually the useful one, e.g.
            // "error NETSDK1004: Assets file ... not found. Run a NuGet package restore".
            string detail = (output + stderr.Result)
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .LastOrDefault(l => l.Contains("error", StringComparison.OrdinalIgnoreCase)) ?? $"exit code {process.ExitCode}";
            failure = $"MSBuild could not read {Path.GetFileName(projectPath)}: {detail}";
            return null;
        }

        int start = output.IndexOf('{');
        if (start < 0)
        {
            failure = $"MSBuild returned no data for {Path.GetFileName(projectPath)}.";
            return null;
        }

        failure = string.Empty;
        return JsonDocument.Parse(output[start..]).RootElement.Clone();
    }

    private static string Property(JsonElement json, string name) =>
        json.TryGetProperty("Properties", out JsonElement props) &&
        props.TryGetProperty(name, out JsonElement value)
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static IEnumerable<JsonElement> Items(JsonElement json, string name) =>
        json.TryGetProperty("Items", out JsonElement items) &&
        items.TryGetProperty(name, out JsonElement list) &&
        list.ValueKind == JsonValueKind.Array
            ? list.EnumerateArray()
            : Enumerable.Empty<JsonElement>();

    private static IEnumerable<string> ItemPaths(JsonElement json, string name) =>
        Items(json, name)
            .Select(i => Metadata(i, "FullPath", Metadata(i, "Identity", string.Empty)))
            .Where(p => p.Length > 0);

    private static string Metadata(JsonElement item, string name, string fallback) =>
        item.TryGetProperty(name, out JsonElement value) && value.GetString() is { Length: > 0 } s
            ? s
            : fallback;
}

/// <summary>
/// Hands out the current <see cref="ProjectContext"/>, re-reading the project only when it
/// changes. The MSBuild query costs about half a second, which is fine once per project edit but
/// not once per keystroke in a live preview.
/// </summary>
internal sealed class ProjectContextSource
{
    private readonly string _projectPath;
    private string? _stamp;
    private ProjectContext? _current;

    public ProjectContextSource(string projectPath) => _projectPath = Path.GetFullPath(projectPath);

    public ProjectContext Current
    {
        get
        {
            string stamp = Stamp();
            if (_current is null || stamp != _stamp)
            {
                _current = ProjectContext.Load(_projectPath);
                _stamp = stamp;
            }

            return _current;
        }
    }

    /// <summary>
    /// The project file plus the restore output: a changed PackageReference shows up in
    /// project.assets.json only after a restore, which is also when its paths become valid.
    /// </summary>
    private string Stamp()
    {
        string assets = Path.Combine(Path.GetDirectoryName(_projectPath)!, "obj", "project.assets.json");
        return $"{StampOf(_projectPath)}|{StampOf(assets)}";

        static string StampOf(string path)
        {
            var info = new FileInfo(path);
            return info.Exists ? $"{info.LastWriteTimeUtc.Ticks}:{info.Length}" : "-";
        }
    }
}
