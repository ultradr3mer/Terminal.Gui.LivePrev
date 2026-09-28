using System.Reflection;
using System.Runtime.CompilerServices;
using TuiPreview.Host.Preview;

namespace TuiPreview.Host;

/// <summary>
/// Runs the live preview inside your own app, so a view compiles against exactly what the app
/// runs with: its references, its NuGet packages, its own types and its Terminal.Gui.
/// <code>
/// if (TuiPreviewHost.TryRun(args, out int code)) return code;
/// </code>
/// Call it first in <c>Main</c>, before any of the app's own setup runs.
/// </summary>
public static class TuiPreviewHost
{
  /// <summary>The first argument that switches the app into preview mode.</summary>
  public const string Switch = "--tui-preview";

  /// <summary>
  /// Runs the preview when <paramref name="args"/> starts with <see cref="Switch"/>, and does
  /// nothing otherwise.
  /// </summary>
  /// <returns>True when a preview ran and <c>Main</c> should return <paramref name="exitCode"/>.</returns>
  public static bool TryRun(string[] args, out int exitCode)
  {
    exitCode = 0;
    if (args.Length == 0 || args[0] != Switch)
    {
      return false;
    }

    Assembly? entry = Assembly.GetEntryAssembly();

    // A single-file bundle has no assembly paths to compile against, and NativeAOT cannot load
    // an assembly at all.
    if (entry is null || entry.Location.Length == 0 || !RuntimeFeature.IsDynamicCodeSupported)
    {
      Console.Error.WriteLine(
          $"error: {Switch} needs a regular framework-dependent build. " +
          "Single-file, trimmed and NativeAOT builds cannot compile a view at runtime.");
      exitCode = 1;
      return true;
    }

    exitCode = PreviewRunner.Run(args[1..], requireProject: false, _ =>
    {
      ProjectContext context = ProjectContext.FromRunningApp(entry);
      return () => context;
    });
    return true;
  }
}
