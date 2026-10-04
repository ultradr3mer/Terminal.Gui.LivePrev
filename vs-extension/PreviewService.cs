using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.VisualStudio.Extensibility;
using Microsoft.VisualStudio.Extensibility.Documents;
using Microsoft.VisualStudio.Extensibility.Editor;
using Microsoft.VisualStudio.Extensibility.Shell;
using Microsoft.VisualStudio.RpcContracts.Documents;
using TuiLivePreview.VisualStudio.Core;
using TuiPreview.Cli;

namespace TuiLivePreview.VisualStudio;

/// <summary>
/// Everything the commands share: the log, the scratch copies, the running previews.
/// <para>
/// One instance for the extension's lifetime, registered as a singleton, because the running
/// previews and the mirrored buffers have to outlive any one command invocation.
/// </para>
/// </summary>
internal sealed class PreviewService : IDisposable
{
    /// <summary>
    /// Bumped whenever the extension changes, and logged on first use, so a stale install is
    /// recognisable from the log rather than looking like a fix that did not work.
    /// </summary>
    public const string Build = "2026-10-04.1";

    private readonly VisualStudioExtensibility _extensibility;
    private readonly PreviewLog _log;
    private readonly ScratchFiles _scratch = ScratchFiles.InTemp();

    /// <summary>
    /// Running previews, keyed by view path.
    /// <para>
    /// One per view, across render, watch and interactive: those are all previews of the same
    /// view, and watch and interactive run until stopped, so a new window per right-click would
    /// leak one process each time. Starting a preview replaces the view's previous one.
    /// </para>
    /// </summary>
    private readonly ConcurrentDictionary<string, Process> _running = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Pending debounced mirror writes, keyed by view path.</summary>
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _pendingMirrors =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The "live" setting as of the last command. Kept rather than re-read on every keystroke,
    /// which would be a cross-process call per character typed.
    /// </summary>
    private volatile bool _live = true;

    public PreviewService(VisualStudioExtensibility extensibility)
    {
        _extensibility = extensibility;
        _log = new PreviewLog(extensibility, _scratch.Sibling("extension.log"));
        _log.Line($"extension loaded (build {Build}, {typeof(PreviewService).Assembly.Location})");
    }

    /// <summary>Where the extension is installed, and so where its bundled copy of the tool lives.</summary>
    private static string? ExtensionDirectory => Path.GetDirectoryName(typeof(PreviewService).Assembly.Location);

    /// <summary>
    /// Runs a command so every failure is both logged and shown, with a way to the log. A
    /// silently swallowed exception is indistinguishable from a dead menu entry, which is exactly
    /// the failure mode that wastes the most time.
    /// </summary>
    public async Task GuardAsync(string name, Func<Task> body, CancellationToken cancellationToken)
    {
        _log.Line($"--- {name} invoked ---");
        try
        {
            await body();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _log.Line($"{name}: cancelled");
        }
        catch (Exception ex)
        {
            _log.Line($"FAILED {name}: {ex}");
            string message = ex is PreviewException ? ex.Message : $"{ex.GetType().Name}: {ex.Message}";
            await OfferLogAsync($"TUI Preview: {message}", cancellationToken);
        }
    }

    /// <summary>Starts a preview of the selected view in its own console window.</summary>
    public async Task LaunchAsync(PreviewMode mode, IClientContext context, CancellationToken cancellationToken)
    {
        _log.Line($"launch requested: mode={mode.ToString().ToLowerInvariant()}");

        EditorSettings editor = await PreviewOptions.ReadAsync(_extensibility, cancellationToken);
        _live = editor.Live;

        string view = await PickViewAsync(context, cancellationToken);
        ViewText buffer = await ReadViewAsync(view, cancellationToken);

        ViewSettings settings = ViewSettings.Read(view, buffer.Text);
        foreach (string warning in settings.Warnings)
        {
            _log.Line($"view settings: {warning}");
        }

        PreviewPlanner.Validate(view, settings);

        string className = await ResolveClassNameAsync(view, buffer.Text, settings, editor, cancellationToken);
        Launcher launcher = ToolLocator.Resolve(view, settings.CliPath, NullIfBlank(editor.CliPath), ExtensionDirectory);

        // Whether to mirror the editor buffer rather than trusting what is on disk. Off means the
        // preview always reflects the last save, which is what makes a render reproducible.
        string previewPath = view;
        if (buffer.IsOpen && editor.Live)
        {
            previewPath = _scratch.Mirror(view, buffer.Text);
            _log.Line($"mirrored {Path.GetFileName(view)} ({(buffer.IsDirty ? "unsaved" : "saved")}) -> {previewPath}");
        }
        else if (buffer.IsDirty)
        {
            _log.Line(
                $"{Path.GetFileName(view)} has unsaved changes, but \"Preview unsaved changes\" is off; " +
                "previewing the last saved version.");
        }

        var request = new PreviewRequest
        {
            Launcher = launcher,
            View = view,
            ClassName = className,
            PreviewPath = previewPath,
            ExtraFiles = settings.Extra,
            References = settings.References,
            // A view's own size wins over the setting, so one wide view does not make every other
            // preview wide too.
            Width = settings.Width ?? editor.Width,
            Height = settings.Height ?? editor.Height,
            Ansi = editor.Ansi,
        };

        _log.Line(
            $"resolved: view={view} class={className} launcher={launcher.Describe()}" +
            (previewPath == view ? string.Empty : $" preview={previewPath}") +
            (settings.Extra.Count > 0 ? $" extra={string.Join(",", settings.Extra)}" : string.Empty) +
            (settings.References.Count > 0 ? $" refs={string.Join(",", settings.References)}" : string.Empty) +
            (settings.CliPath is not null ? $" clipath={settings.CliPath}" : string.Empty));

        if (editor.Confirmation)
        {
            bool start = await _extensibility.Shell().ShowPromptAsync(
                $"Preview {Path.GetFileName(view)} as {className}?",
                PromptOptions.OKCancel with { Title = "TUI Preview" },
                cancellationToken);
            if (!start)
            {
                _log.Line("cancelled at the confirmation prompt");
                return;
            }
        }

        Start(request, mode);

        // Shown, not just logged: an annotation overriding the sidecar, or a typo in either,
        // changes what is previewed, and the log is not where anyone looks while a preview starts.
        // Only after the preview has started, because a warning must never hold it up.
        if (settings.Warnings.Count > 0)
        {
            int more = settings.Warnings.Count - 1;
            _ = OfferLogAsync(
                $"TUI Preview: {settings.Warnings[0]}" + (more > 0 ? $" (and {more} more)" : string.Empty),
                CancellationToken.None);
        }
    }

    /// <summary>
    /// Gives a view its own preview settings: detects what it would preview with today and writes
    /// that down, either to a <c>.tui.json</c> sidecar or as a <c>// @tuipreview</c> annotation.
    /// <para>
    /// Only what the view does not already state is written, so the annotation never shadows a
    /// sidecar value that a later edit to the sidecar should change. It never overwrites: an
    /// existing sidecar or annotation is opened instead, since both are hand-edited and init has no
    /// way to merge into one without losing the comments and ordering their author chose.
    /// </para>
    /// </summary>
    public async Task InitAsync(IClientContext context, CancellationToken cancellationToken)
    {
        EditorSettings editor = await PreviewOptions.ReadAsync(_extensibility, cancellationToken);
        string view = await PickViewAsync(context, cancellationToken);
        ViewText buffer = await ReadViewAsync(view, cancellationToken);
        string name = Path.GetFileName(view);
        string sidecar = ViewSettings.SidecarPathFor(view)!;

        ViewSettings current = ViewSettings.Read(view, buffer.Text);
        string className = await ResolveClassNameAsync(view, buffer.Text, current, editor, cancellationToken);
        var detected = PreviewPlanner.Detected(current, editor, className);
        string described = string.Join(", ", detected.Select(d => $"{d.Key}={d.Value}"));
        _log.Line($"init: detected {described} for {name}");

        var choice = await _extensibility.Shell().ShowPromptAsync(
            $"Where should the preview settings for {name} go?\n\n" +
            $"Detected: {described}.\n\n" +
            "A sidecar is a JSON file next to the view. An annotation is a \"// @tuipreview\" " +
            "comment at the top of the view, and wins over the sidecar wherever both state the same key.",
            new PromptOptions<InitTarget>
            {
                Title = "TUI Preview: Init",
                Choices =
                {
                    { $"Sidecar ({Path.GetFileName(sidecar)})", InitTarget.Sidecar },
                    { "Annotation in the view", InitTarget.Annotation },
                    { "Cancel", InitTarget.None },
                },
                DismissedReturns = InitTarget.None,
            },
            cancellationToken);

        if (choice == InitTarget.None)
        {
            _log.Line("init: cancelled");
            return;
        }

        var missing = detected.Where(d => !current.States(d.Key)).ToList();

        if (choice == InitTarget.Sidecar)
        {
            await InitSidecarAsync(view, sidecar, missing, cancellationToken);
        }
        else
        {
            await InitAnnotationAsync(view, buffer.Text, sidecar, missing, cancellationToken);
        }
    }

    private async Task InitSidecarAsync(
        string view, string sidecar, IReadOnlyList<(string Key, string Value)> entries, CancellationToken cancellationToken)
    {
        if (File.Exists(sidecar))
        {
            _log.Line($"init: {sidecar} already exists; opening it instead");
            await OpenAsync(sidecar, cancellationToken);
            await InformAsync(
                $"{Path.GetFileName(sidecar)} already exists, so it was opened rather than overwritten.",
                cancellationToken);
            return;
        }

        File.WriteAllText(sidecar, PreviewPlanner.SidecarText(view, entries));
        _log.Line($"init: wrote {sidecar}");
        await OpenAsync(sidecar, cancellationToken);
    }

    /// <summary>
    /// Puts a <c>// @tuipreview</c> line at the top of the view.
    /// <para>
    /// Done as an edit to the document rather than a write to disk, so it lands in the undo history
    /// and does not race a buffer with unsaved changes. A buffer that had none is saved afterwards,
    /// so the tool, which reads from disk, sees the same thing the editor shows.
    /// </para>
    /// </summary>
    private async Task InitAnnotationAsync(
        string view, string text, string sidecar, IReadOnlyList<(string Key, string Value)> entries, CancellationToken cancellationToken)
    {
        string name = Path.GetFileName(view);

        if (ViewSettings.HasAnnotation(text))
        {
            _log.Line($"init: {name} already has a @tuipreview annotation");
            await OpenAsync(view, cancellationToken);
            await InformAsync($"{name} already has a // @tuipreview annotation; edit that line instead.", cancellationToken);
            return;
        }

        if (entries.Count == 0)
        {
            await InformAsync(
                $"{Path.GetFileName(sidecar)} already states everything init would write, so {name} was left as it is.",
                cancellationToken);
            return;
        }

        string line = PreviewPlanner.AnnotationLine(entries, text.Contains("\r\n") ? "\r\n" : "\n");

        ITextDocumentSnapshot document = await _extensibility.Documents().OpenTextDocumentAsync(
            new Uri(view), cancellationToken);
        bool wasDirty = document.IsDirty;

        await _extensibility.Editor().EditAsync(
            batch =>
            {
                var editor = document.AsEditable(batch);
                editor.Insert(0, line);
                if (!wasDirty)
                {
                    editor.Save();
                }
            },
            cancellationToken);

        _log.Line($"init: added \"{line.Trim()}\" to {name}");
    }

    /// <summary>Stops every preview this extension started.</summary>
    public async Task StopAllAsync(CancellationToken cancellationToken)
    {
        int count = 0;
        foreach (string view in _running.Keys.ToList())
        {
            if (_running.TryRemove(view, out Process? process))
            {
                count += Stop(process) ? 1 : 0;
            }
        }

        _log.Line($"stopAll: stopped {count} preview(s)");
        await InformAsync(
            count == 0 ? "No previews were running." : $"Stopped {count} preview{(count == 1 ? string.Empty : "s")}.",
            cancellationToken);
    }

    /// <summary>
    /// Opens a copy of the log in the editor.
    /// <para>
    /// A copy, because the extension has no way to bring its Output window pane to the front, and
    /// opening the live file would have Visual Studio ask to reload it after every logged line. The
    /// live log is also under View &gt; Output, in the "TUI Preview" pane.
    /// </para>
    /// </summary>
    public async Task ShowLogAsync(CancellationToken cancellationToken)
    {
        string copy = _log.Snapshot();
        await OpenAsync(copy, cancellationToken);
    }

    /// <summary>
    /// Keeps a running preview in step with unsaved edits.
    /// <para>
    /// Only views that are actually being previewed are touched, i.e. those with a scratch copy.
    /// Rewriting that copy is enough: the preview polls its timestamp, so no restart is needed.
    /// </para>
    /// <para>
    /// Debounced, because a rebuild costs a full compile-instantiate cycle and the editor reports
    /// every keystroke. Without this, typing a word would queue ten rebuilds and the preview would
    /// lag behind the typing.
    /// </para>
    /// </summary>
    public void BufferChanged(string view, Func<string> text)
    {
        if (!_live || !_scratch.IsMirrored(view))
        {
            return;
        }

        var debounce = new CancellationTokenSource();
        if (_pendingMirrors.TryGetValue(view, out var previous))
        {
            previous.Cancel();
        }

        _pendingMirrors[view] = debounce;

        // The text is captured now, from the snapshot the change was reported with: by the time the
        // delay is over, this snapshot is still the right one to compare with the next.
        string content = text();
        _ = Task.Delay(250, debounce.Token).ContinueWith(
            _ =>
            {
                _pendingMirrors.TryRemove(new KeyValuePair<string, CancellationTokenSource>(view, debounce));
                try
                {
                    _scratch.Mirror(view, content);
                    _log.Line($"mirrored {Path.GetFileName(view)} (edit)");
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    _log.Line($"could not mirror {Path.GetFileName(view)}: {ex.Message}");
                }
            },
            debounce.Token,
            TaskContinuationOptions.OnlyOnRanToCompletion,
            TaskScheduler.Default);
    }

    /// <summary>
    /// A previewed view's editor was closed. Its unsaved changes, if any, were just discarded, so
    /// the scratch copy goes back to what is on disk rather than showing an edit that no longer
    /// exists anywhere.
    /// </summary>
    public void BufferClosed(string view)
    {
        if (!_scratch.IsMirrored(view) || !File.Exists(view))
        {
            return;
        }

        BufferChanged(view, () => File.ReadAllText(view));
    }

    public void Dispose()
    {
        foreach (Process process in _running.Values)
        {
            Stop(process);
        }

        _running.Clear();
        _log.Dispose();
    }

    private void Start(PreviewRequest request, PreviewMode mode)
    {
        if (_running.TryRemove(request.View, out Process? previous) && Stop(previous))
        {
            _log.Line($"stopped the previous preview of {Path.GetFileName(request.View)}");
        }

        // One script per view and mode, rewritten each time, so the temp folder does not fill up.
        string script = _scratch.Sibling(
            $"launch-{Path.GetFileNameWithoutExtension(_scratch.PathFor(request.View))}-{mode.ToString().ToLowerInvariant()}.cmd");
        File.WriteAllText(script, PreviewCommand.LaunchScript(request, mode), new System.Text.UTF8Encoding(false));

        _log.Line($"running: {PreviewCommand.CommandLine(request, mode)}");
        _log.Line($"    via {script}");

        // Shell-executed, so Windows gives the script a console window of its own (or a tab in
        // Windows Terminal, when that is the default terminal). The extension host has no console
        // to share, and a preview started without one would run invisibly.
        Process? process = Process.Start(new ProcessStartInfo("cmd.exe", PreviewCommand.ScriptArguments(script))
        {
            UseShellExecute = true,
            WorkingDirectory = Path.GetDirectoryName(request.View)!,
        });

        if (process is null)
        {
            throw new PreviewException($"Windows did not start {script}.");
        }

        process.EnableRaisingEvents = true;
        process.Exited += (_, _) =>
        {
            // Only if it is still the current one: a replaced preview must not evict its successor.
            _running.TryRemove(new KeyValuePair<string, Process>(request.View, process));
        };
        _running[request.View] = process;
    }

    /// <summary>Kills a preview with everything it started. True if it was still running.</summary>
    private bool Stop(Process process)
    {
        try
        {
            if (process.HasExited)
            {
                return false;
            }

            // The tree, because the process is cmd.exe and the preview is its child.
            process.Kill(entireProcessTree: true);
            return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            _log.Line($"could not stop process {process.Id}: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// The view a command targets: the item right-clicked in Solution Explorer, or the document
    /// right-clicked in the editor. Both arrive as the active selection, which is authoritative;
    /// the active editor is only a fallback for an invocation that carries no selection at all.
    /// </summary>
    private async Task<string> PickViewAsync(IClientContext context, CancellationToken cancellationToken)
    {
        string? view = null;
        try
        {
            Uri? selected = await context.GetSelectedPathAsync(cancellationToken);
            if (selected is { IsFile: true })
            {
                view = selected.LocalPath;
                _log.Line($"target from selection: {view}");
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.Line($"no selection: {ex.Message}");
        }

        if (!PreviewPlanner.IsView(view))
        {
            try
            {
                ITextViewSnapshot? active = await context.GetActiveTextViewAsync(cancellationToken);
                if (active?.FilePath is { } path && PreviewPlanner.IsView(path))
                {
                    view = path;
                    _log.Line($"target from active editor: {view}");
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.Line($"no active editor: {ex.Message}");
            }
        }

        if (view is null)
        {
            throw new PreviewException(
                "No .tui.cs file could be determined. Open one, or right-click it in Solution Explorer.");
        }

        if (!PreviewPlanner.IsView(view))
        {
            throw new PreviewException($"{Path.GetFileName(view)} is not a previewable view. Expected a name ending in .tui.cs.");
        }

        if (!File.Exists(view))
        {
            throw new PreviewException($"{view} does not exist on disk. Save it first.");
        }

        return view;
    }

    /// <summary>
    /// The view's text, from the open document when there is one. Settings are read from this
    /// rather than from disk so that an annotation the user has just typed but not saved is the
    /// one that applies.
    /// </summary>
    private async Task<ViewText> ReadViewAsync(string view, CancellationToken cancellationToken)
    {
        try
        {
            DocumentsExtensibility documents = _extensibility.Documents();
            DocumentSnapshot? open = await documents.GetOpenDocumentAsync(new Uri(view), cancellationToken);
            if (open is not null)
            {
                ITextDocumentSnapshot? text = await documents.GetTextDocumentSnapshotAsync(open, cancellationToken);
                if (text is not null)
                {
                    return new ViewText(TextOf(text), IsOpen: true, IsDirty: open.IsDirty);
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Not open, or not a text document: either way the file on disk is the truth.
            _log.Line($"reading {Path.GetFileName(view)} from disk ({ex.GetType().Name}: {ex.Message})");
        }

        try
        {
            return new ViewText(File.ReadAllText(view), IsOpen: false, IsDirty: false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new PreviewException($"Could not read {Path.GetFileName(view)}: {ex.Message}");
        }
    }

    /// <summary>
    /// Determines the class to preview, asking only when neither the view's settings, the Tools
    /// &gt; Options override nor the file's own declarations yield one.
    /// </summary>
    private async Task<string> ResolveClassNameAsync(
        string view, string text, ViewSettings settings, EditorSettings editor, CancellationToken cancellationToken)
    {
        if (PreviewPlanner.ClassName(settings, editor, text) is { } known)
        {
            _log.Line($"class: {known}");
            return known;
        }

        string? entered = await _extensibility.Shell().ShowPromptAsync(
            $"Could not determine the class name in {Path.GetFileName(view)}. Enter the fully qualified class name.",
            InputPromptOptions.Default with { Title = "TUI Preview: class name", DefaultText = string.Empty },
            cancellationToken);

        if (string.IsNullOrWhiteSpace(entered))
        {
            throw new PreviewException("Cancelled: no class name was provided.");
        }

        return entered.Trim();
    }

    public static string TextOf(ITextDocumentSnapshot document)
    {
        var buffer = new char[document.Length];
        document.Text.CopyTo(buffer);
        return new string(buffer);
    }

    private async Task OpenAsync(string file, CancellationToken cancellationToken) =>
        await _extensibility.Documents().OpenDocumentAsync(new Uri(file), cancellationToken);

    private async Task InformAsync(string message, CancellationToken cancellationToken) =>
        await _extensibility.Shell().ShowPromptAsync(
            $"TUI Preview: {message}", PromptOptions.OK with { Title = "TUI Preview" }, cancellationToken);

    /// <summary>A message with a button to the log, which is where the details are.</summary>
    private async Task OfferLogAsync(string message, CancellationToken cancellationToken)
    {
        try
        {
            bool showLog = await _extensibility.Shell().ShowPromptAsync(
                message,
                new PromptOptions<bool>
                {
                    Title = "TUI Preview",
                    Choices =
                    {
                        { "Show Log", true },
                        { "Close", false },
                    },
                    DefaultChoiceIndex = 1,
                    DismissedReturns = false,
                },
                cancellationToken);

            if (showLog)
            {
                await ShowLogAsync(cancellationToken);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.Line($"could not show a message: {ex.Message}");
        }
    }

    private static string? NullIfBlank(string value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private sealed record ViewText(string Text, bool IsOpen, bool IsDirty);

    private enum InitTarget
    {
        None,
        Sidecar,
        Annotation,
    }
}
