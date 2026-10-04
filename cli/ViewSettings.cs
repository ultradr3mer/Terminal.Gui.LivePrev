using System.Text.Json;
using System.Text.RegularExpressions;

namespace TuiPreview.Cli;

/// <summary>
/// Preview settings a view carries with itself, as opposed to settings passed on the command line.
/// <para>
/// Two sources, in increasing order of precedence:
/// </para>
/// <list type="number">
/// <item>An optional sidecar JSON file named after the view (<c>MyView.tui.json</c>).</item>
/// <item>An <c>// @tuipreview</c> annotation in the view itself.</item>
/// </list>
/// <para>
/// The annotation wins because it is the one the author is looking at, and because a sidecar
/// cannot describe an unsaved buffer: an editor previewing a dirty file would read a sibling
/// file that is one edit behind. Anything neither source states falls back to the command line.
/// </para>
/// </summary>
internal sealed partial class ViewSettings
{
  private readonly List<string> _extra = new();
  private readonly List<string> _references = new();
  private readonly List<string> _projects = new();
  private readonly List<string> _warnings = new();

  /// <summary>
  /// Which file last stated each single-valued key, and what it said. Kept so an annotation that
  /// overrides the sidecar can say so: the sidecar is read first and silently losing one of its
  /// values to a comment in the view is otherwise invisible.
  /// </summary>
  private readonly Dictionary<string, (string Value, string Origin)> _stated =
    new(StringComparer.OrdinalIgnoreCase);

  /// <summary>The fully qualified class to instantiate, or null to leave it to the command line.</summary>
  public string? Class { get; private set; }

  /// <summary>Render width in columns, or null when neither source states one.</summary>
  public int? Width { get; private set; }

  /// <summary>Render height in rows, or null when neither source states one.</summary>
  public int? Height { get; private set; }

  /// <summary>
  /// Further .cs files compiled with the view, as written. Paths are resolved relative to the
  /// file that named them, because that is what the author of that file can reason about.
  /// </summary>
  public IReadOnlyList<string> Extra => _extra;

  /// <summary>
  /// Assemblies to add to the default reference set, as written. A path to a dll, or a directory
  /// holding one, because both are what a third-party control library hands out.
  /// </summary>
  public IReadOnlyList<string> References => _references;

  /// <summary>
  /// Projects whose build output is added to the reference set, as written: a control library in
  /// the same solution, which is what a <c>&lt;ProjectReference&gt;</c> in the app points at. The
  /// tool asks MSBuild where each one builds to; it never builds them itself.
  /// </summary>
  public IReadOnlyList<string> Projects => _projects;

  /// <summary>
  /// A path to the <c>tuipreview</c> tool for this view, or null to let the caller find one.
  /// <para>
  /// Only the editor extension reads this. The tool cannot act on it, since it is already the
  /// tool running, but a view states where its preview comes from and the extension is what
  /// launches it.
  /// </para>
  /// </summary>
  public string? CliPath { get; private set; }

  /// <summary>Problems worth telling the user about that do not stop the preview.</summary>
  public IReadOnlyList<string> Warnings => _warnings;

  /// <summary>Which source supplied the last value applied, for the log line.</summary>
  public string Source { get; private set; } = "none";

  /// <summary>
  /// The directory every relative path in these settings is resolved against, or null before
  /// <see cref="Read"/> has run.
  /// <para>
  /// Kept here rather than left to the caller to work out, because "relative to the view" is the
  /// only sensible reading of a path written inside a view. A caller that called
  /// <c>GetFullPath</c> itself would resolve it against its own working directory, which is
  /// wherever the terminal happened to open.
  /// </para>
  /// </summary>
  public string BaseDir { get; private set; } = string.Empty;

  /// <summary>
  /// Reads both sources for <paramref name="viewFile"/>, which must exist.
  /// </summary>
  public static ViewSettings Read(string viewFile)
  {
    string text;
    try
    {
      text = File.ReadAllText(viewFile);
    }
    catch (IOException ex)
    {
      var unreadable = Read(viewFile, string.Empty);
      unreadable._warnings.Add(
        $"could not read {Path.GetFileName(viewFile)} for annotations ({ex.Message}).");
      return unreadable;
    }

    return Read(viewFile, text);
  }

  /// <summary>
  /// Reads both sources for <paramref name="viewFile"/>, taking the annotations from
  /// <paramref name="text"/> rather than from disk.
  /// <para>
  /// This is what an editor calls: the open buffer may hold an annotation that is not saved yet,
  /// and that unsaved annotation is the one the user is looking at. The sidecar is still read from
  /// disk, since nothing else could hold it.
  /// </para>
  /// </summary>
  public static ViewSettings Read(string viewFile, string text)
  {
    var settings = new ViewSettings
    {
      BaseDir = Path.GetDirectoryName(Path.GetFullPath(viewFile))!,
    };

    if (SidecarPathFor(viewFile) is { } sidecar)
    {
      settings.ReadSidecar(sidecar);
    }

    settings.ReadAnnotations(viewFile, text);

    return settings;
  }

  /// <summary>
  /// The sidecar that goes with a view, or null when the name does not end in <c>.tui.cs</c>.
  /// <para>
  /// Named after the view: MyView.tui.cs -> MyView.tui.json. The .tui.cs suffix is dropped
  /// rather than replaced, so the pair reads as one artifact with two halves.
  /// </para>
  /// </summary>
  public static string? SidecarPathFor(string viewFile)
  {
    string name = Path.GetFileName(viewFile);
    int suffix = name.LastIndexOf(".tui.cs", StringComparison.Ordinal);
    if (suffix <= 0)
    {
      return null;
    }

    return Path.Combine(
      Path.GetDirectoryName(Path.GetFullPath(viewFile))!,
      name[..suffix] + ".tui.json");
  }

  /// <summary>
  /// Whether either source stated a valid value for the single-valued <paramref name="key"/>.
  /// An editor's "init" uses this to write down only what the view does not already say.
  /// </summary>
  public bool States(string key) => _stated.ContainsKey(key);

  /// <summary>
  /// Whether <paramref name="text"/> already holds a <c>// @tuipreview</c> line, valid or not.
  /// </summary>
  public static bool HasAnnotation(string text) => Annotation().IsMatch(text);

  private void ReadSidecar(string file)
  {
    if (!File.Exists(file))
    {
      return;
    }

    string baseDir = Path.GetDirectoryName(file)!;
    string origin = Path.GetFileName(file);

    JsonElement root;
    try
    {
      using JsonDocument doc = JsonDocument.Parse(
        File.ReadAllText(file),
        new JsonDocumentOptions
        {
          CommentHandling = JsonCommentHandling.Skip,
          AllowTrailingCommas = true,
        });
      root = doc.RootElement.Clone();
    }
    catch (JsonException ex)
    {
      _warnings.Add($"{origin} is not valid JSON ({ex.Message}); ignoring it.");
      return;
    }

    if (root.ValueKind != JsonValueKind.Object)
    {
      _warnings.Add($"{origin} must hold a JSON object; ignoring it.");
      return;
    }

    if (TryString(root, "class", out string className))
    {
      Apply("class", className, origin, baseDir);
    }

    if (TryString(root, "clipath", out string cliPath))
    {
      Apply("clipath", cliPath, origin, baseDir);
    }

    if (TryNumber(root, "width", out double width))
    {
      Apply("width", Number(width), origin, baseDir);
    }

    if (TryNumber(root, "height", out double height))
    {
      Apply("height", Number(height), origin, baseDir);
    }

    if (root.TryGetProperty("extra", out JsonElement extra))
    {
      foreach (string entry in ReadExtraList(extra, "extra", origin))
      {
        Apply("extra", Resolve(baseDir, entry), origin, baseDir);
      }
    }

    // "reference" takes the same string-or-array shape as "extra", and its paths are resolved
    // against the view for the same reason: a relative path written inside a view means
    // "relative to that view".
    if (root.TryGetProperty("reference", out JsonElement references))
    {
      foreach (string entry in ReadExtraList(references, "reference", origin))
      {
        Apply("reference", Resolve(baseDir, entry), origin, baseDir);
      }
    }

    if (root.TryGetProperty("project", out JsonElement projects))
    {
      foreach (string entry in ReadExtraList(projects, "project", origin))
      {
        Apply("project", Resolve(baseDir, entry), origin, baseDir);
      }
    }

    // Reported after the known keys are read, so a typo does not also suppress a value.
    foreach (JsonProperty property in root.EnumerateObject())
    {
      if (property.Name is not ("class" or "width" or "height" or "extra" or "reference" or "project" or "clipath"))
      {
        _warnings.Add($"{origin}: unknown key \"{property.Name}\"; ignoring it.");
      }
    }
  }

  /// <summary>
  /// The "extra" entries, accepting a bare string as well as an array because a single extra
  /// file is the common case and an array of one is noise.
  /// </summary>
  private IEnumerable<string> ReadExtraList(JsonElement extra, string key, string origin)
  {
    if (extra.ValueKind == JsonValueKind.Array)
    {
      return extra.EnumerateArray()
        .Where(e => e.ValueKind == JsonValueKind.String)
        .Select(e => e.GetString()!);
    }

    if (extra.ValueKind == JsonValueKind.String)
    {
      return [extra.GetString()!];
    }

    _warnings.Add($"{origin}: \"{key}\" must be a string or an array of strings; ignoring it.");
    return [];
  }

  private void ReadAnnotations(string viewFile, string text)
  {
    string origin = Path.GetFileName(viewFile);
    string baseDir = Path.GetDirectoryName(Path.GetFullPath(viewFile))!;

    foreach (Match line in Annotation().Matches(text))
    {
      foreach (Match pair in Pair().Matches(line.Groups["args"].Value))
      {
        string key = pair.Groups["key"].Value;
        string value = pair.Groups["quoted"].Success
          ? pair.Groups["quoted"].Value
          : pair.Groups["bare"].Value;

        // All name a file the view is written against, so all resolve against the view.
        bool isFile = key.Equals("extra", StringComparison.OrdinalIgnoreCase)
          || key.Equals("reference", StringComparison.OrdinalIgnoreCase)
          || key.Equals("project", StringComparison.OrdinalIgnoreCase);

        Apply(key, isFile ? Resolve(baseDir, value) : value, origin, baseDir);
      }
    }
  }

  /// <summary>
  /// Applies one key/value pair, validating as it goes.
  /// <para>
  /// A bad value is reported and dropped rather than thrown: a typo in a preview annotation
  /// should not stop the preview, and the warning names the file so it can be found again.
  /// </para>
  /// </summary>
  private void Apply(string key, string value, string origin, string baseDir)
  {
    value = value.Trim();
    if (value.Length == 0)
    {
      _warnings.Add($"{origin}: \"{key}\" has no value; ignoring it.");
      return;
    }

    NoteOverride(key, value, origin);

    switch (key.ToLowerInvariant())
    {
      case "class":
        Class = value;
        Source = "view settings";
        break;

      case "width":
      case "height":
        if (int.TryParse(value, out int size) && size > 0)
        {
          if (key.Equals("width", StringComparison.OrdinalIgnoreCase))
          {
            Width = size;
          }
          else
          {
            Height = size;
          }

          Source = "view settings";
        }
        else
        {
          _warnings.Add($"{origin}: \"{key}\" must be a positive whole number, got \"{value}\".");
        }

        break;

      case "extra":
        _extra.Add(value);
        Source = "view settings";
        break;

      case "reference":
        // A dll or a directory holding one, both of which a NuGet package documents. A
        // directory is kept as written: the caller expands it, because which file inside a
        // directory is the library is not a guess this parser should be making.
        _references.Add(value);
        Source = "view settings";
        break;

      case "project":
        _projects.Add(value);
        Source = "view settings";
        break;

      case "clipath":
        CliPath = Path.IsPathRooted(value) ? value : Path.Combine(baseDir, value);
        Source = "view settings";
        break;

      default:
        _warnings.Add($"{origin}: unknown key \"{key}\"; ignoring it.");
        break;
    }
  }

  /// <summary>
  /// Warns when a single-valued key is stated again by a different file with a different value,
  /// which in practice is the annotation overriding the sidecar. Lists (extra, reference) add up
  /// rather than override, so there is nothing to lose and nothing to warn about.
  /// </summary>
  private void NoteOverride(string key, string value, string origin)
  {
    if (key.ToLowerInvariant() is not ("class" or "width" or "height" or "clipath"))
    {
      return;
    }

    // An invalid size is reported by Apply and never takes effect, so it overrides nothing.
    if (key.ToLowerInvariant() is "width" or "height" && !(int.TryParse(value, out int size) && size > 0))
    {
      return;
    }

    if (_stated.TryGetValue(key, out var earlier)
      && earlier.Origin != origin
      && !string.Equals(earlier.Value, value, StringComparison.Ordinal))
    {
      _warnings.Add(
        $"{origin}: \"{key.ToLowerInvariant()}\" = \"{value}\" overrides \"{earlier.Value}\" " +
        $"from {earlier.Origin}.");
    }

    _stated[key] = (value, origin);
  }

  /// <summary>A relative path in a view's settings is relative to that view.</summary>
  private static string Resolve(string baseDir, string value) =>
    Path.IsPathRooted(value) ? value : Path.Combine(baseDir, value);

  /// <summary>
  /// The class a view declares, for when neither it nor the command line names one.
  /// <para>
  /// The namespace plus the first class declaration, which is the layout half in the usual case
  /// and so exactly the class a preview wants. This is the last resort of a three-step chain:
  /// an explicit --class, then what the view says for itself, then this. It exists so the tool
  /// works from a bare command line with nothing to configure, which is how it is used by hand.
  /// </para>
  /// </summary>
  public static string? InferClassName(string viewFile)
  {
    string text;
    try
    {
      text = File.ReadAllText(viewFile);
    }
    catch (IOException)
    {
      return null;
    }

    return InferClassNameFromText(text);
  }

  /// <summary>
  /// <see cref="InferClassName"/> for source that is already in memory, such as an editor buffer
  /// with unsaved changes.
  /// </summary>
  public static string? InferClassNameFromText(string text)
  {
    // Comments are removed first, so a commented-out class cannot be picked up.
    string code = Regex.Replace(Regex.Replace(text, @"/\*[\s\S]*?\*/", " "), @"//[^\n]*", " ");

    // The file-scoped form first, then the block form.
    Match ns = Regex.Match(code, @"^\s*namespace\s+([A-Za-z_][\w.]*)\s*;", RegexOptions.Multiline);
    if (!ns.Success)
    {
      ns = Regex.Match(code, @"^\s*namespace\s+([A-Za-z_][\w.]*)\s*\{", RegexOptions.Multiline);
    }

    // A declaration that opens its own body. Nested types are excluded by requiring the
    // declaration to start a line, which is how they are written in practice.
    Match type = Regex.Match(
      code,
      @"^(?:[^\S\n]*)(?:(?:public|internal|sealed|abstract|partial|static|unsafe|new)\s+)*class\s+([A-Za-z_]\w*)",
      RegexOptions.Multiline);
    if (!type.Success)
    {
      return null;
    }

    return ns.Success ? $"{ns.Groups[1].Value}.{type.Groups[1].Value}" : type.Groups[1].Value;
  }

  private static string Number(double value) =>
    value.ToString(System.Globalization.CultureInfo.InvariantCulture);

  private static bool TryString(JsonElement root, string name, out string value)
  {
    value = string.Empty;
    if (!root.TryGetProperty(name, out JsonElement element) || element.ValueKind != JsonValueKind.String)
    {
      return false;
    }

    value = element.GetString() ?? string.Empty;
    return true;
  }

  private static bool TryNumber(JsonElement root, string name, out double value)
  {
    value = 0;
    return root.TryGetProperty(name, out JsonElement element)
      && element.ValueKind == JsonValueKind.Number
      && element.TryGetDouble(out value);
  }

  /// <summary>
  /// A line comment opening a @tuipreview annotation. Only line comments count: the view is C#,
  /// and a block comment wrapping preview configuration is not something anyone writes.
  /// </summary>
  [GeneratedRegex(@"^[ \t]*//[ \t]*@tuipreview\b(?<args>[^\n]*)", RegexOptions.Multiline)]
  private static partial Regex Annotation();

  /// <summary>
  /// Key=value pairs on one annotation line. A value may be double-quoted to carry spaces, and an
  /// unquoted value ends at whitespace or a separator so several pairs fit on one line.
  /// </summary>
  [GeneratedRegex(
    @"(?<key>[A-Za-z_][\w-]*)[ \t]*=[ \t]*(?:""(?<quoted>[^""]*)""|(?<bare>[^\s,;]+))")]
  private static partial Regex Pair();
}