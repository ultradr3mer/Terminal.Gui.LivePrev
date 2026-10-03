import * as crypto from "crypto";
import * as fs from "fs";
import * as os from "os";
import * as path from "path";
import * as vscode from "vscode";

/** The three ways a preview can be launched; they map 1:1 onto the tool's own flags. */
type Mode = "render" | "watch" | "interactive";

/**
 * How the `tuipreview` tool will be invoked: as an executable (a build, or the dotnet tool on
 * PATH), as a framework-dependent dll run by `dotnet` (the copy bundled with the extension), or
 * through `dotnet run` on its project.
 */
type Launcher =
    | { kind: "exe"; command: string }
    | { kind: "dll"; path: string }
    | { kind: "dotnet"; project: string };

interface Resolved {
    launcher: Launcher;

    /** The .tui.cs the user picked, on disk. */
    view: string;
    className: string;

    /**
     * The file the tool should actually read. Usually `view` itself; the scratch copy when the
     * editor holds unsaved changes, because the tool can only read from disk.
     */
    previewPath: string;

    /** Further .cs files to compile with the view, from the view's own settings. */
    extraFiles: string[];

    /** Assemblies to add to the tool's default reference set. */
    references: string[];

    /** The size the view asked for, so `launch` can prefer it over the workspace-wide setting. */
    width?: number;
    height?: number;
}

/**
 * Preview settings a view states for itself, gathered from the two places it can put them.
 *
 * The tool understands both as well, and it re-reads them itself; this exists so the extension
 * can report the effective values and pass `--class` explicitly, and so a view's `extra` files
 * can be shown and validated before anything is launched.
 */
interface ViewSettings {
    className?: string;
    width?: number;
    height?: number;
    extra: string[];

    /** Assemblies to add to the tool's default reference set. */
    references: string[];

    /** Where to find the tuipreview tool for this view. */
    cliPath?: string;

    warnings: string[];
}

/**
 * Live terminals, keyed by view path.
 *
 * Reuse is deliberate. Watch and interactive modes run until stopped, so a new terminal per
 * invocation would leak one process per right-click with no obvious way to find them.
 */
const terminals = new Map<string, vscode.Terminal>();

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
const BUILD = "2026-10-03.1";

/** Shows which build is live, so a stale Extension Development Host is immediately visible. */
let status: vscode.StatusBarItem | undefined;

/** Where the extension is installed, and so where its bundled copy of the tool lives. */
let extensionRoot: string | undefined;

/**
 * Scratch copies of views, keyed by the real file's path.
 *
 * The tool reads the view from disk, so previewing an unsaved buffer means writing that buffer
 * somewhere the tool can read. The path has to be *stable* across edits, because the
 * interactive preview notices changes by polling that file's timestamp: a fresh temp file per
 * keystroke would leave the running preview watching a path that no longer exists. One file
 * per real view, rewritten in place, is what makes the poll-and-rebuild machinery work.
 */
const scratchFiles = new Map<string, string>();

/** Where a real view's unsaved buffer is mirrored, created on first use. */
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

    // The suffix is kept so the copy is still recognisably a view in logs and diagnostics.
    const suffix = ".tui.cs";
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
 * invisible to the tool's timestamp poll if we skipped it.
 */
function mirrorDocument(doc: vscode.TextDocument): string {
    const real = doc.uri.fsPath;
    const scratch = scratchPathFor(real);

    fs.writeFileSync(scratch, doc.getText(), "utf8");
    logLine(`mirrored ${path.basename(real)} (${doc.isDirty ? "unsaved" : "saved"}) -> ${scratch}`);

    return scratch;
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
        // A view's own size wins over the setting, so one wide view does not make every
        // other preview in the workspace wide too.
        extra.push("--width", String(resolved.width ?? cfg.get<number>("width", 80)));
        extra.push("--height", String(resolved.height ?? cfg.get<number>("height", 24)));
    }

    const label =
        mode === "interactive"
            ? "TUI Preview (interactive)"
            : mode === "watch"
              ? "TUI Preview (watch)"
              : "TUI Preview";

    if (cfg.get<boolean>("confirmation", false)) {
        const answer = await vscode.window.showInformationMessage(
            `Preview ${path.basename(resolved.view)} as ${resolved.className}?`,
            "Start",
            "Cancel"
        );
        if (answer !== "Start") {
            return;
        }
    }

    // One shared terminal per view across render/watch/interactive: those are all previews
    // of the same view, and each is a long-running process that must not be duplicated.
    const terminal = openTerminal(resolved.view, label, {
        TUI_ANSI: cfg.get<boolean>("ansi", false) ? "1" : "0",
    });

    const command = commandPrefix(resolved.launcher) + commandLine(resolved, extra);
    logLine(`running: ${command}`);
    terminal.sendText(command);

    // preserveFocus = false: the whole point is to watch the preview appear, and leaving
    // focus in the editor hides the fact that anything ran at all.
    terminal.show(false);
}

/**
 * Resolves the target file, its class name and how to launch the tool.
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

    const view = clicked.fsPath;
    if (!isViewEntry(view)) {
        throw new Error(
            `${path.basename(view)} is not a previewable view. ` +
            `Expected a name ending in .tui.cs.`
        );
    }

    // The open document's text, when there is one. Settings are read from this rather than from
    // disk so that an annotation the user has just typed but not saved is the one that applies.
    const openDoc = vscode.workspace.textDocuments.find(
        (d) => d.uri.scheme === "file" && d.uri.fsPath === view
    );

    let text: string;
    try {
        text = openDoc?.getText() ?? fs.readFileSync(view, "utf8");
    } catch (err) {
        throw new Error(`Could not read ${path.basename(view)}: ${String(err)}`);
    }

    const settings = readViewSettings(view, text);
    for (const warning of settings.warnings) {
        logLine(`view settings: ${warning}`);
    }

    // Reported here rather than left to the tool's file check, which cannot say which of the
    // view's own settings asked for the file that is missing.
    for (const file of settings.extra) {
        if (!fs.existsSync(file)) {
            throw new Error(
                `${path.basename(view)} lists an extra file that does not exist: ${file}. ` +
                `Check its "extra" annotation or .tui.json.`
            );
        }
    }

    // A reference may be a dll or a directory of them, so both are accepted; anything else is a
    // typo that would otherwise preview a view that cannot compile.
    for (const reference of settings.references) {
        if (!fs.existsSync(reference)) {
            throw new Error(
                `${path.basename(view)} lists a reference that does not exist: ${reference}. ` +
                `Check its "reference" annotation or .tui.json.`
            );
        }
    }

    const className = await resolveClassName(view, settings);
    const launcher = resolveLauncher(view, settings);

    // Whether to mirror the editor buffer rather than trusting what is on disk. Off means the
    // preview always reflects the last save, which is what you want from a scripted render and
    // what makes the output reproducible.
    const cfg = vscode.workspace.getConfiguration("tuiLivePreview");
    const live = cfg.get<boolean>("live", true);

    let previewPath = view;
    if (openDoc && live) {
        previewPath = mirrorDocument(openDoc);
    } else if (openDoc?.isDirty) {
        logLine(
            `${path.basename(view)} has unsaved changes, but tuiLivePreview.live is off; ` +
            `previewing the last saved version.`
        );
    }

    logLine(
        `resolved: view=${view} class=${className} ` +
        `launcher=${describeLauncher(launcher)}` +
        (previewPath === view ? "" : ` preview=${previewPath}`) +
        (settings.extra.length > 0 ? ` extra=${settings.extra.join(",")}` : "") +
        (settings.references.length > 0 ? ` refs=${settings.references.join(",")}` : "") +
        (settings.cliPath ? ` cliPath=${settings.cliPath}` : "")
    );

    return {
        launcher,
        view,
        className,
        previewPath,
        extraFiles: settings.extra,
        references: settings.references,
        width: settings.width,
        height: settings.height,
    };
}

/**
 * Picks the view to preview.
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
    for (const entry of entries) {
        if (entry.isFile() && isViewEntry(entry.name)) {
            return path.join(root, entry.name);
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
 * Reads the settings a view states about itself.
 *
 * Two sources, in increasing order of precedence:
 *   1. a sidecar `MyView.tui.json` next to the view,
 *   2. a `// @tuipreview key=value ...` annotation in the view.
 *
 * The annotation wins because it is what the author is looking at, and because a sidecar cannot
 * describe an unsaved buffer: a dirty editor buffer and its stale neighbour would disagree, and
 * the buffer is the thing the user is actually looking at.
 *
 * `text` is passed in rather than read here because the live buffer may not be on disk yet, and
 * reading the file would silently preview settings the user had just edited away from.
 *
 * Bad values are collected as warnings rather than thrown. A typo in an annotation must not stop
 * a preview that is otherwise fine, and the warning names the file so it can be found again.
 */
function readViewSettings(view: string, text: string): ViewSettings {
    const settings: ViewSettings = { extra: [], references: [], warnings: [] };
    const baseDir = path.dirname(view);

    // A sidecar is optional and its absence is not a problem, so a read failure of any kind is
    // simply "no settings over here".
    const sidecar = view.replace(/\.tui\.cs$/, ".tui.json");
    let parsed: unknown;
    try {
        parsed = JSON.parse(stripJsonComments(fs.readFileSync(sidecar, "utf8")));
    } catch {
        parsed = undefined;
    }

    if (parsed !== undefined) {
        if (typeof parsed !== "object" || parsed === null || Array.isArray(parsed)) {
            settings.warnings.push(`${path.basename(sidecar)} must hold a JSON object; ignoring it.`);
        } else {
            applyJson(settings, parsed as Record<string, unknown>, baseDir, path.basename(sidecar));
        }
    }

    applyAnnotation(settings, text, baseDir, path.basename(view));

    return settings;
}

/** Applies one parsed source, validating each key and reporting the ones that are not usable. */
function applyJson(
    settings: ViewSettings,
    source: Record<string, unknown>,
    baseDir: string,
    origin: string
): void {
    for (const [key, value] of Object.entries(source)) {
        switch (key) {
            case "class":
                if (typeof value === "string") {
                    settings.className = value.trim();
                }
                break;

            case "clipath":
                if (typeof value === "string" && value.trim().length > 0) {
                    settings.cliPath = path.resolve(baseDir, value.trim());
                }
                break;

            case "width":
            case "height": {
                const size = positiveInt(value);
                if (size === undefined) {
                    settings.warnings.push(`${origin}: "${key}" must be a positive whole number.`);
                    break;
                }

                if (key === "width") {
                    settings.width = size;
                } else {
                    settings.height = size;
                }

                break;
            }

            case "extra":
                // A bare string as well as an array: a single extra file is the common case and an
                // array of one is noise.
                for (const entry of Array.isArray(value) ? value : [value]) {
                    if (typeof entry === "string" && entry.trim().length > 0) {
                        settings.extra.push(path.resolve(baseDir, entry.trim()));
                    }
                }

                break;

            case "reference":
                // Resolved too, because the tool takes absolute paths and the extension has to
                // report a bad one before anything is launched.
                for (const entry of Array.isArray(value) ? value : [value]) {
                    if (typeof entry === "string" && entry.trim().length > 0) {
                        settings.references.push(path.resolve(baseDir, entry.trim()));
                    }
                }

                break;

            default:
                settings.warnings.push(`${origin}: unknown key "${key}"; ignoring it.`);
                break;
        }
    }
}

/**
 * The annotation grammar: `// @tuipreview` then `key=value` pairs, which may be repeated on
 * several lines. A value may be double-quoted to carry spaces, and an unquoted value ends at
 * whitespace or a separator so several pairs fit on one line.
 *
 * Only line comments count. The view is C#, and a block comment wrapping preview configuration
 * is not something anyone writes.
 */
const ANNOTATION = /^[ \t]*\/\/[ \t]*@tuipreview\b(?<args>[^\n]*)/gm;
const PAIR = /(?<key>[A-Za-z_][\w-]*)[ \t]*=[ \t]*(?:"(?<quoted>[^"]*)"|(?<bare>[^\s,;]+))/g;

function applyAnnotation(
    settings: ViewSettings,
    text: string,
    baseDir: string,
    origin: string
): void {
    for (const line of text.matchAll(ANNOTATION)) {
        const args = line.groups?.args ?? "";
        for (const pair of args.matchAll(PAIR)) {
            const key = pair.groups!.key;
            const lower = key.toLowerCase();
            const value = (pair.groups!.quoted ?? pair.groups!.bare).trim();

            if (value.length === 0) {
                settings.warnings.push(`${origin}: "${key}" has no value; ignoring it.`);
                continue;
            }

            if (lower === "class") {
                settings.className = value;
                continue;
            }

            if (lower === "clipath") {
                settings.cliPath = path.resolve(baseDir, value);
                continue;
            }

            if (lower === "extra") {
                // Relative to the view, not to the process: a workspace-relative path is
                // meaningless in whatever directory the terminal happened to open in.
                settings.extra.push(path.resolve(baseDir, value));
                continue;
            }

            if (lower === "reference") {
                settings.references.push(path.resolve(baseDir, value));
                continue;
            }

            if (lower === "width" || lower === "height") {
                const size = positiveInt(value);
                if (size === undefined) {
                    settings.warnings.push(
                        `${origin}: "${key}" must be a positive whole number, got "${value}".`
                    );
                    continue;
                }

                if (lower === "width") {
                    settings.width = size;
                } else {
                    settings.height = size;
                }

                continue;
            }

            settings.warnings.push(`${origin}: unknown key "${key}"; ignoring it.`);
        }
    }
}

/** A positive whole number, or undefined. Rejects "80px", 80.5 and 0 alike. */
function positiveInt(value: unknown): number | undefined {
    if (typeof value === "string" && !/^\d+$/.test(value.trim())) {
        return undefined;
    }

    const parsed = typeof value === "number" ? value : Number(value);
    return Number.isInteger(parsed) && parsed > 0 ? parsed : undefined;
}

/**
 * Makes a sidecar tolerant of the two things people write in JSON that is really C#-adjacent
 * configuration: `//` comments and a trailing comma. Both are what a hand-edited file ends up
 * with, and refusing to preview because of one would be needlessly brittle.
 *
 * Done as a single pass over strings and comments, because doing it as two independent replaces
 * corrupts a file whose *values* contain the other construct -- a path like "https://x" or an
 * escape inside a string. A single alternation only ever rewrites the construct it is inside.
 */
function stripJsonComments(text: string): string {
    let out = "";
    let index = 0;

    while (index < text.length) {
        const char = text[index];

        if (char === '"') {
            // Copy the string verbatim, honouring backslash escapes so an escaped quote does
            // not end it early.
            let end = index + 1;
            while (end < text.length) {
                if (text[end] === "\\") {
                    end += 2;
                    continue;
                }

                if (text[end] === '"') {
                    end++;
                    break;
                }

                end++;
            }

            out += text.slice(index, end);
            index = end;
            continue;
        }

        if (char === "/" && text[index + 1] === "/") {
            while (index < text.length && text[index] !== "\n") {
                index++;
            }

            continue;
        }

        if (char === "/" && text[index + 1] === "*") {
            const close = text.indexOf("*/", index + 2);
            index = close === -1 ? text.length : close + 2;
            continue;
        }

        // A comma directly before a closing brace or bracket is a trailing comma.
        if (char === ",") {
            const rest = text.slice(index + 1);
            if (/^\s*[}\]]/.test(rest)) {
                index++;
                continue;
            }
        }

        out += char;
        index++;
    }

    return out;
}

/**
 * Determines the `--class` value.
 *
 * The tool requires it and will not infer it, so this is the friction the context menu exists
 * to remove: take the class the view declares for itself, else read the namespace and class
 * declaration out of the file, and only ask when that yields nothing.
 */
async function resolveClassName(file: string, settings: ViewSettings): Promise<string> {
    if (settings.className) {
        logLine(`class from the view's own settings: ${settings.className}`);
        return settings.className;
    }

    const override = vscode.workspace
        .getConfiguration("tuiLivePreview")
        .get<string>("className", "")
        .trim();
    if (override) {
        return override;
    }

    const fromCSharp = readClassFromCSharp(file);
    if (fromCSharp) {
        return fromCSharp;
    }

    const entered = await vscode.window.showInputBox({
        title: "TUI Preview: class name",
        prompt:
            `Could not determine the class name in ${path.basename(file)}. ` +
            `Enter the fully qualified class name.`,
        placeHolder: "MyApp.Views.MyWindow",
        ignoreFocusOut: true,
        validateInput: (value) =>
            value.trim().length === 0 ? "The tool requires a non-empty --class." : null,
    });

    if (!entered) {
        throw new Error("Cancelled: no class name was provided.");
    }
    return entered.trim();
}

/**
 * True when the path is a file the user can right-click to preview.
 *
 * The `.tui.cs` suffix is what makes this matchable: a bare `.cs` would put the whole
 * workspace in scope, so the menus would appear on every source file.
 */
function isViewEntry(file: string): boolean {
    return file.endsWith(".tui.cs");
}

/**
 * Extracts the fully qualified class name from a C# file.
 *
 * Reads `namespace` plus the first class declaration. Without this every preview would prompt
 * for a name the file already states.
 *
 * The class declared is taken to be the view: the file is named after it and the tool only
 * ever instantiates one class from it, so the first non-nested class declaration is both the
 * simple rule and the correct one. It is usually `partial`, the layout half of a view whose
 * logic half is the sibling `.cs`, and that is exactly the class to preview.
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

    return ns ? `${ns}.${type[2]}` : type[2];
}


/**
 * Finds how to run the `tuipreview` tool.
 *
 * Preference order: the view's own `cliPath`, then the `tuiLivePreview.cliPath` setting, then a
 * build in the workspace, then `dotnet run` on the tool's project, then the copy bundled with the
 * extension, then the dotnet tool on PATH.
 *
 * A view's own path comes first because it is the more specific claim: a view that names its
 * tool is saying "preview me with this", not "use whatever the workspace defaults to".
 *
 * A workspace build wins over the bundled copy so that someone working on the tool previews
 * with their own changes. Everyone else has no `cli/` in their workspace and gets the bundled
 * copy, which needs no setup and always matches this version of the extension. The built
 * binary matters because previews are frequent and interactive: paying a restore and build on
 * every right-click would make the extension feel broken.
 *
 * The view's own project is never built. A `.tui.cs` depends on nothing but Terminal.Gui and
 * TuiControls, which the tool ships itself.
 */
function resolveLauncher(view: string, settings?: ViewSettings): Launcher {
    const cfg = vscode.workspace.getConfiguration("tuiLivePreview");

    // The view's own cliPath outranks the workspace setting: a view that names its tool is
    // saying "preview me with this", which is a more specific claim than a folder-wide default.
    // It is honoured whether or not the setting is present.
    const configured = (settings?.cliPath ?? cfg.get<string>("cliPath", "")).trim();

    if (configured) {
        if (!fs.existsSync(configured)) {
            // Named differently depending on where it came from, because the fix differs: a
            // wrong setting is edited in settings.json, a wrong annotation in the view.
            throw new Error(
                settings?.cliPath
                    ? `${path.basename(view)} names a cliPath that does not exist: ${configured}`
                    : `tuiLivePreview.cliPath does not exist: ${configured}`
            );
        }
        return launcherFor(configured);
    }

    for (const root of workspaceRoots()) {
        const built = findBuiltCli(root);
        if (built) {
            return launcherFor(built);
        }
    }

    for (const root of workspaceRoots()) {
        const project = path.join(root, "cli", "TuiPreview.Cli.csproj");
        if (fs.existsSync(project)) {
            return { kind: "dotnet", project };
        }
    }

    // Published into tool/ by install-ext.ps1 before packaging, so it ships in the VSIX.
    const bundled = extensionRoot && path.join(extensionRoot, "tool", "tuipreview.dll");
    if (bundled && fs.existsSync(bundled)) {
        return { kind: "dll", path: bundled };
    }

    // `dotnet tool install -g TuiPreview.Cli` puts it on PATH. If it is not there either, the
    // terminal says so, which is clearer than anything we could guess at here.
    return { kind: "exe", command: "tuipreview" };
}

/** A launcher for a path to the tool, by what kind of file it is. */
function launcherFor(file: string): Launcher {
    if (file.endsWith(".csproj")) {
        return { kind: "dotnet", project: file };
    }
    // A dll is not executable by itself; it runs under the dotnet host.
    if (file.endsWith(".dll")) {
        return { kind: "dll", path: file };
    }
    return { kind: "exe", command: file };
}

function describeLauncher(launcher: Launcher): string {
    switch (launcher.kind) {
        case "exe":
            return launcher.command;
        case "dll":
            return `dotnet ${launcher.path}`;
        case "dotnet":
            return `dotnet run ${launcher.project}`;
    }
}

function workspaceRoots(): string[] {
    return (vscode.workspace.workspaceFolders ?? []).map((f) => f.uri.fsPath);
}

/**
 * Looks for cli/bin/<config>/<tfm>/tuipreview.exe.
 *
 * Debug is preferred over Release because that is what someone iterating on the tool wants,
 * and the newest target framework first so a multi-targeted build does not resolve to a stale
 * output.
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
                process.platform === "win32" ? "tuipreview.exe" : "tuipreview"
            );
            if (fs.existsSync(exe)) {
                return exe;
            }
            // Framework-dependent deployments on non-Windows run the dll via its host.
            const dll = path.join(configDir, tfm, "tuipreview.dll");
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

    const launcher = resolved.launcher;
    const head =
        launcher.kind === "exe"
            ? [quote(launcher.command)]
            : launcher.kind === "dll"
              ? ["dotnet", quote(launcher.path)]
              : ["dotnet", "run", "--project", quote(launcher.project), "--"];

    return [
        ...head,
        "--view",
        // previewPath, not view: this is the scratch copy when the editor holds unsaved edits,
        // and it is the only file the tool can actually read.
        quote(resolved.previewPath),
        "--class",
        quote(resolved.className),
        // Only when the compiled file is a scratch copy. The tool uses this for the window
        // title, which must name the real file rather than one with a content hash in it.
        ...(resolved.previewPath === resolved.view
            ? []
            : ["--display-name", quote(resolved.view)]),
        // Layout-only helpers the view names for itself. Passed explicitly rather than left to the
        // tool so that the resolved command in the log is the whole truth about what was compiled,
        // and so a missing file is reported by the extension, which can name the setting.
        ...(resolved.extraFiles.length > 0
            ? [
                "--extra",
                ...resolved.extraFiles.map((f) => quote(f)),
            ]
            : []),
        // Additive: the tool always includes the framework, Terminal.Gui and TuiControls, so a
        // view written against a third-party control library keeps all of them.
        ...(resolved.references.length > 0
            ? [
                "--reference",
                ...resolved.references.map((f) => quote(f)),
            ]
            : []),
        ...extra,
    ].join(" ");
}

/**
 * The prefix a command name needs in the user's default shell.
 *
 * PowerShell and cmd.exe disagree about a line that starts with a quoted path, and there is
 * no single form both accept (verified against PowerShell 7.6 and cmd.exe):
 *
 *   cmd.exe      "C:\...\tuipreview.exe" --view ...   works
 *   PowerShell   "C:\...\tuipreview.exe" --view ...   ParserError: Unexpected token 'view'
 *
 * PowerShell parses a leading quoted string as a string *expression*, so the arguments that
 * follow are a syntax error. Its call operator makes it a command invocation instead:
 *
 *   PowerShell   & "C:\...\tuipreview.exe" --view ... works
 *   cmd.exe      & "C:\...\tuipreview.exe" --view ... "& was unexpected at this time."
 *
 * The `&` is a command separator in cmd.exe, which is why prefixing it unconditionally is
 * not a workaround — it trades one broken shell for the other. So the shell has to be
 * detected, and the call operator emitted only for PowerShell.
 *
 * Only the `exe` launcher needs this. The others start with `dotnet`, which is unquoted and
 * resolves via PATH, so it parses the same way in both.
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
 * Returns the terminal for a view, creating it if needed.
 *
 * The env is applied only on creation, because that is the only time VS Code lets us set it.
 * Callers must therefore not rely on a changed setting taking effect on an existing
 * terminal.
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
    extensionRoot = context.extensionPath;

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
     * Debounced, because a rebuild costs a full compile-instantiate cycle and
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
