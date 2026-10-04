// Exercises the real compiled extension (out/extension.js) against a stubbed vscode host.
// Run with: node test/extension.test.js
const Module = require("module");
const path = require("path");
const fs = require("fs");
const os = require("os");
const assert = require("assert");

// Redirect require("vscode") to the stub, exactly as the extension host would.
const stubPath = path.join(__dirname, "stub-vscode.js");
const originalResolve = Module._resolveFilename;
Module._resolveFilename = function (request, ...rest) {
    return request === "vscode"
        ? stubPath
        : originalResolve.call(this, request, ...rest);
};

const vscode = require(stubPath);
const ext = require(path.join(__dirname, "..", "out", "extension.js"));

const state = { passed: 0, failed: 0, root: undefined };

/**
 * Runs one assertion and records the result.
 *
 * Deliberately synchronous: an `async` callback would be fire-and-forget, and any `await`
 * inside it would settle *after* the next test has already started — which silently
 * interleaves side effects (terminal writes) into the wrong test. Making that a loud
 * mistake is better than letting it produce a phantom failure somewhere else.
 */
function test(name, fn) {
    if (fn.constructor.name === "AsyncFunction") {
        state.failed++;
        console.log(`  FAIL  ${name}\n        async test callbacks are not awaited; hoist the await out`);
        return;
    }

    try {
        fn();
        state.passed++;
        console.log(`  ok    ${name}`);
    } catch (err) {
        state.failed++;
        console.log(`  FAIL  ${name}\n        ${err.message}`);
    }
}

/** A throwaway workspace laid out the way the real repo is. */
function makeWorkspace() {
    const root = fs.mkdtempSync(path.join(os.tmpdir(), "tuiext-"));
    fs.mkdirSync(path.join(root, "cli", "bin", "Debug", "net10.0"), { recursive: true });
    fs.mkdirSync(path.join(root, "tests"), { recursive: true });
    fs.writeFileSync(path.join(root, "cli", "TuiPreview.Cli.csproj"), "<Project/>");
    fs.writeFileSync(path.join(root, "cli", "bin", "Debug", "net10.0", "tuipreview.exe"), "MZ");
    return root;
}

function setWorkspace(root, config = {}) {
    vscode.__state.workspaceFolders = root
        ? [{ uri: vscode.Uri.file(root), name: "ws", index: 0 }]
        : undefined;
    vscode.__state.configuration = { tuiLivePreview: config };
    vscode.window.activeTextEditor = undefined;
    vscode.window.terminals.length = 0;
}

/**
 * Runs a command and returns the terminal it actually wrote to.
 *
 * Identified by which terminal's `sent` array grew, not by position: terminals are created
 * lazily per file, so the most recently created one is not necessarily the target (running
 * a preview for file A after file B was opened would return B's terminal).
 */
async function run(name, arg) {
    const before = new Map(vscode.window.terminals.map((t) => [t, t.sent.length]));
    await vscode.__state._commands[name](arg);

    for (const t of vscode.window.terminals) {
        if ((before.get(t) ?? 0) < t.sent.length) {
            return t;
        }
    }
    return undefined;
}

/**
 * The most recent command line sent to a terminal.
 *
 * Reused terminals accumulate, so the first entry is the first command ever run against that
 * file, not the one just issued. Assertions must look at the tail.
 */
function lastLine(terminal) {
    return terminal.sent[terminal.sent.length - 1];
}

/** Runs a command expecting failure, returning the error message shown to the user. */
async function runExpectingError(name) {
    let message;
    const original = vscode.window.showErrorMessage;
    vscode.window.showErrorMessage = (m) => {
        message = m;
    };
    await vscode.__state._commands[name]();
    vscode.window.showErrorMessage = original;
    return message;
}

/** Writes a file under the workspace's tests folder, creating directories as needed. */
function writeView(root, name, body) {
    const file = path.join(root, "tests", name);
    fs.mkdirSync(path.dirname(file), { recursive: true });
    fs.writeFileSync(file, body);
    return file;
}

/** The layout half of a view, the shape the tool is built for. */
function layoutHalf(ns, name) {
    return (
        `namespace ${ns};\n` +
        `public partial class ${name} : Window\n{\n    private void InitializeComponent() { }\n}\n`
    );
}

/** Points the stub at a given file as the active editor. */
function edit(file) {
    vscode.window.activeTextEditor = { document: { uri: vscode.Uri.file(file) } };
}

/**
 * Runs a command expecting it to prompt, returning what was typed into the prompt.
 *
 * Distinguishes "the extension inferred the name" from "the extension asked", which is the
 * whole point of the class-name tests: an inference that silently degrades into a prompt
 * still produces a working preview, so only the prompt reveals that it stopped inferring.
 */
async function runExpectingInput(name, answer = "Manually.Named") {
    let typed;
    const original = vscode.window.showInputBox;
    vscode.window.showInputBox = async (opts) => {
        typed = { answer, title: opts?.title, prompt: opts?.prompt };
        return answer;
    };
    await vscode.__state._commands[name]();
    vscode.window.showInputBox = original;
    return typed;
}

module.exports = {
    assert, ext, lastLine, makeWorkspace, run, runExpectingError, runExpectingInput,
    setWorkspace, state, test, vscode, writeView, layoutHalf, edit,
};

const h = module.exports;

async function main() {
    const root = makeWorkspace();
    h.state.root = root;

    h.setWorkspace(root);
    vscode.__state._commands = {};
    h.ext.activate({ subscriptions: [] });

    const view = h.writeView(root, "My.tui.cs", h.layoutHalf("MyApp.Views", "MainWindow"));

    console.log("command registration");
    h.test("registers every contributed command", () => {
        for (const name of [
            "tuiLivePreview.render",
            "tuiLivePreview.watch",
            "tuiLivePreview.interactive",
            "tuiLivePreview.stopAll",
            "tuiLivePreview.showLog",
        ]) {
            h.assert.ok(vscode.__state._commands[name], `missing ${name}`);
        }
    });

    console.log("render");
    h.edit(view);
    const t = await h.run("tuiLivePreview.render");
    const line = h.lastLine(t);

    h.test("emits exactly one command line", () => {
        h.assert.strictEqual(t.sent.length, 1, t.sent.join("|"));
    });
    h.test("prefers the built tuipreview.exe over dotnet run", () => {
        h.assert.ok(line.includes("tuipreview.exe"), line);
    });
    h.test("derives --class from the partial class declaration", () => {
        h.assert.ok(line.includes('--class "MyApp.Views.MainWindow"'), line);
    });
    h.test("passes the quoted absolute path as --view", () => {
        h.assert.ok(line.includes(`--view "${view}"`), line);
    });
    h.test("passes width and height, not --watch", () => {
        h.assert.ok(line.includes("--width"), line);
        h.assert.ok(line.includes("--height"), line);
        h.assert.ok(!line.includes("--watch"), line);
    });
    h.test("passes no --project: a view needs nothing from its project", () => {
        h.assert.ok(!line.includes("--project"), line);
    });

    console.log("watch");
    const watched = await h.run("tuiLivePreview.watch");
    h.test("passes --watch", () => {
        h.assert.ok(h.lastLine(watched).includes("--watch"), h.lastLine(watched));
    });

    console.log("interactive");
    const interactive = await h.run("tuiLivePreview.interactive");
    h.test("passes --interactive and omits width/height", () => {
        const l = h.lastLine(interactive);
        h.assert.ok(l.includes("--interactive"), l);
        h.assert.ok(!l.includes("--width"), `interactive fills the terminal: ${l}`);
    });

    console.log("terminal reuse");
    const terminalsBefore = vscode.window.terminals.length;
    const sendsBefore = t.sent.length;
    await vscode.__state._commands["tuiLivePreview.render"]();
    h.test("reuses the terminal for the same view", () => {
        h.assert.strictEqual(
            vscode.window.terminals.length,
            terminalsBefore,
            "a second preview must not open another terminal"
        );
    });
    h.test("still sends a fresh command line into the reused terminal", () => {
        h.assert.strictEqual(t.sent.length, sendsBefore + 1, t.sent.join(" | "));
    });

    console.log("paths with spaces");
    const spaced = h.writeView(root, "My View.tui.cs", h.layoutHalf("Spaced", "Class"));
    h.edit(spaced);
    const spacedRun = await h.run("tuiLivePreview.render");
    h.test("quotes a path containing spaces", () => {
        h.assert.ok(h.lastLine(spacedRun).includes(`"${spaced}"`), h.lastLine(spacedRun));
    });

    console.log("class name resolution");

    h.edit(h.writeView(root, "NoClass.tui.cs", "// nothing declared yet\n"));
    let prompted = false;
    const originalInput = vscode.window.showInputBox;
    vscode.window.showInputBox = async () => {
        prompted = true;
        return "Typed.In.Class";
    };
    const typed = await h.run("tuiLivePreview.render");
    vscode.window.showInputBox = originalInput;

    h.test("prompts when the file declares no class", () => {
        h.assert.ok(prompted, "expected an input box");
    });
    h.test("uses the typed class name", () => {
        h.assert.ok(h.lastLine(typed).includes("Typed.In.Class"), h.lastLine(typed));
    });

    h.edit(view);
    vscode.__state.configuration.tuiLivePreview = { className: "Over.Ridden" };
    const overridden = await h.run("tuiLivePreview.render");
    h.test("className setting overrides the declaration", () => {
        h.assert.ok(h.lastLine(overridden).includes("Over.Ridden"), h.lastLine(overridden));
    });
    vscode.__state.configuration.tuiLivePreview = {};

    console.log("tool discovery");

    // No build output: must fall back to `dotnet run` against cli/TuiPreview.Cli.csproj.
    fs.rmSync(path.join(root, "cli", "bin"), { recursive: true, force: true });
    h.edit(view);
    const viaDotnet = await h.run("tuiLivePreview.render");
    const dotnetLine = h.lastLine(viaDotnet);

    h.test("falls back to dotnet run when nothing is built", () => {
        h.assert.ok(dotnetLine.startsWith("dotnet run"), dotnetLine);
        h.assert.ok(dotnetLine.includes("TuiPreview.Cli.csproj"), dotnetLine);
    });
    h.test("passes the -- separator so dotnet does not eat the tool's flags", () => {
        h.assert.ok(dotnetLine.includes("-- "), dotnetLine);
    });

    // Neither a build nor the project: a workspace that merely contains views.
    fs.rmSync(path.join(root, "cli"), { recursive: true, force: true });
    const viaPath = await h.run("tuiLivePreview.render");
    h.test("falls back to the installed tuipreview on PATH", () => {
        h.assert.ok(h.lastLine(viaPath).includes('"tuipreview" --view'), h.lastLine(viaPath));
    });

    // The copy install-ext.ps1 publishes into the extension before packaging.
    const extDir = fs.mkdtempSync(path.join(os.tmpdir(), "tuiext-install-"));
    const bundled = path.join(extDir, "tool", "tuipreview.dll");
    fs.mkdirSync(path.dirname(bundled), { recursive: true });
    fs.writeFileSync(bundled, "MZ");
    vscode.__state._commands = {};
    h.ext.activate({ subscriptions: [], extensionPath: extDir });
    h.edit(view);

    const viaBundle = h.lastLine(await h.run("tuiLivePreview.render"));
    h.test("uses the tool bundled with the extension when the workspace has none", () => {
        h.assert.ok(viaBundle.startsWith(`dotnet "${bundled}" --view`), viaBundle);
    });

    // Someone working on the tool itself must preview with their own build, not the bundle.
    fs.mkdirSync(path.join(root, "cli", "bin", "Debug", "net10.0"), { recursive: true });
    fs.writeFileSync(path.join(root, "cli", "bin", "Debug", "net10.0", "tuipreview.exe"), "MZ");
    const overBundle = h.lastLine(await h.run("tuiLivePreview.render"));
    h.test("a workspace build wins over the bundled tool", () => {
        h.assert.ok(overBundle.includes("tuipreview.exe"), overBundle);
    });

    vscode.__state.configuration.tuiLivePreview = { cliPath: bundled };
    const viaDllSetting = h.lastLine(await h.run("tuiLivePreview.render"));
    vscode.__state.configuration.tuiLivePreview = {};
    h.test("a dll in cliPath runs under dotnet", () => {
        h.assert.ok(viaDllSetting.startsWith(`dotnet "${bundled}" --view`), viaDllSetting);
    });
    fs.rmSync(extDir, { recursive: true, force: true });

    console.log("error handling");

    vscode.__state.configuration.tuiLivePreview = {
        cliPath: path.join(root, "does", "not", "exist.exe"),
    };
    h.edit(view);
    const badPathMessage = await h.runExpectingError("tuiLivePreview.render");
    vscode.__state.configuration.tuiLivePreview = {};

    h.test("missing cliPath reports a readable error", () => {
        h.assert.ok(badPathMessage, "expected an error message");
        h.assert.ok(/does not exist/.test(badPathMessage), badPathMessage);
    });

    h.edit(h.writeView(root, "Plain.cs", "public class Plain { }\n"));
    const notAView = await h.runExpectingError("tuiLivePreview.render");
    h.test("a plain .cs is refused with an explanation", () => {
        h.assert.ok(notAView?.includes(".tui.cs"), `expected an explanation: ${notAView}`);
    });

    h.setWorkspace(undefined);
    const noFileMessage = await h.runExpectingError("tuiLivePreview.render");
    h.test("no workspace and no open view reports an error", () => {
        h.assert.ok(noFileMessage, "expected an error message");
    });

    console.log("stopAll");
    const root2 = makeWorkspace();
    h.setWorkspace(root2);
    vscode.__state._commands = {};
    h.ext.activate({ subscriptions: [] });
    const view2 = h.writeView(root2, "Stop.tui.cs", h.layoutHalf("S", "Stop"));
    h.edit(view2);
    await vscode.__state._commands["tuiLivePreview.render"]();
    await vscode.__state._commands["tuiLivePreview.stopAll"]();
    h.test("stopAll disposes preview terminals", () => {
        h.assert.ok(
            vscode.window.terminals.every((t) => t.disposed > 0),
            "expected every terminal to be disposed"
        );
    });

    console.log("context-menu uri (regression: the uri argument used to be ignored)");
    const fromMenu = h.writeView(root2, "FromMenu.tui.cs", h.layoutHalf("Menu", "Target"));
    const active = h.writeView(root2, "Active.tui.cs", h.layoutHalf("Active", "Target"));
    h.edit(active);

    // What VS Code actually does when you right-click one file in the explorer while a
    // different one is the active tab. Before the fix this previewed the active tab.
    const menuRun = await h.run("tuiLivePreview.render", vscode.Uri.file(fromMenu));
    h.test("uses the uri passed by the context menu, not the active editor", () => {
        const l = h.lastLine(menuRun);
        h.assert.ok(l.includes("Menu.Target"), `previewed the wrong file: ${l}`);
        h.assert.ok(l.includes(`"${fromMenu}"`), l);
    });

    const activeRun = await h.run("tuiLivePreview.render");
    h.test("falls back to the active editor when no uri is passed", () => {
        h.assert.ok(h.lastLine(activeRun).includes("Active.Target"), h.lastLine(activeRun));
    });

    console.log("diagnostics channel");
    h.test("logs the invocation and the executed command", () => {
        const lines = (vscode.window.channels ?? []).flatMap((c) => c.lines);
        h.assert.ok(
            lines.some((l) => l.includes("--- tuiLivePreview.render invoked ---")),
            "expected an invocation marker in the log"
        );
        h.assert.ok(
            lines.some((l) => l.includes("running:") && l.includes("tuipreview.exe")),
            "expected the executed command line in the log"
        );
    });
    h.setWorkspace(undefined);
    await h.runExpectingError("tuiLivePreview.render");
    h.test("logs why a preview could not start", () => {
        const lines = (vscode.window.channels ?? []).flatMap((c) => c.lines);
        h.assert.ok(
            lines.some((l) => l.includes("FAILED tuiLivePreview.render")),
            "expected a FAILED entry in the log"
        );
    });

    console.log("stale-host detection");
    // activate() runs once per harness setWorkspace, so take the most recent item.
    h.test("shows a status bar item carrying the build id", () => {
        const items = vscode.window.statusBarItems ?? [];
        const item = items[items.length - 1];
        h.assert.ok(item, "expected a status bar item");
        h.assert.ok(
            /TUI \d{4}-\d{2}-\d{2}\.\d/.test(item.text),
            `status bar should carry a build id: ${item.text}`
        );
        h.assert.strictEqual(item.shown, 1, "status bar item should be visible");
    });
    h.test("status bar click opens the log", () => {
        const items = vscode.window.statusBarItems ?? [];
        h.assert.strictEqual(items[items.length - 1].command, "tuiLivePreview.showLog");
    });

    // Awaited outside the assertion because test() callbacks must stay synchronous.
    await new Promise((r) => setImmediate(r));
    h.test("logs every registered command after activation", () => {
        // The diagnostic for "command not found": a stale host omits commands here.
        const lines = (vscode.window.channels ?? []).flatMap((c) => c.lines);
        const entry = lines.find((l) => l.includes("registered commands:"));
        h.assert.ok(entry, "expected a registered-commands log line");
        for (const n of ["render", "watch", "interactive", "showLog", "stopAll"]) {
            h.assert.ok(
                entry.includes(`tuiLivePreview.${n}`),
                `registered-commands log is missing ${n}: ${entry}`
            );
        }
    });

    fs.rmSync(root2, { recursive: true, force: true });
    return root;
}

/**
 * The command line has to be valid in the user's *default* shell.
 *
 * PowerShell and cmd.exe disagree about a line that starts with a quoted path, and there is no
 * form both accept: cmd.exe needs the bare quoted path, PowerShell needs its `&` call operator
 * and rejects the bare form with "Unexpected token 'view'". Prefixing `&` unconditionally is
 * not a fix either — cmd.exe reads it as a command separator and fails with "& was
 * unexpected at this time."
 *
 * So the emitted line has to follow the shell, which is what these assert. Without them the
 * whole extension is dead on arrival for anyone whose default profile is PowerShell, and
 * nothing else in the suite can catch it: every other assertion is happy with a command line
 * that no real shell would accept.
 */
async function shellTests() {
    const h = module.exports;
    const root = h.makeWorkspace();
    h.setWorkspace(root);
    vscode.__state._commands = {};
    h.ext.activate({ subscriptions: [] });

    h.edit(h.writeView(root, "Shell.tui.cs", h.layoutHalf("App", "Shell")));

    // activate() has already run, so re-registering is enough to get a clean terminal map.
    console.log("shell-aware command line");
    for (const [shell, expected] of [
        ["C:\\Program Files\\PowerShell\\7\\pwsh.exe", "& "],
        ["C:\\WINDOWS\\System32\\WindowsPowerShell\\v1.0\\powershell.exe", "& "],
        ["/usr/bin/zsh", ""],
        ["C:\\WINDOWS\\system32\\cmd.exe", ""],
        // VS Code cannot always determine the default profile; PowerShell is the safer guess.
        [undefined, "& "],
    ]) {
        vscode.__state.shell = shell;
        const t = await h.run("tuiLivePreview.render");
        const line = h.lastLine(t);
        const label = shell ?? "(undefined)";
        h.test(`emits ${expected ? "the call operator" : "no call operator"} for ${label}`, () => {
            h.assert.ok(
                line.startsWith(`${expected}"`),
                `expected the line to start with ${JSON.stringify(expected + '"')}: ${line}`
            );
        });
        h.test(`still quotes the args for ${label}`, () => {
            h.assert.ok(line.includes(`--class "App.Shell"`), line);
        });
    }

    // The dotnet launcher resolves via PATH and is never quoted, so it must not gain a prefix.
    console.log("shell-aware command line (dotnet launcher)");
    vscode.__state.configuration = {
        tuiLivePreview: { cliPath: path.join(root, "cli", "TuiPreview.Cli.csproj") },
    };
    vscode.__state.shell = "C:\\Program Files\\PowerShell\\7\\pwsh.exe";
    const dt = await h.run("tuiLivePreview.render");
    h.test("dotnet launcher gets no call operator", () => {
        h.assert.ok(
            h.lastLine(dt).startsWith("dotnet run"),
            `expected a bare 'dotnet run': ${h.lastLine(dt)}`
        );
    });

    return root;
}

/**
 * Previewing unsaved edits.
 *
 * The tool can only read from disk, so an unsaved buffer has to be mirrored to a scratch file
 * that the preview then watches. The scratch path must stay the same across edits: the
 * interactive preview polls one specific path, so a fresh temp file per keystroke would leave
 * it watching something that no longer exists and the preview would simply stop updating.
 */
async function liveTests() {
    const h = module.exports;
    const root = h.makeWorkspace();
    h.setWorkspace(root);
    vscode.__state._commands = {};
    h.ext.activate({ subscriptions: [] });

    const saved = h.layoutHalf("App", "Live");
    const real = h.writeView(root, "Live.tui.cs", saved);

    let text = saved.replace("{ }", '{ Title = "UNSAVED EDIT"; }');
    const doc = {
        uri: vscode.Uri.file(real),
        isDirty: true,
        getText: () => text,
    };
    vscode.__state.textDocuments = [doc];
    h.edit(real);

    const scratchOf = (line) => {
        const m = line.match(/--view "([^"]+)"/);
        h.assert.ok(m, `expected a --view path: ${line}`);
        return m[1];
    };

    console.log("unsaved-buffer previewing");
    const t = await h.run("tuiLivePreview.render");
    const first = h.lastLine(t);

    h.test("previews a scratch copy, not the file on disk", () => {
        h.assert.ok(!first.includes(`--view "${real}"`), `should not read the real path: ${first}`);
        h.assert.ok(fs.existsSync(scratchOf(first)), "the scratch file should exist");
    });

    h.test("the scratch copy holds the live buffer, not the saved file", () => {
        h.assert.strictEqual(
            fs.readFileSync(scratchOf(first), "utf8"),
            text,
            "the scratch file should contain the editor buffer"
        );
        h.assert.notStrictEqual(text, saved, "the fixture should differ from the saved file");
    });

    h.test("the scratch copy keeps the .tui.cs suffix", () => {
        h.assert.ok(scratchOf(first).endsWith(".tui.cs"), scratchOf(first));
    });

    h.test("names the real file with --display-name", () => {
        // The window title must show the file the user edits, not one with a hash in it.
        h.assert.ok(first.includes(`--display-name "${real}"`), first);
    });

    const firstScratch = scratchOf(first);

    text = saved.replace("{ }", '{ Title = "SECOND UNSAVED EDIT"; }');
    const t2 = await h.run("tuiLivePreview.render");

    h.test("edits keep the same scratch path", () => {
        // The interactive preview polls one path; if it moved, a running preview goes deaf.
        h.assert.strictEqual(
            scratchOf(h.lastLine(t2)),
            firstScratch,
            "the scratch path must not change when the buffer is edited"
        );
    });

    h.test("the re-mirrored buffer is what the preview reads", () => {
        h.assert.strictEqual(fs.readFileSync(scratchOf(h.lastLine(t2)), "utf8"), text);
    });

    console.log("unsaved-buffer previewing (live = false)");
    vscode.__state.configuration = { tuiLivePreview: { live: false } };
    const t3 = await h.run("tuiLivePreview.render");

    h.test("live: false previews the saved file instead", () => {
        h.assert.ok(
            h.lastLine(t3).includes(`--view "${real}"`),
            `expected the real path when live is off: ${h.lastLine(t3)}`
        );
    });

    h.test("live: false explains itself in the log", () => {
        const lines = (vscode.window.channels ?? []).flatMap((c) => c.lines);
        h.assert.ok(
            lines.some((l) => l.includes("unsaved changes") && l.includes("live is off")),
            "expected the log to say the unsaved buffer was skipped"
        );
    });

    vscode.__state.textDocuments = [];
    return root;
}

module.exports.main = main;

/**
 * The context-menu `when` clauses.
 *
 * These are the one part of the extension only VS Code evaluates, so a mistake here is
 * invisible to every other test: the commands register fine, they just never appear in a
 * menu. A doubly-escaped `\\.` compiles to a regex demanding a literal backslash and so
 * never matches anything, which is how the menus silently disappeared once already.
 */
function testWhenClauses() {
    const manifest = require(path.join(__dirname, "..", "package.json"));
    const menus = manifest.contributes.menus;

    const regexOf = (when) =>
        new RegExp(when.slice(when.indexOf("/") + 1, when.lastIndexOf("/")) + "$");

    h.test("every context-menu entry has a when guard", () => {
        for (const [menu, items] of Object.entries(menus)) {
            for (const item of items) {
                h.assert.ok(item.when, `${menu}: ${item.command} has no "when" guard`);
            }
        }
    });

    h.test("the when regex matches a .tui.cs filename", () => {
        // It has to match, or right-clicking a view shows nothing at all - which is exactly
        // the "menus silently disappeared" failure mode.
        const re = regexOf(menus["explorer/context"][0].when);
        h.assert.ok(re.test("Counter.tui.cs"), "did not match a view");
        h.assert.ok(re.test("My View.tui.cs"), "did not match a view with spaces");
    });

    h.test("the when regex rejects other files", () => {
        // Counter.cs is the logic half of a view: previewing it would compile the one file
        // that is allowed to depend on anything.
        const re = regexOf(menus["explorer/context"][0].when);
        for (const other of [
            "App.xaml",
            "MainWindow.tui.xaml",
            "MainWindow.tui.cs.bak",
            "Counter.cs",
            "tui.cs",
            "notes.txt",
        ]) {
            h.assert.ok(!re.test(other), `should not match ${other}`);
        }
    });

    h.test("the when clause is not double-escaped", () => {
        // Two backslashes (one per dot) is correct. Four means the JSON was escaped twice
        // and the regex demands literal backslashes, so nothing ever matches.
        for (const [menu, items] of Object.entries(menus)) {
            for (const item of items) {
                if (!item.when || !item.when.includes("tui")) {
                    continue;
                }
                const count = [...item.when].filter((c) => c.charCodeAt(0) === 92).length;
                h.assert.strictEqual(
                    count,
                    2,
                    `${menu}: ${item.command} has ${count} backslashes, expected 2: ${item.when}`
                );
            }
        }
    });

    // Since VS Code 1.74 a command declared in contributes.commands activates the extension
    // by itself, so declaring it is what makes a menu entry work.
    h.test("every menu command is declared", () => {
        const declared = new Set(manifest.contributes.commands.map((c) => c.command));

        for (const [menu, items] of Object.entries(menus)) {
            for (const item of items) {
                h.assert.ok(declared.has(item.command), `${menu}: ${item.command} not declared`);
            }
        }
    });
}

/**
 * Inferring `--class` from C# source, and what the command line leaves out.
 *
 * The class name has to be read from the file, or every preview prompts for a name the file
 * already states. The layout half of a view is a partial class, so partial must be inferred
 * like any other. And the view's project must play no part: the tool compiles a `.tui.cs`
 * against Terminal.Gui and TuiControls only, so there is nothing to build and no --project.
 */
async function classNameTests() {
    const h = module.exports;
    const root = h.makeWorkspace();
    h.setWorkspace(root);
    vscode.__state._commands = {};
    h.ext.activate({ subscriptions: [] });

    console.log("class name inference");

    const cases = [
        [
            "file-scoped namespace",
            "namespace My.App.Views;\npublic sealed class Counter : Window { }\n",
            "My.App.Views.Counter",
        ],
        [
            "block namespace",
            "namespace My.App {\n    public class Panel : Window { }\n}\n",
            "My.App.Panel",
        ],
        [
            "sealed and internal modifiers",
            "namespace N;\ninternal sealed class Thing : Window { }\n",
            "N.Thing",
        ],
        [
            "no namespace at all",
            "public class Bare : Window { }\n",
            "Bare",
        ],
        [
            "a commented-out class is ignored",
            "namespace N;\n// public class Ghost : Window { }\npublic class Real : Window { }\n",
            "N.Real",
        ],
        [
            "a partial layout half",
            "namespace N;\npublic sealed partial class Split : Window\n{\n    private void InitializeComponent() { }\n}\n",
            "N.Split",
        ],
    ];

    for (const [label, body, expected] of cases) {
        h.edit(h.writeView(root, `${label.replace(/\W+/g, "")}.tui.cs`, body));
        const t = await h.run("tuiLivePreview.render");
        h.test(`infers the class from ${label}`, () => {
            h.assert.ok(
                h.lastLine(t).includes(`--class "${expected}"`),
                `expected --class "${expected}": ${h.lastLine(t)}`
            );
        });
    }

    console.log("the view's own project plays no part");

    // An app project right beside the view: the old hosted launcher would have built it.
    h.writeView(root, "App/App.csproj",
        '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType></PropertyGroup></Project>');
    h.edit(h.writeView(root, "App/MainView.tui.cs", h.layoutHalf("App", "MainView")));
    const inApp = h.lastLine(await h.run("tuiLivePreview.render"));
    h.test("a view inside an app project still runs the tool", () => {
        h.assert.ok(inApp.includes("tuipreview.exe"), inApp);
        h.assert.ok(!inApp.includes("--project"), inApp);
        h.assert.ok(!inApp.includes("--tui-preview"), inApp);
    });

    return root;
}

/**
 * Settings a view states for itself: a `// @tuipreview` annotation and a `.tui.json` sidecar.
 *
 * These matter because everything else in the extension is one global bag for the whole
 * workspace. A view is the only thing that knows its own class name and which layout helpers it
 * needs, so both belong with the view. The two cases worth guarding hardest are precedence
 * (the annotation must beat the sidecar) and the dirty buffer (an unsaved annotation is the one
 * the user is looking at, and must not be read off disk).
 *
 * Each preview is awaited before the assertions that read it: `test` runs its callback
 * synchronously, and a promise returned from one would settle after the suite had moved on.
 */
async function viewSettingsTests() {
    const h = module.exports;
    const root = h.makeWorkspace();
    h.setWorkspace(root);
    vscode.__state._commands = {};
    h.ext.activate({ subscriptions: [] });

    console.log("@tuipreview annotations");

    const annotated = h.writeView(
        root,
        "Annotated.tui.cs",
        "// @tuipreview class=Other.Declared width=64 height=12\n" +
            h.layoutHalf("Demo", "Annotated")
    );
    h.edit(annotated);
    const annLine = h.lastLine(await h.run("tuiLivePreview.render"));

    h.test("an annotation supplies the class, with no prompt", () => {
        h.assert.ok(annLine.includes('--class "Other.Declared"'), annLine);
    });

    h.test("the annotation beats the class the file declares", () => {
        // The point of declaring it: the file declares Demo.Annotated and the annotation says
        // something else. If inference won, the annotation would not be a setting at all.
        h.assert.ok(!annLine.includes('--class "Demo.Annotated"'), annLine);
    });

    h.test("an annotation supplies the render size", () => {
        h.assert.ok(annLine.includes("--width 64"), annLine);
        h.assert.ok(annLine.includes("--height 12"), annLine);
    });

    console.log("@tuipreview extras");

    h.writeView(root, "helpers/Layout.cs", "namespace Demo { }");
    const withExtra = h.writeView(
        root,
        "WithExtra.tui.cs",
        "// @tuipreview class=Demo.WithExtra extra=helpers/Layout.cs\n" +
            h.layoutHalf("Demo", "WithExtra")
    );
    h.edit(withExtra);
    const extraLine = h.lastLine(await h.run("tuiLivePreview.render"));

    h.test("an extra file is compiled with the view", () => {
        h.assert.ok(extraLine.includes("--extra"), extraLine);
    });

    h.test("an extra path is resolved against the view, not the cwd", () => {
        h.assert.ok(
            extraLine.includes(`"${path.join(root, "tests", "helpers", "Layout.cs")}"`),
            extraLine
        );
    });

    const spaced = h.writeView(root, "Helpers With Space.cs", "namespace Demo { }");
    const quoted = h.writeView(
        root,
        "Quoted.tui.cs",
        `// @tuipreview class=Demo.Quoted extra="${path.basename(spaced)}"\n` +
            h.layoutHalf("Demo", "Quoted")
    );
    h.edit(quoted);
    const quotedLine = h.lastLine(await h.run("tuiLivePreview.render"));

    h.test("a quoted value may contain spaces", () => {
        h.assert.ok(quotedLine.includes(`"${spaced}"`), quotedLine);
    });

    const missing = h.writeView(
        root,
        "BadExtra.tui.cs",
        "// @tuipreview class=Demo.Bad extra=Nope.cs\n" + h.layoutHalf("Demo", "Bad")
    );
    h.edit(missing);
    const missingMessage = await h.runExpectingError("tuiLivePreview.render");

    h.test("a missing extra file is reported before anything is launched", () => {
        h.assert.ok(
            missingMessage && missingMessage.includes("Nope.cs"),
            `expected the missing file to be named: ${missingMessage}`
        );
    });

    console.log("sidecar .tui.json");

    const sidecarView = h.writeView(root, "Sidecar.tui.cs", h.layoutHalf("Demo", "Sidecar"));
    fs.writeFileSync(
        path.join(root, "tests", "Sidecar.tui.json"),
        JSON.stringify({ class: "Demo.FromSidecar", width: 40, height: 10 })
    );
    h.edit(sidecarView);
    const sideLine = h.lastLine(await h.run("tuiLivePreview.render"));

    h.test("a sidecar supplies the class", () => {
        h.assert.ok(sideLine.includes('--class "Demo.FromSidecar"'), sideLine);
    });

    h.test("a sidecar supplies the render size", () => {
        h.assert.ok(sideLine.includes("--width 40"), sideLine);
        h.assert.ok(sideLine.includes("--height 10"), sideLine);
    });

    const commented = h.writeView(root, "Commented.tui.cs", h.layoutHalf("Demo", "Commented"));
    fs.writeFileSync(
        path.join(root, "tests", "Commented.tui.json"),
        '{\n  // only this wide\n  "class": "Demo.Commented", "width": 50,\n}\n'
    );
    h.edit(commented);
    const commentedLine = h.lastLine(await h.run("tuiLivePreview.render"));

    h.test("a sidecar may carry comments and trailing commas", () => {
        h.assert.ok(commentedLine.includes('--class "Demo.Commented"'), commentedLine);
        h.assert.ok(commentedLine.includes("--width 50"), commentedLine);
    });

    const lastOut = h.writeView(root, "LastOut.tui.cs", h.layoutHalf("Demo", "LastOut"));
    fs.writeFileSync(
        path.join(root, "tests", "LastOut.tui.json"),
        '{\n  "width": 51,\n  // "height": 12,\n  /* block */\n}\n'
    );
    h.edit(lastOut);
    const lastOutLine = h.lastLine(await h.run("tuiLivePreview.render"));

    h.test("a trailing comma before a commented-out last entry is still trailing", () => {
        h.assert.ok(lastOutLine.includes("--width 51"), lastOutLine);
    });

    const broken = h.writeView(root, "Broken.tui.cs", h.layoutHalf("Demo", "Broken"));
    fs.writeFileSync(path.join(root, "tests", "Broken.tui.json"), '{ "width": 51 ');
    h.edit(broken);
    await h.run("tuiLivePreview.render");

    h.test("a sidecar that does not parse is reported, not silently dropped", () => {
        const lines = (vscode.window.channels ?? []).flatMap((c) => c.lines);
        h.assert.ok(
            lines.some((l) => l.includes("Broken.tui.json") && l.includes("not valid JSON")),
            "expected the log to name the broken sidecar"
        );
    });

    console.log("annotation vs sidecar");

    const both = h.writeView(
        root,
        "Both.tui.cs",
        "// @tuipreview class=Demo.FromAnnotation width=70\n" + h.layoutHalf("Demo", "Both")
    );
    fs.writeFileSync(
        path.join(root, "tests", "Both.tui.json"),
        JSON.stringify({ class: "Demo.FromSidecar", width: 40 })
    );
    h.edit(both);
    const bothLine = h.lastLine(await h.run("tuiLivePreview.render"));

    h.test("the annotation overrides the sidecar", () => {
        h.assert.ok(bothLine.includes('--class "Demo.FromAnnotation"'), bothLine);
        h.assert.ok(bothLine.includes("--width 70"), bothLine);
        h.assert.ok(!bothLine.includes("--width 40"), `sidecar leaked through: ${bothLine}`);
    });

    const sideOnly = h.writeView(
        root,
        "SideOnly.tui.cs",
        "// @tuipreview class=Demo.SideOnly\n" + h.layoutHalf("Demo", "SideOnly")
    );
    fs.writeFileSync(
        path.join(root, "tests", "SideOnly.tui.json"),
        JSON.stringify({ height: 33 })
    );
    h.edit(sideOnly);
    const sideOnlyLine = h.lastLine(await h.run("tuiLivePreview.render"));

    h.test("the sidecar still supplies what the annotation omits", () => {
        h.assert.ok(sideOnlyLine.includes("--height 33"), sideOnlyLine);
        h.assert.ok(sideOnlyLine.includes('--class "Demo.SideOnly"'), sideOnlyLine);
    });

    console.log("@tuipreview references");

    const lib = path.join(root, "lib", "ThirdParty.dll");
    fs.mkdirSync(path.dirname(lib), { recursive: true });
    fs.writeFileSync(lib, "MZ");
    const refd = h.writeView(
        root,
        "Referenced.tui.cs",
        "// @tuipreview class=Demo.Referenced reference=../lib/ThirdParty.dll\n" +
            h.layoutHalf("Demo", "Referenced")
    );
    h.edit(refd);
    const refLine = h.lastLine(await h.run("tuiLivePreview.render"));

    h.test("a reference is passed to the tool", () => {
        h.assert.ok(refLine.includes("--reference"), refLine);
    });

    h.test("a reference is resolved against the view", () => {
        h.assert.ok(refLine.includes(`"${lib}"`), refLine);
    });

    const badRef = h.writeView(
        root,
        "BadRef.tui.cs",
        "// @tuipreview class=Demo.BadRef reference=../lib/Nope.dll\n" +
            h.layoutHalf("Demo", "BadRef")
    );
    h.edit(badRef);
    const refMessage = await h.runExpectingError("tuiLivePreview.render");

    h.test("a missing reference is reported before anything is launched", () => {
        h.assert.ok(
            refMessage && refMessage.includes("Nope.dll"),
            `expected the missing reference to be named: ${refMessage}`
        );
    });

    console.log("@tuipreview projects");

    const controlsProject = path.join(root, "Controls", "Controls.csproj");
    fs.mkdirSync(path.dirname(controlsProject), { recursive: true });
    fs.writeFileSync(controlsProject, "<Project />");
    const projd = h.writeView(
        root,
        "Projected.tui.cs",
        "// @tuipreview class=Demo.Projected project=../Controls/Controls.csproj\n" +
            h.layoutHalf("Demo", "Projected")
    );
    h.edit(projd);
    const projLine = h.lastLine(await h.run("tuiLivePreview.render"));

    h.test("a project is passed to the tool, resolved against the view", () => {
        h.assert.ok(projLine.includes(`--project "${controlsProject}"`), projLine);
    });

    const badProj = h.writeView(
        root,
        "BadProj.tui.cs",
        "// @tuipreview class=Demo.BadProj project=../Nope/Nope.csproj\n" +
            h.layoutHalf("Demo", "BadProj")
    );
    h.edit(badProj);
    const projMessage = await h.runExpectingError("tuiLivePreview.render");

    h.test("a missing project is reported before anything is launched", () => {
        h.assert.ok(
            projMessage && projMessage.includes("Nope.csproj"),
            `expected the missing project to be named: ${projMessage}`
        );
    });

    console.log("@tuipreview cliPath");

    const localCli = path.join(root, "tools", "tuipreview.exe");
    fs.mkdirSync(path.dirname(localCli), { recursive: true });
    fs.writeFileSync(localCli, "MZ");
    const withCli = h.writeView(
        root,
        "WithCli.tui.cs",
        "// @tuipreview class=Demo.WithCli clipath=../tools/tuipreview.exe\n" +
            h.layoutHalf("Demo", "WithCli")
    );
    h.edit(withCli);
    const cliLine = h.lastLine(await h.run("tuiLivePreview.render"));

    h.test("a view's cliPath selects the tool to run", () => {
        // The workspace also has a built cli/bin copy, so this proves the view won rather than
        // that some lookup happened to find the right thing.
        h.assert.ok(cliLine.includes(localCli), cliLine);
        h.assert.ok(
            !cliLine.includes(path.join("cli", "bin")),
            `the workspace build should have been overridden: ${cliLine}`
        );
    });

    const otherCli = path.join(root, "tools", "other.exe");
    fs.writeFileSync(otherCli, "MZ");
    vscode.__state.configuration = { tuiLivePreview: { cliPath: otherCli } };
    h.edit(withCli);
    const settingLine = h.lastLine(await h.run("tuiLivePreview.render"));
    vscode.__state.configuration = {};

    h.test("a cliPath outranks the cliPath setting", () => {
        h.assert.ok(settingLine.includes(localCli), `the view's path should win: ${settingLine}`);
    });

    const badCli = h.writeView(
        root,
        "BadCli.tui.cs",
        "// @tuipreview class=Demo.BadCli clipath=../tools/Nope.exe\n" +
            h.layoutHalf("Demo", "BadCli")
    );
    h.edit(badCli);
    const cliMessage = await h.runExpectingError("tuiLivePreview.render");

    h.test("a missing cliPath names the view, not the setting", () => {
        // The fix differs by source: a wrong annotation is fixed in the view, a wrong setting in
        // settings.json. Saying "tuiLivePreview.cliPath" for an annotation sends the user to the
        // wrong file.
        h.assert.ok(cliMessage && cliMessage.includes("BadCli.tui.cs"), cliMessage);
        h.assert.ok(!cliMessage.includes("tuiLivePreview.cliPath"), cliMessage);
    });

    console.log("settings from an unsaved buffer");

    const dirty = h.writeView(root, "Dirty.tui.cs", h.layoutHalf("Demo", "Dirty"));
    vscode.__state.textDocuments = [
        {
            uri: vscode.Uri.file(dirty),
            isDirty: true,
            // The saved file says nothing, so reading settings from disk would silently fall
            // back to inference and preview a different class than the one on screen.
            getText: () =>
                "// @tuipreview class=Demo.FromTheBuffer\n" + h.layoutHalf("Demo", "Dirty"),
        },
    ];
    h.edit(dirty);
    const dirtyLine = h.lastLine(await h.run("tuiLivePreview.render"));

    h.test("an unsaved annotation is the one that applies", () => {
        h.assert.ok(dirtyLine.includes('--class "Demo.FromTheBuffer"'), dirtyLine);
    });
    vscode.__state.textDocuments = [];

    console.log("bad settings do not stop a preview");

    const typos = h.writeView(
        root,
        "Typos.tui.cs",
        "// @tuipreview class=Demo.Typos widht=90 colour=red\n" + h.layoutHalf("Demo", "Typos")
    );
    h.edit(typos);
    const typoLine = h.lastLine(await h.run("tuiLivePreview.render"));

    h.test("a misspelled key is ignored, the good keys still apply", () => {
        h.assert.ok(typoLine.includes('--class "Demo.Typos"'), typoLine);
        h.assert.ok(!typoLine.includes("--width 90"), `a bad value leaked through: ${typoLine}`);
    });

    h.test("bad settings are logged rather than raised as errors", () => {
        const lines = (vscode.window.channels ?? []).flatMap((c) => c.lines);
        h.assert.ok(
            lines.some((l) => l.includes("widht") && l.includes("unknown key")),
            "expected the log to name the unknown key"
        );
    });

    console.log("annotation overriding the sidecar");

    const overridden = h.writeView(
        root,
        "Overridden.tui.cs",
        "// @tuipreview class=Demo.Overridden width=70\n" + h.layoutHalf("Demo", "Overridden")
    );
    fs.writeFileSync(
        path.join(root, "tests", "Overridden.tui.json"),
        JSON.stringify({ class: "Demo.Overridden", width: 40 })
    );
    h.edit(overridden);
    const shown = [];
    const originalWarning = vscode.window.showWarningMessage;
    vscode.window.showWarningMessage = (m) => {
        shown.push(m);
    };
    await h.run("tuiLivePreview.render");
    vscode.window.showWarningMessage = originalWarning;

    h.test("an annotation overriding the sidecar is shown as a warning", () => {
        h.assert.strictEqual(shown.length, 1, JSON.stringify(shown));
        h.assert.ok(shown[0].includes('"width"') && shown[0].includes("overrides"), shown[0]);
        h.assert.ok(shown[0].includes("Overridden.tui.json"), shown[0]);
    });

    h.test("a key both state with the same value is not an override", () => {
        // The class agrees, so only the width is worth a word.
        h.assert.ok(!shown[0].includes('"class"'), shown[0]);
    });

    return root;
}

/**
 * The init command: detect what a view would preview with, then write it to the place the user
 * picks in the modal. Each case answers the modal through `answer`, so the tests cover exactly
 * the two buttons plus cancel.
 */
async function initTests() {
    const root = h.makeWorkspace();
    h.setWorkspace(root, { width: 55 });
    vscode.__state._commands = {};
    vscode.__state.textDocuments = [];
    h.ext.activate({ subscriptions: [] });

    async function init(file, answer) {
        let asked;
        const original = vscode.window.showInformationMessage;
        vscode.window.showInformationMessage = async (message, options, ...buttons) => {
            if (options?.modal) {
                asked = { message, detail: options.detail, buttons };
                return buttons.find((b) => b.startsWith(answer));
            }
            return undefined;
        };
        await vscode.__state._commands["tuiLivePreview.init"](vscode.Uri.file(file));
        vscode.window.showInformationMessage = original;
        return asked;
    }

    console.log("\ninit: sidecar");

    const plain = h.writeView(root, "Plain.tui.cs", h.layoutHalf("Demo", "Plain"));
    const asked = await init(plain, "Sidecar");
    const plainSidecar = path.join(root, "tests", "Plain.tui.json");

    h.test("init asks where to put the settings, showing what it detected", () => {
        h.assert.ok(asked, "no modal was shown");
        h.assert.strictEqual(asked.buttons.length, 2, JSON.stringify(asked.buttons));
        h.assert.ok(asked.detail.includes("class=Demo.Plain"), asked.detail);
        h.assert.ok(asked.detail.includes("width=55"), asked.detail);
    });

    h.test("init writes a sidecar named after the view", () => {
        h.assert.ok(fs.existsSync(plainSidecar), "no sidecar was written");
    });

    // The written sidecar must be one both readers accept, so it is read back through a real
    // render rather than inspected as text. The width setting is cleared first, so a --width 55
    // can only have come from the sidecar.
    vscode.__state.configuration = {};
    h.edit(plain);
    const plainLine = h.lastLine(await h.run("tuiLivePreview.render"));
    vscode.__state.configuration = { tuiLivePreview: { width: 55 } };

    h.test("the sidecar init writes is read back by a preview", () => {
        h.assert.ok(plainLine.includes('--class "Demo.Plain"'), plainLine);
        h.assert.ok(plainLine.includes("--width 55"), plainLine);
        h.assert.ok(plainLine.includes("--height 24"), plainLine);
    });

    h.test("the sidecar documents the optional keys as comments", () => {
        const body = fs.readFileSync(plainSidecar, "utf8");
        h.assert.ok(body.includes('// "extra"'), body);
        h.assert.ok(body.includes('// "reference"'), body);
    });

    fs.writeFileSync(plainSidecar, '{ "width": 99 }');
    await init(plain, "Sidecar");

    h.test("init never overwrites an existing sidecar", () => {
        h.assert.strictEqual(fs.readFileSync(plainSidecar, "utf8"), '{ "width": 99 }');
    });

    console.log("init: annotation");

    const bare = h.writeView(root, "Bare.tui.cs", h.layoutHalf("Demo", "Bare"));
    await init(bare, "Annotation");
    const bareText = fs.readFileSync(bare, "utf8");

    h.test("init puts the annotation on the first line of the view", () => {
        h.assert.ok(
            bareText.startsWith("// @tuipreview class=Demo.Bare width=55 height=24\n"),
            bareText.split("\n")[0]
        );
    });

    h.test("init leaves the rest of the view as it was", () => {
        h.assert.ok(bareText.endsWith(h.layoutHalf("Demo", "Bare")), bareText);
    });

    h.test("init writes no sidecar when the annotation is chosen", () => {
        h.assert.ok(!fs.existsSync(path.join(root, "tests", "Bare.tui.json")));
    });

    await init(bare, "Annotation");

    h.test("init does not add a second annotation", () => {
        const count = fs.readFileSync(bare, "utf8").split("@tuipreview").length - 1;
        h.assert.strictEqual(count, 1);
    });

    const half = h.writeView(root, "Half.tui.cs", h.layoutHalf("Demo", "Half"));
    fs.writeFileSync(
        path.join(root, "tests", "Half.tui.json"),
        JSON.stringify({ class: "Demo.Half", width: 40 })
    );
    await init(half, "Annotation");

    h.test("init only writes what the sidecar does not already state", () => {
        // Repeating class and width in the annotation would shadow any later edit to the
        // sidecar, which is the one place the user put them on purpose.
        const first = fs.readFileSync(half, "utf8").split("\n")[0];
        h.assert.strictEqual(first, "// @tuipreview height=24");
    });

    console.log("init: cancel");

    const untouched = h.writeView(root, "Untouched.tui.cs", h.layoutHalf("Demo", "Untouched"));
    await init(untouched, "nothing matches this");

    h.test("cancelling init writes nothing", () => {
        h.assert.strictEqual(fs.readFileSync(untouched, "utf8"), h.layoutHalf("Demo", "Untouched"));
        h.assert.ok(!fs.existsSync(path.join(root, "tests", "Untouched.tui.json")));
    });

    return root;
}

void main().then(async (root) => {
    fs.rmSync(root, { recursive: true, force: true });

    console.log("\nmanifest when-clauses");
    testWhenClauses();

    // Own workspace and its own activate() call, so the terminal map and the shell under
    // test are not inherited from the suite above.
    const shellRoot = await shellTests();
    fs.rmSync(shellRoot, { recursive: true, force: true });

    const liveRoot = await liveTests();
    fs.rmSync(liveRoot, { recursive: true, force: true });

    const classRoot = await classNameTests();
    fs.rmSync(classRoot, { recursive: true, force: true });

    const settingsRoot = await viewSettingsTests();
    fs.rmSync(settingsRoot, { recursive: true, force: true });

    const initRoot = await initTests();
    fs.rmSync(initRoot, { recursive: true, force: true });

    console.log(`\n${h.state.passed} passed, ${h.state.failed} failed`);
    process.exitCode = h.state.failed === 0 ? 0 : 1;
});
