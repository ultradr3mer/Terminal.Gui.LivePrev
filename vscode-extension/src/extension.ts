import * as cp from "child_process";
import * as crypto from "crypto";
import * as fs from "fs";
import * as os from "os";
import * as path from "path";
import * as vscode from "vscode";

/** The three ways a preview can be launched; they map 1:1 onto the CLI's own flags. */
type Mode = "render" | "watch" | "interactive";

/**
 * How the preview will be invoked: the CLI as a built binary or through `dotnet run`, or the
 * view's own app, which hosts the preview itself through TuiPreview.Host.
 */
type Launcher =
    | { kind: "exe"; command: string }
    | { kind: "dotnet"; project: string }
    /** `dll` is the shadow copy to run, set by {@link prepareApp} just before launching. */
    | { kind: "app"; project: string; dll?: string };

interface Resolved {
    launcher: Launcher;

    /** Which of the two authoring paths this is. Decides --xaml vs --view. */
    kind: ViewKind;

    /** The file the user picked, on disk. */
    xaml: string;
    className: string;

    /**
     * The file the CLI should actually read, and the real file it mirrors.
     *
     * Usually the same path. They differ when the editor is holding unsaved changes: the CLI
     * can only read from disk, so it gets the scratch copy, and `codeBehind` has to be passed
     * explicitly because the scratch copy has no sibling `.cs` of its own.
     */
    previewPath: string;
    codeBehind?: string;
}

/**
 * Live terminals, keyed by XAML path *and* purpose.
 *
 * Keying by path alone is not enough: terminal env is fixed at creation, so a generated-code
 * dump (TUI_DUMP=1) that reused a terminal opened for a normal preview (TUI_ANSI=0) would
 * silently never dump. Purposes therefore get separate terminals.
 *
 * Reuse within a purpose is still deliberate. Watch and interactive modes run until stopped,
 * so a new terminal per invocation would leak one process per right-click with no obvious
 * way to find them.
 */
const terminals = new Map<string, vscode.Terminal>();

/** The shadow copy each hosted-app preview runs from, deleted when its terminal closes. */
const appCopies = new Map<vscode.Terminal, string>();

/** Disposables for onDidCloseTerminal, so the extension host can tear them down. */
const subscriptions: vscode.Disposable[] = [];

/**
 * Diagnostics channel.
 *
 * A toast is a bad place to find out why a preview did not start: it auto-dismisses, and
 * "nothing happened" is indistinguishable from "the extension never activated". Every step
 * is logged here so the cause is recoverable after the fact.
 */
let log: vscode.OutputChannel | undefined;

function logLine(message: string): void {
    log?.appendLine(`[${new Date().toLocaleTimeString()}] ${message}`);
}

/**
 * Bumped whenever the manifest or this file changes in a way that needs a host restart.
 * Surfaced in the status bar and the log so "command not found" is never a mystery: a
 * stale host runs old code while VS Code shows the new menu entries from package.json.
 */
const BUILD = "2026-09-28.1";

/** Shows which build is live, so a stale Extension Development Host is immediately visible. */
let status: vscode.StatusBarItem | undefined;

/**
 * Scratch copies of the XAML, keyed by the real file's path.
 *
 * The CLI reads the XAML from disk, so previewing an unsaved buffer means writing that buffer
 * somewhere the CLI can read. The path has to be *stable* across edits, because the interactive
 * host notices changes by polling that file's timestamp: a fresh temp file per keystroke would
 * leave the running preview watching a path that no longer exists. One file per real XAML,
 * rewritten in place, is what makes the existing poll-and-rebuild machinery work unmodified.
 *
 * The temp file also has to keep the `.tui.xaml` extension, because the pipeline keys the
 * generated class and the code-behind convention off the file name.
 */
const scratchFiles = new Map<string, string>();

/** Where a real XAML's unsaved buffer is mirrored, created on first use. */
function scratchPathFor(realPath: string): string {
    const existing = scratchFiles.get(realPath);
    if (existing) {
        return existing;
    }

    // Hashed rather than derived from the name, because the name can contain characters that
    // are illegal in a path and because two files must not collide on a shared name.
    const hash = crypto.createHash("sha1").update(realPath).digest("hex").slice(0, 12);
    const dir = path.join(os.tmpdir(), "tui-live-preview");
    fs.mkdirSync(dir, { recursive: true });

    // The suffix is preserved, and it has to be. The pipeline keys the code-behind
    // convention off the file name, and the extension keys its own file matching off it, so a
    // .tui.cs view mirrored to a .tui.xaml name (or vice versa) would change which authoring
    // path the file is treated as.
    const suffix = realPath.endsWith(".tui.cs") ? ".tui.cs" : ".tui.xaml";
    const stem = path.basename(realPath).slice(0, -suffix.length);
    const scratch = path.join(dir, `${stem}-${hash}${suffix}`);
    scratchFiles.set(realPath, scratch);
    return scratch;
}

/**
 * Mirrors the document's current text to its scratch file and returns the path to preview.
 *
 * Writes unconditionally: a document that is clean and one that has been edited back to its
 * saved contents must both be reflected, and mtime granularity makes a same-length rewrite
 * invisible to the CLI's timestamp poll if we skipped it.
 */
function mirrorDocument(doc: vscode.TextDocument): string {
    const real = doc.uri.fsPath;
    const scratch = scratchPathFor(real);

    fs.writeFileSync(scratch, doc.getText(), "utf8");
    logLine(`mirrored ${path.basename(real)} (${doc.isDirty ? "unsaved" : "saved"}) -> ${scratch}`);

    return scratch;
}

/**
 * The code-behind that belongs with a previewed XAML.
 *
 * Required whenever the XAML being compiled is the scratch copy, because the pipeline's own
 * convention looks for a sibling `.cs` and the scratch file has no siblings. Without this the
 * preview silently falls back to a stub with no ViewModel and every `{Bind ...}` fails to
 * compile, which looks like a generator bug rather than a missing argument.
 *
 * Returns undefined when there is no code-behind at all, so the CLI keeps deriving it itself
 * and behaves exactly as it did before this existed.
 */
function codeBehindFor(realPath: string): string | undefined {
    const sibling = realPath.replace(/\.xaml$/i, ".cs");
    if (fs.existsSync(sibling)) {
        return sibling;
    }

    // No sibling: say so once, because the stub's no-op handlers are otherwise a silent
    // surprise ("the buttons render but do nothing").
    logLine(`no code-behind beside ${path.basename(realPath)}; handlers will be no-ops`);
    return undefined;
}

function showLog(): void {
    log?.show(true);
}

/**
 * Starts a preview in a terminal.
 *
 * A terminal rather than an output channel because `--interactive` needs a real TTY: it
 * drives Terminal.Gui's ANSI driver, reads keys, and owns the screen. An output channel
 * would render a static frame and swallow all input.
 */
async function launch(mode: Mode, picked?: vscode.Uri): Promise<void> {
    logLine(`launch requested: mode=${mode}`);

    const resolved = await resolve(picked);
    const cfg = vscode.workspace.getConfiguration("tuiLivePreview");

    const extra: string[] = [];
    if (mode === "watch") {
        extra.push("--watch");
    } else if (mode === "interactive") {
        extra.push("--interactive");
    } else {
        // Width/height only apply to the printed render; interactive fills the terminal.
        extra.push("--width", String(cfg.get<number>("width", 80)));
        extra.push("--height", String(cfg.get<number>("height", 24)));
    }

    const label =
        mode === "interactive"
            ? "TUI Preview (interactive)"
            : mode === "watch"
              ? "TUI Preview (watch)"
              : "TUI Preview";

    if (cfg.get<boolean>("confirmation", false)) {
        const answer = await vscode.window.showInformationMessage(
            `Preview ${path.basename(resolved.xaml)} as ${resolved.className}?`,
            "Start",
            "Cancel"
        );
        if (answer !== "Start") {
            return;
        }
    }

    let appCopy: string | undefined;
    if (resolved.launcher.kind === "app") {
        resolved.launcher.dll = await prepareApp(resolved.launcher.project);
        appCopy = path.dirname(resolved.launcher.dll);

        // A fresh terminal: typing the new command into one whose preview still runs would
        // arrive as keystrokes. Closing it also releases its shadow copy.
        const running = terminals.get(resolved.xaml);
        if (running) {
            terminals.delete(resolved.xaml);
            running.dispose();
        }
    }

    // One shared terminal per XAML across render/watch/interactive: those are all previews
    // of the same view, and each is a long-running process that must not be duplicated.
    const terminal = openTerminal(resolved.xaml, label, {
        TUI_ANSI: cfg.get<boolean>("ansi", false) ? "1" : "0",
    });
    if (appCopy) {
        appCopies.set(terminal, appCopy);
    }

    const command = commandPrefix(resolved.launcher) + commandLine(resolved, extra);
    logLine(`running: ${command}`);
    terminal.sendText(command);

    // preserveFocus = false: the whole point is to watch the preview appear, and leaving
    // focus in the editor hides the fact that anything ran at all.
    terminal.show(false);
}

/**
 * Resolves the target file, its class name and how to launch the CLI.
 *
 * Each step throws a message naming the missing piece, because "it didn't work" is the
 * least useful thing an extension can tell you.
 */
async function resolve(picked?: vscode.Uri): Promise<Resolved> {
    const clicked = pickViewUri(picked);
    if (!clicked) {
        throw new Error(
            "No .tui.cs file could be determined. Open one, or right-click it in the explorer."
        );
    }
    if (clicked.scheme !== "file") {
        throw new Error("Only files on disk can be previewed (unsaved editors are not supported).");
    }

    // A .tui.cs with a .tui.xaml beside it is that file's code-behind, so the file to preview
    // is the XAML. `xaml` is what the user acted on; `source` is what the CLI reads.
    const target = resolveViewTarget(clicked.fsPath);
    if (!target) {
        throw new Error(
            `${path.basename(clicked.fsPath)} is not a previewable view. ` +
            `Expected a name ending in .tui.cs.`
        );
    }

    // The title, the .previewrc and the code-behind lookup are all keyed on the file the user
    // recognises. For a paired view that is the .tui.cs they right-clicked, not the scratch
    // copy of the XAML behind it.
    const xaml = clicked.fsPath;
    const kind = target.kind;
    const source = target.source;

    const className = await resolveClassName(vscode.Uri.file(source), kind);
    const launcher = resolveLauncher(xaml);

    // Whether to mirror the editor buffer rather than trusting what is on disk. Off means the
    // preview always reflects the last save, which is what you want from a scripted render and
    // what makes the output reproducible.
    const cfg = vscode.workspace.getConfiguration("tuiLivePreview");
    const live = cfg.get<boolean>("live", true);

    // Mirror whichever file actually holds the view, so an unsaved edit to either half lands.
    const openDoc = vscode.workspace.textDocuments.find(
        (d) => d.uri.scheme === "file" && d.uri.fsPath === source
    );

    let previewPath = source;
    if (openDoc && live) {
        previewPath = mirrorDocument(openDoc);
    } else if (openDoc?.isDirty) {
        logLine(
            `${path.basename(source)} has unsaved changes, but tuiLivePreview.live is off; ` +
            `previewing the last saved version.`
        );
    }

    // A paired XAML needs its own code-behind named explicitly, because the scratch copy of
    // the XAML has no sibling. A standalone .tui.cs view is the whole class and gets none.
    const codeBehind = kind === "tuiXaml" ? codeBehindFor(source) : undefined;

    logLine(
        `resolved: ${kind} xaml=${xaml} source=${source} class=${className} ` +
        `launcher=${describeLauncher(launcher)}` +
        (previewPath === source ? "" : ` preview=${previewPath}`) +
        (codeBehind ? ` codeBehind=${codeBehind}` : "")
    );

    return {
        launcher,
        kind,
        xaml,
        className,
        previewPath,
        codeBehind,
    };
}

/**
 * Picks the XAML to preview.
 *
 * Order matters and the first entry is the important one: when invoked from a context menu
 * VS Code passes the clicked resource, which is authoritative. Right-clicking a file in the
 * explorer very often targets a file that is NOT the active tab, and preferring the active
 * editor there would preview the wrong window. The active editor is only a fallback for
 * palette invocations, which pass no argument at all.
 */
function pickViewUri(picked?: vscode.Uri): vscode.Uri | undefined {
    if (picked && picked.scheme === "file") {
        logLine(`target from context menu: ${picked.fsPath}`);
        return picked;
    }

    const active = vscode.window.activeTextEditor?.document.uri;
    if (active && active.scheme === "file") {
        logLine(`target from active editor: ${active.fsPath}`);
        return active;
    }

    for (const folder of vscode.workspace.workspaceFolders ?? []) {
        const found = findViewFile(folder.uri.fsPath);
        if (found) {
            logLine(`target from workspace search: ${found}`);
            return vscode.Uri.file(found);
        }
    }

    logLine("no target found (no context-menu uri, no active editor, no workspace match)");
    return undefined;
}

/** Depth-limited search for a previewable view, used only when nothing suitable is open. */
function findViewFile(root: string, depth = 4): string | undefined {
    if (depth < 0) {
        return undefined;
    }

    let entries: fs.Dirent[];
    try {
        entries = fs.readdirSync(root, { withFileTypes: true });
    } catch {
        return undefined;
    }

    // Files before directories: an explicit top-level Counter.tui.cs beats a nested one.
    // .tui.cs is checked first because it needs no generator and is the preferred authoring
    // path, so a workspace holding both should find the C# view first.
    for (const wanted of [".tui.cs", ".tui.xaml"] as const) {
        for (const entry of entries) {
            if (entry.isFile() && entry.name.endsWith(wanted)) {
                return path.join(root, entry.name);
            }
        }
    }

    for (const entry of entries) {
        // Skip build output and dependencies: large, and never contain preview sources.
        if (!entry.isDirectory() || entry.name === "bin" || entry.name === "obj"
            || entry.name === "node_modules" || entry.name.startsWith(".")) {
            continue;
        }
        const found = findViewFile(path.join(root, entry.name), depth - 1);
        if (found) {
            return found;
        }
    }
    return undefined;
}

/**
 * Determines the `--class` value.
 *
 * The CLI requires it and will not infer it, so this is the friction the context menu
 * exists to remove: read `x:Class` out of the XAML and only prompt if it is absent.
 */
/**
 * Determines the `--class` value.
 *
 * The CLI requires it and will not infer it, so this is the friction the context menu
 * exists to remove. Each source states its own class name, in its own way:
 *
 *   `.tui.xaml` - the `x:Class` attribute
 *   `.tui.cs`   - the `namespace` plus the class declaration
 *
 * Only when neither yields one does the user get asked.
 */
async function resolveClassName(uri: vscode.Uri, kind: ViewKind): Promise<string> {
    const override = vscode.workspace
        .getConfiguration("tuiLivePreview")
        .get<string>("className", "")
        .trim();
    if (override) {
        return override;
    }

    if (kind === "tuiCs") {
        const fromCSharp = readClassFromCSharp(uri.fsPath);
        if (fromCSharp) {
            return fromCSharp;
        }
    } else {
        const fromXaml = readXClass(uri.fsPath);
        if (fromXaml) {
            return fromXaml;
        }
    }

    const entered = await vscode.window.showInputBox({
        title: "TUI Preview: class name",
        prompt:
            `Could not determine the class name in ${path.basename(uri.fsPath)}. ` +
            `Enter the fully qualified class name.`,
        placeHolder: "MyApp.Views.MyWindow",
        ignoreFocusOut: true,
        validateInput: (value) =>
            value.trim().length === 0 ? "The CLI requires a non-empty --class." : null,
    });

    if (!entered) {
        throw new Error("Cancelled: no class name was provided.");
    }
    return entered.trim();
}

/**
 * Extracts x:Class from the XAML text.
 *
 * A regex rather than an XML parser on purpose: these files use a custom namespace
 * (http://schemas.gui-cs.github.io/tui/2026/xaml) and are often mid-edit, so a strict parse
 * would fail on exactly the broken file you most want to preview. Both quote styles are
 * accepted since the attribute is matched in the source, not in a parsed DOM.
 */
function readXClass(file: string): string | undefined {
    let text: string;
    try {
        text = fs.readFileSync(file, "utf8");
    } catch {
        return undefined;
    }

    const match = /\bx:Class\s*=\s*(["'])([^"']+)\1/.exec(text);
    return match?.[2]?.trim() || undefined;
}

/**
 * The two kinds of file this extension previews.
 *
 * A view is authored in one of two ways, and they are not equivalent:
 *
 *   `tuiXaml` - a `.tui.xaml` compiled through the source generator, with an optional
 *               `.tui.cs` code-behind. The generator is the most experimental part of the
 *               tool, so this is the legacy path.
 *   `tuiCs`   - a plain `.tui.cs` compiled on its own, no generator, no stub. The C#
 *               compiler is the only thing between the file and the Terminal.Gui API.
 *
 * The `.tui.cs` suffix is what makes this matchable: a bare `.cs` would put the whole
 * workspace in scope, so the menus would appear on every source file. `.tui.` is the same
 * convention the XAML path already uses for its code-behind.
 */
type ViewKind = "tuiXaml" | "tuiCs";

/** True when the path is a file the user can right-click to preview. */
function isViewEntry(file: string): boolean {
    return file.endsWith(".tui.cs");
}

/**
 * Resolves a right-clicked `.tui.cs` to the file that should actually be previewed, and how.
 *
 * The two names are the same documented convention for different things, so a `.tui.cs` can
 * be either:
 *
 *   - a **standalone view**, compiled on its own with `--view` and no generator; or
 *   - the **code-behind** of a `.tui.xaml` of the same stem, in which case the file to
 *     preview is that XAML, with its own sibling .cs passed as `--code-behind`.
 *
 * Only the sibling tells them apart, because the two look identical from the name alone. A
 * code-behind cannot be previewed as a view: it is a partial class whose controls exist only
 * in the generated half, so instantiating it standalone cannot work.
 *
 * The menus are offered on `.tui.cs` only. Offering them on `.tui.xaml` too would be
 * redundant for the C# path and would be actively misleading for a paired file, where both
 * entries would appear to preview the same view.
 */
function resolveViewTarget(file: string): { kind: ViewKind; source: string } | undefined {
    if (!isViewEntry(file)) {
        return undefined;
    }

    const xamlSibling = file.replace(/\.tui\.cs$/, ".tui.xaml");
    if (fs.existsSync(xamlSibling)) {
        return { kind: "tuiXaml", source: xamlSibling };
    }

    return { kind: "tuiCs", source: file };
}

/** The CLI flag that selects this kind. */
function sourceFlagFor(kind: ViewKind): "--view" | "--xaml" {
    return kind === "tuiCs" ? "--view" : "--xaml";
}

/**
 * Extracts the fully qualified class name from a C# file.
 *
 * Reads `namespace` plus the first type declaration. A `--view` file has no `x:Class` to
 * read, so without this every C# preview would prompt for a name the file already states.
 *
 * The type declared is taken to be the view: the file is named after it and the CLI only
 * ever instantiates one class from it, so taking the first non-nested, non-partial,
 * non-record, non-enum, non-interface declaration is both the simple rule and the correct
 * one for a file that exists to define a single view.
 */
function readClassFromCSharp(file: string): string | undefined {
    let text: string;
    try {
        text = fs.readFileSync(file, "utf8");
    } catch {
        return undefined;
    }

    // Strip comments first, so a commented-out class cannot be picked up. A line comment
    // swallows the rest of the line; a block comment is removed wholesale.
    const withoutComments = text
        .replace(/\/\*[\s\S]*?\*\//g, " ")
        .replace(/\/\/[^\n]*/g, " ");

    // File-scoped (namespace X;) first, then the block form.
    const fileScoped = /^\s*namespace\s+([A-Za-z_][\w.]*)\s*;/m.exec(withoutComments);
    const blockScoped = /^\s*namespace\s+([A-Za-z_][\w.]*)\s*\{/m.exec(withoutComments);
    const ns = fileScoped?.[1] ?? blockScoped?.[1];

    // A type declaration that opens its own body: "class X {", "sealed class X {".
    // Excludes nested types, which are indented in practice, by requiring the declaration to
    // start at the beginning of a line.
    const type = /^(?:[^\S\n]*)((?:(?:public|internal|sealed|abstract|partial|static|unsafe|new)\s+)*)class\s+([A-Za-z_]\w*)/m.exec(
        withoutComments
    );
    if (!type) {
        return undefined;
    }

    // `partial` means another file supplies the rest, so this half may well hold no
    // constructor and the class may not even be the one to instantiate. Refuse to guess.
    if (/(?:^|\s)partial\s/.test(type[1])) {
        return undefined;
    }

    return ns ? `${ns}.${type[2]}` : type[2];
}


/**
 * Finds how to run the CLI.
 *
 * Preference order: explicit setting, then a built binary, then `dotnet run`. The built
 * binary matters because previews are frequent and interactive — paying a full restore and
 * build on every right-click would make the extension feel broken, whereas `dotnet run`
 * after a successful build is incremental and quick.
 */
function resolveLauncher(file: string): Launcher {
    const cfg = vscode.workspace.getConfiguration("tuiLivePreview");
    const launchMode = cfg.get<string>("launchMode", "auto");

    // An app that hosts the preview itself compiles the view against exactly what it runs
    // with, so in auto mode it wins over the CLI.
    if (launchMode !== "cli") {
        const owning = findOwningProject(file);
        if (owning && isHostingApp(owning)) {
            return { kind: "app", project: owning };
        }
        if (launchMode === "app") {
            throw new Error(
                owning
                    ? `tuiLivePreview.launchMode is "app", but ${path.basename(owning)} is not an ` +
                      `executable that references TuiPreview.Host.`
                    : `tuiLivePreview.launchMode is "app", but no project was found above ` +
                      `${path.basename(file)}.`
            );
        }
    }

    const configured = cfg.get<string>("cliPath", "").trim();

    if (configured) {
        if (!fs.existsSync(configured)) {
            throw new Error(`tuiLivePreview.cliPath does not exist: ${configured}`);
        }
        return configured.endsWith(".csproj")
            ? { kind: "dotnet", project: configured }
            : { kind: "exe", command: configured };
    }

    for (const root of workspaceRoots()) {
        const built = findBuiltCli(root);
        if (built) {
            return { kind: "exe", command: built };
        }
    }

    for (const root of workspaceRoots()) {
        const project = path.join(root, "cli", "cli.csproj");
        if (fs.existsSync(project)) {
            return { kind: "dotnet", project };
        }
    }

    throw new Error(
        "Could not find the preview CLI. Set tuiLivePreview.cliPath to cli.exe or to cli.csproj."
    );
}

function describeLauncher(launcher: Launcher): string {
    switch (launcher.kind) {
        case "exe":
            return launcher.command;
        case "dotnet":
            return `dotnet run ${launcher.project}`;
        case "app":
            return `app ${launcher.project}`;
    }
}

/**
 * An executable that references TuiPreview.Host, directly or through a
 * Directory.Build.props (which only shows up in the restore output).
 */
function isHostingApp(project: string): boolean {
    let text: string;
    try {
        text = fs.readFileSync(project, "utf8");
    } catch {
        return false;
    }

    if (!/<OutputType>\s*(Exe|WinExe)\s*<\/OutputType>/i.test(text)) {
        return false;
    }
    if (/TuiPreview\.Host/i.test(text)) {
        return true;
    }

    try {
        const assets = path.join(path.dirname(project), "obj", "project.assets.json");
        return fs.readFileSync(assets, "utf8").includes('"TuiPreview.Host/');
    } catch {
        return false;
    }
}

interface ProcessResult {
    code: number;
    output: string;
}

/** Runs a process to completion and never rejects. Replaceable, so tests need no real dotnet. */
let runProcess = (command: string, args: string[], cwd: string): Promise<ProcessResult> =>
    new Promise((resolve) => {
        cp.execFile(
            command,
            args,
            { cwd, windowsHide: true, maxBuffer: 16 * 1024 * 1024 },
            (err, stdout, stderr) => {
                // A number is the exit code; a string (ENOENT) means it never started.
                const code = err ? (typeof err.code === "number" ? err.code : 1) : 0;
                resolve({ code, output: `${stdout}${stderr}${err && typeof err.code !== "number" ? err.message : ""}` });
            }
        );
    });

/** Test seam: replaces how dotnet is run. */
export function __setRunProcess(runner: typeof runProcess): void {
    runProcess = runner;
}

/**
 * Builds the app and returns a shadow copy of its output to run.
 *
 * The build may fail, and that is expected: the view file is part of the app, so a view with a
 * compile error breaks the app's build too. The last successful build then still hosts the
 * preview, which compiles the view from source and shows its errors in the log.
 *
 * The copy keeps the app's bin folder unlocked, so it can be rebuilt or debugged while the
 * preview runs.
 */
async function prepareApp(project: string): Promise<string> {
    const name = path.basename(project);
    const cwd = path.dirname(project);

    const build = await vscode.window.withProgress(
        { location: vscode.ProgressLocation.Notification, title: `TUI Preview: building ${name}` },
        () => runProcess("dotnet", ["build", project, "-nologo", "-v", "q"], cwd)
    );
    logLine(`dotnet build ${name}: exit ${build.code}${build.output.trim() ? `\n${build.output.trim()}` : ""}`);

    const query = await runProcess("dotnet", ["msbuild", project, "-nologo", "-getProperty:TargetPath"], cwd);
    const targetPath = query.output.trim();
    if (query.code !== 0 || !targetPath || !fs.existsSync(targetPath)) {
        throw new Error(
            `${name} has no build output to host the preview` +
            (build.code === 0 ? "" : ", and its build failed") +
            ". See the TUI Preview log."
        );
    }

    if (build.code !== 0) {
        logLine(`${name} did not build; hosting the preview in its last successful build`);
        vscode.window.setStatusBarMessage(`TUI Preview: ${name} did not build, using its last build`, 8000);
    }

    const hash = crypto.createHash("sha1").update(project).digest("hex").slice(0, 12);
    const copy = path.join(os.tmpdir(), "tui-live-preview", `app-${hash}-${Date.now()}`);
    fs.cpSync(path.dirname(targetPath), copy, { recursive: true });
    logLine(`shadow copy of ${name}: ${copy}`);

    return path.join(copy, path.basename(targetPath));
}

/** Deletes a terminal's shadow copy once its process has let go of the files. */
function releaseAppCopy(terminal: vscode.Terminal): void {
    const copy = appCopies.get(terminal);
    if (!copy) {
        return;
    }
    appCopies.delete(terminal);

    setTimeout(() => {
        try {
            fs.rmSync(copy, { recursive: true, force: true });
        } catch (err) {
            logLine(`could not remove ${copy}: ${String(err)}`);
        }
    }, 2000);
}

function workspaceRoots(): string[] {
    return (vscode.workspace.workspaceFolders ?? []).map((f) => f.uri.fsPath);
}

/**
 * Looks for cli/bin/<config>/<tfm>/cli.exe.
 *
 * Debug is preferred over Release because that is what someone iterating on the generator
 * wants, and the newest target framework first so a multi-targeted build does not resolve
 * to a stale output.
 */
function findBuiltCli(root: string): string | undefined {
    const binRoot = path.join(root, "cli", "bin");
    if (!fs.existsSync(binRoot)) {
        return undefined;
    }

    let configs: string[];
    try {
        configs = fs.readdirSync(binRoot);
    } catch {
        return undefined;
    }

    for (const config of ["Debug", "Release", ...configs]) {
        const configDir = path.join(binRoot, config);
        if (!fs.existsSync(configDir)) {
            continue;
        }

        let tfms: string[];
        try {
            tfms = fs
                .readdirSync(configDir)
                .filter((d) => d.startsWith("net"))
                .sort()
                .reverse();
        } catch {
            continue;
        }

        for (const tfm of tfms) {
            const exe = path.join(
                configDir,
                tfm,
                process.platform === "win32" ? "cli.exe" : "cli"
            );
            if (fs.existsSync(exe)) {
                return exe;
            }
            // Framework-dependent deployments on non-Windows run the dll via its host.
            const dll = path.join(configDir, tfm, "cli.dll");
            if (fs.existsSync(dll)) {
                return dll;
            }
        }
    }
    return undefined;
}

/**
 * Builds the shell command line for a preview.
 *
 * Paths are double-quoted because a workspace path routinely contains spaces
 * ("C:\Users\First Last\..."), and an unquoted path there fails in a way that looks like the
 * preview itself is broken rather than a quoting problem. Embedded quotes are escaped for
 * the same reason.
 *
 * The command name additionally needs the PowerShell call operator, see
 * {@link commandPrefix}.
 */
function commandLine(resolved: Resolved, extra: string[]): string {
    const quote = (value: string) => `"${value.replace(/"/g, '\\"')}"`;

    if (resolved.launcher.kind === "app") {
        if (!resolved.launcher.dll) {
            throw new Error("This command is not available for an app that hosts the preview.");
        }

        // The running app is the project, so there is no --project.
        return [
            "dotnet",
            quote(resolved.launcher.dll),
            "--tui-preview",
            "--view",
            quote(resolved.previewPath),
            "--class",
            quote(resolved.className),
            ...(resolved.previewPath === resolved.xaml
                ? []
                : ["--display-name", quote(resolved.xaml)]),
            ...extra,
        ].join(" ");
    }

    const head =
        resolved.launcher.kind === "exe"
            ? [quote(resolved.launcher.command)]
            : ["dotnet", "run", "--project", quote(resolved.launcher.project), "--"];

    return [
        ...head,
        // --xaml or --view, depending on how the view is authored. The CLI rejects both at
        // once, so the kind must be decided here rather than by passing both.
        sourceFlagFor(resolved.kind),
        // previewPath, not xaml: this is the scratch copy when the editor holds unsaved edits,
        // and it is the only file the CLI can actually read.
        quote(resolved.previewPath),
        // The CLI validates that this path exists and otherwise ignores it, so passing the
        // project we were launched from is correct in both cases.
        "--project",
        quote(projectArgument(resolved)),
        "--class",
        quote(resolved.className),
        // Only when the XAML we are compiling is a copy, i.e. when the sibling convention
        // would look in the wrong directory. Passing it unconditionally is harmless but
        // noisy in the log, and it would be wrong to imply the real file is always this one.
        ...(resolved.codeBehind ? ["--code-behind", quote(resolved.codeBehind)] : []),
        // Only when the compiled XAML is a scratch copy. The CLI uses this for the window
        // title and to find the .previewrc, both of which must sit beside the real file -
        // deriving them from the scratch path would put a content hash in the title and
        // strand the saved log height in %TEMP%.
        ...(resolved.previewPath === resolved.xaml
            ? []
            : ["--display-name", quote(resolved.xaml)]),
        ...extra,
    ].join(" ");
}

/**
 * The prefix a command name needs in the user's default shell.
 *
 * PowerShell and cmd.exe disagree about a line that starts with a quoted path, and there is
 * no single form both accept (verified against PowerShell 7.6 and cmd.exe):
 *
 *   cmd.exe      "C:\...\cli.exe" --xaml ...   works
 *   PowerShell   "C:\...\cli.exe" --xaml ...   ParserError: Unexpected token 'xaml'
 *
 * PowerShell parses a leading quoted string as a string *expression*, so the arguments that
 * follow are a syntax error. Its call operator makes it a command invocation instead:
 *
 *   PowerShell   & "C:\...\cli.exe" --xaml ... works
 *   cmd.exe      & "C:\...\cli.exe" --xaml ... "& was unexpected at this time."
 *
 * The `&` is a command separator in cmd.exe, which is why prefixing it unconditionally is
 * not a workaround — it trades one broken shell for the other. So the shell has to be
 * detected, and the call operator emitted only for PowerShell.
 *
 * Only the `exe` launcher needs this. `dotnet` is unquoted and resolves via PATH, so it
 * parses the same way in both.
 *
 * `vscode.env.shell` is the user's configured default profile, which is what a plain
 * `createTerminal` opens. An undefined value means VS Code could not determine it (common on
 * a first run before any profile is selected); PowerShell is the safer default there because
 * it is the modern Windows default, whereas assuming cmd.exe produces an opaque ParserError.
 */
function commandPrefix(launcher: Launcher): string {
    if (launcher.kind !== "exe") {
        return "";
    }

    const shell = (vscode.env.shell ?? "").toLowerCase();
    const isPowerShell =
        shell.includes("powershell") || shell.includes("pwsh") || shell === "";

    // Not a `ps1`/`.psm1` shim or a PowerShell-hosted profile under another name; the
    // substring checks above cover the real-world profile paths on Windows, macOS and Linux.
    return isPowerShell ? "& " : "";
}

/**
 * The --project value: the project the previewed file belongs to.
 *
 * The CLI compiles the view against that project's references, NuGet packages and usings, so
 * it has to be the view's own project - passing the preview tool's cli.csproj for a user's view
 * would compile it against the wrong dependencies. Found the way `dotnet build` would find it:
 * the nearest directory above the file holding exactly one .csproj. A directory with several is
 * ambiguous and skipped rather than guessed at.
 *
 * Starts from the real file, never the scratch copy, which lives in a temp folder with no
 * project around it. Falls back to the tool's own cli.csproj - always a real path, because the
 * CLI rejects a --project that does not exist.
 */
function projectArgument(resolved: Resolved): string {
    const owning = findOwningProject(resolved.xaml);
    if (owning) {
        return owning;
    }

    if (resolved.launcher.kind === "dotnet") {
        return resolved.launcher.project;
    }

    for (const root of workspaceRoots()) {
        const project = path.join(root, "cli", "cli.csproj");
        if (fs.existsSync(project)) {
            return project;
        }
    }
    return path.join(path.dirname(resolved.xaml), "cli.csproj");
}

/** The nearest single .csproj in the file's directory or any directory above it. */
function findOwningProject(file: string): string | undefined {
    let dir = path.dirname(file);
    for (;;) {
        let projects: string[] = [];
        try {
            projects = fs.readdirSync(dir).filter((n) => n.toLowerCase().endsWith(".csproj"));
        } catch {
            // Unreadable directory: keep walking, the way a missing one would.
        }

        if (projects.length === 1) {
            return path.join(dir, projects[0]);
        }

        const parent = path.dirname(dir);
        if (parent === dir) {
            return undefined;
        }
        dir = parent;
    }
}

/**
 * Returns the terminal for a (xaml, purpose) pair, creating it if needed.
 *
 * The env is applied only on creation, because that is the only time VS Code lets us set it.
 * Callers must therefore not rely on a changed setting taking effect on an existing
 * terminal; that is what the purpose in the key is for.
 */
function openTerminal(
    key: string,
    label: string,
    env: Record<string, string>
): vscode.Terminal {
    const existing = terminals.get(key);
    if (existing) {
        logLine(`reusing terminal "${label}"`);
        return existing;
    }

    const terminal = vscode.window.createTerminal({ name: label, env });
    terminals.set(key, terminal);
    logLine(`created terminal "${label}" (env ${JSON.stringify(env)})`);

    // Fires when the user closes the terminal. Without this the map would keep a disposed
    // terminal and every later preview for that file would silently do nothing.
    subscriptions.push(
        vscode.window.onDidCloseTerminal((closed) => {
            if (closed === terminal) {
                if (terminals.get(key) === terminal) {
                    terminals.delete(key);
                }
                releaseAppCopy(terminal);
            }
        })
    );

    return terminal;
}

export function activate(context: vscode.ExtensionContext): void {
    log = vscode.window.createOutputChannel("TUI Preview");
    context.subscriptions.push(log);
    logLine(`extension activated (build ${BUILD})`);
    logLine(`extension path: ${context.extensionPath}`);

    status = vscode.window.createStatusBarItem(vscode.StatusBarAlignment.Right, 100);
    status.text = `$(beaker) TUI ${BUILD}`;
    status.tooltip = "TUI Live Preview — click to show the log";
    status.command = "tuiLivePreview.showLog";
    status.show();
    context.subscriptions.push(status);

    /**
     * Keeps a running preview in step with unsaved edits.
     *
     * Only files that are actually being previewed are touched, and only when the scratch file
     * already exists — i.e. after the user has invoked a command on that file. Rewriting the
     * scratch file is enough to trigger a rebuild: the interactive host polls that file's
     * timestamp, so no IPC and no restart is needed.
     *
     * Debounced, because a rebuild costs a full generate-compile-instantiate cycle and
     * `onDidChangeTextDocument` fires on every keystroke. Without this, typing a word would
     * queue ten rebuilds and the preview would lag behind the typing.
     */
    const pending = new Map<string, NodeJS.Timeout>();
    context.subscriptions.push(
        vscode.workspace.onDidChangeTextDocument((e) => {
            const real = e.document.uri.fsPath;
            if (!scratchFiles.has(real)) {
                return; // not being previewed
            }
            if (!vscode.workspace.getConfiguration("tuiLivePreview").get<boolean>("live", true)) {
                return;
            }

            const existing = pending.get(real);
            if (existing) {
                clearTimeout(existing);
            }
            pending.set(
                real,
                setTimeout(() => {
                    pending.delete(real);
                    try {
                        mirrorDocument(e.document);
                    } catch (err) {
                        logLine(`could not mirror ${path.basename(real)}: ${String(err)}`);
                    }
                }, 250)
            );
        })
    );

    /**
     * Wraps a command so every failure is both logged and shown, with a button that opens
     * the log. A silently-swallowed rejection is indistinguishable from a dead menu entry,
     * which is exactly the failure mode that wastes the most time.
     */
    const guard =
        (name: string, body: (picked?: vscode.Uri) => Promise<void>) =>
        async (picked?: vscode.Uri) => {
            logLine(`--- ${name} invoked ---`);
            try {
                await body(picked);
            } catch (err) {
                const detail = err instanceof Error ? (err.stack ?? err.message) : String(err);
                logLine(`FAILED ${name}: ${detail}`);

                const choice = await vscode.window.showErrorMessage(
                    `TUI Preview: ${err instanceof Error ? err.message : String(err)}`,
                    "Show Log"
                );
                if (choice === "Show Log") {
                    showLog();
                }
            }
        };

    const register = (name: string, mode: Mode) =>
        context.subscriptions.push(
            vscode.commands.registerCommand(name, guard(name, (picked) => launch(mode, picked)))
        );

    register("tuiLivePreview.render", "render");
    register("tuiLivePreview.watch", "watch");
    register("tuiLivePreview.interactive", "interactive");

    context.subscriptions.push(
        vscode.commands.registerCommand(
            "tuiLivePreview.showGeneratedCode",
            guard("tuiLivePreview.showGeneratedCode", async (picked) => {
                const resolved = await resolve(picked);
                // TUI_DUMP=1 is the existing --gen-test hook that prints the generated C#.
                // Its own terminal, because env is fixed at creation and this one must not
                // inherit TUI_ANSI from a normal preview of the same file.
                const terminal = openTerminal(
                    `${resolved.xaml}::generated`,
                    "TUI Generated Code",
                    { TUI_DUMP: "1" }
                );
                const command = commandPrefix(resolved.launcher) + commandLine(resolved, ["--gen-test"]);
                logLine(`running: ${command}`);
                terminal.sendText(command);
                terminal.show(false);
            })
        )
    );

    context.subscriptions.push(
        vscode.commands.registerCommand("tuiLivePreview.showLog", () => showLog())
    );

    context.subscriptions.push(
        vscode.commands.registerCommand("tuiLivePreview.stopAll", () => {
            const count = terminals.size;
            for (const terminal of terminals.values()) {
                terminal.dispose();
            }
            terminals.clear();
            logLine(`stopAll: disposed ${count} terminal(s)`);
            void vscode.window.showInformationMessage(
                count === 0
                    ? "TUI Preview: no previews were running."
                    : `TUI Preview: stopped ${count} preview${count === 1 ? "" : "s"}.`
            );
        })
    );

    for (const sub of subscriptions) {
        context.subscriptions.push(sub);
    }

    // Logged last, once everything is wired. If a command is missing from this line, the
    // host is running an older out/extension.js than the manifest advertises — which is
    // exactly the state that surfaces as "command not found".
    void vscode.commands.getCommands(true).then((all) => {
        const mine = all.filter((n) => n.startsWith("tuiLivePreview.")).sort();
        logLine(`registered commands: ${mine.join(", ") || "(none!)"}`);
    });
}

export function deactivate(): void {
    for (const terminal of terminals.values()) {
        terminal.dispose();
    }
    terminals.clear();
    for (const sub of subscriptions) {
        sub.dispose();
    }
}
