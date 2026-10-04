using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TuiPreview.Cli.Preview;

/// <summary>
/// Turns a project the view names (a <c>project=</c> key, or <c>--project</c>) into the assemblies
/// the preview references: what a <c>&lt;ProjectReference&gt;</c> would have given the app.
/// <para>
/// The project is evaluated, never built. Building before every preview would cost seconds on each
/// right-click, and a library that does not compile would surface as a preview failure in a view
/// that is fine. So the output has to exist already: whoever changes the library builds it, as
/// they would before running the app, and a running preview picks the new build up because the
/// output is among the files it watches.
/// </para>
/// </summary>
internal static partial class ProjectOutputs
{
    /// <summary>The assemblies one project contributes, or the reason it cannot contribute any.</summary>
    public sealed record Resolved(string Project, string? TargetPath, IReadOnlyList<string> Assemblies, string? Problem);

    /// <summary>
    /// Asks MSBuild where <paramref name="project"/> builds to and which package assemblies it
    /// runs with.
    /// <para>
    /// Three sources, because a library's bin folder alone is not enough: it holds the library and
    /// the projects it references, but not its NuGet packages, which a library leaves in the
    /// package cache. <c>RuntimeCopyLocalItems</c> is where the SDK itself finds those when it
    /// copies them into an app.
    /// </para>
    /// </summary>
    public static Resolved Resolve(string project)
    {
        string name = Path.GetFileName(project);

        if (!File.Exists(project))
        {
            return new Resolved(project, null, [], $"project {project} does not exist.");
        }

        // Evaluation only, for the target framework: a multi-targeted project has no TargetPath
        // until one framework is chosen.
        var (properties, error) = Evaluate(project, [
            "-getProperty:TargetFramework",
            "-getProperty:TargetFrameworks",
        ]);
        if (error is not null)
        {
            return new Resolved(project, null, [], $"could not evaluate {name}: {error}");
        }

        var extra = new List<string>();
        if (string.IsNullOrEmpty(Property(properties, "TargetFramework")))
        {
            string? framework = PickFramework(Property(properties, "TargetFrameworks"));
            if (framework is null)
            {
                return new Resolved(project, null, [],
                    $"{name} targets none of the frameworks the preview can load " +
                    $"({Property(properties, "TargetFrameworks")}); it needs a .NET {Environment.Version.Major} or older one.");
            }

            extra.Add($"-p:TargetFramework={framework}");
        }

        // ResolvePackageAssets only reads project.assets.json, which a restore wrote. It builds
        // nothing, and it is what fills RuntimeCopyLocalItems.
        (properties, error) = Evaluate(project, [
            .. extra,
            "-t:ResolvePackageAssets",
            "-getProperty:TargetPath",
            "-getItem:RuntimeCopyLocalItems",
        ]);
        if (error is not null)
        {
            // NETSDK1004, no project.assets.json, is the common case: a library that was never
            // restored has never been built either, and MSBuild's wording of it buries that.
            return new Resolved(project, null, [], error.Contains("NETSDK1004", StringComparison.Ordinal)
                ? $"{name} has not been built yet. Build it with: dotnet build \"{project}\""
                : $"could not read the build output of {name}: {error}");
        }

        string? targetPath = Property(properties, "TargetPath");
        if (string.IsNullOrEmpty(targetPath))
        {
            return new Resolved(project, null, [], $"{name} reports no TargetPath; is it a library or an app project?");
        }

        if (!File.Exists(targetPath))
        {
            return new Resolved(project, targetPath, [],
                $"{name} has not been built: {targetPath} does not exist. Build it with: dotnet build \"{project}\"");
        }

        var assemblies = new List<string> { targetPath };

        // The projects it references, which the SDK copies next to it even for a library.
        foreach (string dll in Directory.EnumerateFiles(Path.GetDirectoryName(targetPath)!, "*.dll"))
        {
            if (!string.Equals(dll, targetPath, StringComparison.OrdinalIgnoreCase))
            {
                assemblies.Add(dll);
            }
        }

        // Its packages, from the cache. Only managed dlls: a native asset cannot be referenced.
        foreach (string item in Items(properties, "RuntimeCopyLocalItems"))
        {
            if (item.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) && File.Exists(item))
            {
                assemblies.Add(item);
            }
        }

        return new Resolved(project, targetPath, assemblies, null);
    }

    /// <summary>
    /// The newest framework the preview can load, from a multi-targeted project's list. The
    /// preview runs on this process's runtime, so a newer target is out, and of the rest the
    /// newest is the closest to what the tool was built against. A netstandard2.x target loads
    /// too, and is the fallback when there is no .NET one.
    /// </summary>
    internal static string? PickFramework(string? frameworks)
    {
        string[] all = (frameworks ?? string.Empty)
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        return all
            .Select(f => (Name: f, Match: ModernFramework().Match(f)))
            .Where(f => f.Match.Success)
            .Select(f => (f.Name, Version: new Version(int.Parse(f.Match.Groups[1].Value), int.Parse(f.Match.Groups[2].Value))))
            .Where(f => f.Version.Major <= Environment.Version.Major)
            .OrderByDescending(f => f.Version)
            .Select(f => f.Name)
            .FirstOrDefault()
            ?? all.Where(f => f.StartsWith("netstandard2", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(f => f, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
    }

    /// <summary>
    /// Runs <c>dotnet msbuild</c> with <c>-getProperty</c>/<c>-getItem</c>, which prints JSON.
    /// Returns the parsed output, or the error MSBuild reported.
    /// </summary>
    private static (JsonElement Output, string? Error) Evaluate(string project, IEnumerable<string> arguments)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(project)!,
        };
        start.ArgumentList.Add("msbuild");
        start.ArgumentList.Add(project);
        start.ArgumentList.Add("-nologo");
        foreach (string argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        // Not from the terminal the tool runs in: an MSBuild server or node left behind would keep
        // the preview's console window open after it closes.
        start.Environment["MSBUILDDISABLENODEREUSE"] = "1";
        start.Environment["DOTNET_CLI_USE_MSBUILD_SERVER"] = "0";

        using Process process = Process.Start(start)!;
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        string stdout = process.StandardOutput.ReadToEnd();
        process.WaitForExit();

        if (process.ExitCode != 0)
        {
            // MSBuild writes its errors to stdout. The error lines are what matter; the rest is noise.
            // Each line is "file(line,col): error CODE: message [project]"; only the middle is news.
            string errors = string.Join(" ", (stdout + "\n" + stderr.Result)
                .Split('\n')
                .Select(l => l.Trim())
                .Where(l => l.Contains("error", StringComparison.OrdinalIgnoreCase))
                .Select(l => Regex.Replace(l, @"^.*?:\s*(?=error\b)|\s*\[[^\]]*\]$", string.Empty))
                .Distinct()
                .Take(3));
            return (default, errors.Length > 0 ? errors : $"dotnet msbuild exited with {process.ExitCode}.");
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(stdout);
            return (doc.RootElement.Clone(), null);
        }
        catch (JsonException)
        {
            // A single -getProperty prints the bare value rather than JSON; never asked for here,
            // but say what came back rather than failing obscurely.
            return (default, $"unexpected output from dotnet msbuild: {stdout.Trim()}");
        }
    }

    private static string? Property(JsonElement output, string name) =>
        output.ValueKind == JsonValueKind.Object
        && output.TryGetProperty("Properties", out JsonElement properties)
        && properties.TryGetProperty(name, out JsonElement value)
            ? value.GetString()
            : null;

    private static IEnumerable<string> Items(JsonElement output, string name)
    {
        if (output.ValueKind != JsonValueKind.Object
            || !output.TryGetProperty("Items", out JsonElement items)
            || !items.TryGetProperty(name, out JsonElement list))
        {
            yield break;
        }

        foreach (JsonElement item in list.EnumerateArray())
        {
            if (item.TryGetProperty("FullPath", out JsonElement full) && full.GetString() is { } path)
            {
                yield return path;
            }
            else if (item.TryGetProperty("Identity", out JsonElement identity) && identity.GetString() is { } id)
            {
                yield return id;
            }
        }
    }

    /// <summary>net5.0 and later; netstandard and the .NET Framework monikers are not matched.</summary>
    [GeneratedRegex(@"^net(\d+)\.(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex ModernFramework();
}
