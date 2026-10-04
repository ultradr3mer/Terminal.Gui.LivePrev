using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace TuiLivePreview.VisualStudio.Core;

/// <summary>
/// Scratch copies of views, keyed by the real file's path.
/// <para>
/// The tool reads the view from disk, so previewing an unsaved buffer means writing that buffer
/// somewhere the tool can read. The path has to be stable across edits, because the interactive
/// preview notices changes by polling that file's timestamp: a fresh temp file per keystroke would
/// leave the running preview watching a path that no longer exists. One file per real view,
/// rewritten in place, is what makes the poll-and-rebuild machinery work.
/// </para>
/// </summary>
internal sealed class ScratchFiles(string directory)
{
    private readonly ConcurrentDictionary<string, string> _files = new(StringComparer.OrdinalIgnoreCase);

    public static ScratchFiles InTemp() =>
        new(Path.Combine(Path.GetTempPath(), "tui-live-preview"));

    public string Directory { get; } = directory;

    /// <summary>Whether a scratch copy exists for this view, i.e. whether it has been previewed.</summary>
    public bool IsMirrored(string realPath) => _files.ContainsKey(realPath);

    /// <summary>Where a real view's buffer is mirrored, created on first use.</summary>
    public string PathFor(string realPath) => _files.GetOrAdd(realPath, real =>
    {
        // Hashed rather than derived from the full path, because two views in different folders
        // must not collide on a shared name.
        string hash = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(real.ToLowerInvariant())))
            .ToLowerInvariant()[..12];
        System.IO.Directory.CreateDirectory(Directory);

        // The suffix is kept so the copy is still recognisably a view in logs and diagnostics.
        const string suffix = ".tui.cs";
        string name = Path.GetFileName(real);
        string stem = name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) ? name[..^suffix.Length] : name;
        return Path.Combine(Directory, $"{stem}-{hash}{suffix}");
    });

    /// <summary>
    /// Writes the buffer to the view's scratch file and returns its path.
    /// <para>
    /// Writes unconditionally: a buffer that is clean and one that has been edited back to its
    /// saved contents must both be reflected, and a same-length rewrite within the timestamp
    /// granularity would otherwise be invisible to the tool's poll.
    /// </para>
    /// </summary>
    public string Mirror(string realPath, string text)
    {
        string scratch = PathFor(realPath);
        File.WriteAllText(scratch, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return scratch;
    }

    /// <summary>A file next to the scratch copies, for the launch scripts and the log.</summary>
    public string Sibling(string name)
    {
        System.IO.Directory.CreateDirectory(Directory);
        return Path.Combine(Directory, name);
    }
}
