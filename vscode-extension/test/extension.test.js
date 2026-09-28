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

const state = { passed: 0, failed: 0, root: undefined, xaml: undefined };

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
    fs.writeFileSync(path.join(root, "cli", "cli.csproj"), "<Project/>");
    fs.writeFileSync(path.join(root, "cli", "bin", "Debug", "net10.0", "cli.exe"), "MZ");
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
 * Reused terminals accumulate, so lastLine(t)  is the first command ever run against that file,
 * not the one just issued. Assertions must look at the tail.
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

function writeXaml(root, name, body) {
    const file = path.join(root, "tests", name);
    fs.writeFileSync(file, body);
    return file;
}

/**
 * Points the stub at a given file as the active editor, resolving it to the file a user
 * actually acts on.
 *
 * The menus are offered on `.tui.cs` only, so a `.tui.xaml` is reachable through its code-behind.
 * Creating that sibling here means a test can describe a view the way one is really written -
 * a XAML and its code-behind - and still have `edit()` name the menu target. Without this,
 * every test would have to repeat the pairing, and a test that passed a bare `.tui.xaml` would
 * be testing something the extension no longer offers.
 *
 * A `.tui.cs` is left alone, and an unpaired `.tui.xaml` still gets a code-behind, because an
 * unpaired one is exactly the case where "what does the extension do?" is worth asking.
 */
function edit(file) {
    let entry = file;

    if (file.endsWith(".tui.xaml")) {
        const codeBehind = file.replace(/\.tui\.xaml$/, ".tui.cs");
        if (!fs.existsSync(codeBehind)) {
            fs.writeFileSync(
                codeBehind,
                "// test fixture: a code-behind that exists only so the view is reachable\n" +
                    "public partial class Placeholder { }\n"
            );
        }
        entry = codeBehind;
    }

    vscode.window.activeTextEditor = { document: { uri: vscode.Uri.file(entry) } };
}

/**
 * Runs a command expecting it to prompt, returning what was typed into the prompt.
 *
 * Distinguishes "the extension inferred the name" from "the extension asked", which is the
 * whole point of the C# class-name tests: an inference that silently degrades into a prompt
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
    setWorkspace, state, test, vscode, writeXaml, edit,
};

const h = module.exports;

async function main() {
    const root = makeWorkspace();
    h.state.root = root;

    h.setWorkspace(root);
    vscode.__state._commands = {};
    h.ext.activate({ subscriptions: [] });

    const xaml = h.writeXaml(
        root,
        "My.tui.xaml",
        `<Window x:Class="MyApp.Views.MainWindow"
        xmlns="http://schemas.gui-cs.github.io/tui/2026/xaml">
  <Label Text="hi" />
</Window>`
    );

    // A .tui.xaml on its own is no longer a menu target, so the file acted on is its .tui.cs
    // code-behind - which resolves back to this XAML. That is the real-world shape of a paired
    // view, so exercising it here keeps the two paths honest about each other.
    const entry = path.join(root, "tests", "My.tui.cs");
    fs.writeFileSync(entry, "namespace MyApp.Views;\npublic partial class MainWindow { }\n");
    h.state.xaml = xaml;

    console.log("command registration");
    h.test("registers every contributed command", () => {
        for (const name of [
            "tuiLivePreview.render",
            "tuiLivePreview.watch",
            "tuiLivePreview.interactive",
            "tuiLivePreview.showGeneratedCode",
            "tuiLivePreview.stopAll",
        ]) {
            h.assert.ok(vscode.__state._commands[name], `missing ${name}`);
        }
    });

    console.log("render");
    h.edit(entry);
    const t = await h.run("tuiLivePreview.render");
    const line = h.lastLine(t);

    h.test("emits exactly one command line", () => {
        h.assert.strictEqual(t.sent.length, 1, t.sent.join("|"));
    });
    h.test("prefers the built cli.exe over dotnet run", () => {
        h.assert.ok(line.includes("cli.exe"), line);
    });
    h.test("derives --class from x:Class", () => {
        h.assert.ok(line.includes("MyApp.Views.MainWindow"), line);
    });
    h.test("quotes the absolute xaml path", () => {
        h.assert.ok(line.includes(`"${xaml}"`), line);
    });
    h.test("passes width and height, not --watch", () => {
        h.assert.ok(line.includes("--width"), line);
        h.assert.ok(line.includes("--height"), line);
        h.assert.ok(!line.includes("--watch"), line);
    });
    h.test("passes a --project that exists on disk", () => {
        const m = /--project\s+"([^"]+)"/.exec(line);
        h.assert.ok(m, line);
        h.assert.ok(fs.existsSync(m[1]), `--project does not exist: ${m[1]}`);
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

    console.log("show generated code");
    const gen = await h.run("tuiLivePreview.showGeneratedCode");
    h.test("uses --gen-test with TUI_DUMP=1", () => {
        h.assert.ok(h.lastLine(gen).includes("--gen-test"), h.lastLine(gen));
        h.assert.strictEqual(gen.options.env.TUI_DUMP, "1");
    });

    console.log("terminal reuse");
    const terminalsBefore = vscode.window.terminals.length;
    const sendsBefore = t.sent.length;
    await vscode.__state._commands["tuiLivePreview.render"]();
    h.test("reuses the terminal for the same xaml", () => {
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
    const spaced = h.writeXaml(root, "My View.tui.xaml", `<Window x:Class="Spaced.Class"></Window>`);
    h.edit(spaced);
    const spacedRun = await h.run("tuiLivePreview.render");
    h.test("quotes a path containing spaces", () => {
        h.assert.ok(h.lastLine(spacedRun).includes(`"${spaced}"`), h.lastLine(spacedRun));
    });

    console.log("class name resolution");

    h.edit(h.writeXaml(root, "NoClass.tui.xaml", `<Window xmlns="http://x"><Label/></Window>`));
    let prompted = false;
    const originalInput = vscode.window.showInputBox;
    vscode.window.showInputBox = async () => {
        prompted = true;
        return "Typed.In.Class";
    };
    const typed = await h.run("tuiLivePreview.render");
    vscode.window.showInputBox = originalInput;

    h.test("prompts when x:Class is absent", () => {
        h.assert.ok(prompted, "expected an input box");
    });
    h.test("uses the typed class name", () => {
        h.assert.ok(h.lastLine(typed).includes("Typed.In.Class"), h.lastLine(typed));
    });

    h.edit(h.writeXaml(root, "Single.tui.xaml", `<Window x:Class='Single.Quote.Class'></Window>`));
    const single = await h.run("tuiLivePreview.render");
    h.test("handles single-quoted x:Class", () => {
        h.assert.ok(h.lastLine(single).includes("Single.Quote.Class"), h.lastLine(single));
    });

    h.edit(h.writeXaml(root, "Single.tui.xaml", `<Window x:Class='Single.Quote.Class'></Window>`));
    vscode.__state.configuration.tuiLivePreview = { className: "Over.Ridden" };
    const overridden = await h.run("tuiLivePreview.render");
    h.test("className setting overrides x:Class", () => {
        h.assert.ok(h.lastLine(overridden).includes("Over.Ridden"), h.lastLine(overridden));
    });
    vscode.__state.configuration.tuiLivePreview = {};

    console.log("cli discovery");

    // No build output: must fall back to `dotnet run` against cli/cli.csproj.
    fs.rmSync(path.join(root, "cli", "bin"), { recursive: true, force: true });
    h.edit(xaml);
    const viaDotnet = await h.run("tuiLivePreview.render");
    const dotnetLine = h.lastLine(viaDotnet);

    h.test("falls back to dotnet run when nothing is built", () => {
        h.assert.ok(dotnetLine.startsWith("dotnet run"), dotnetLine);
    });
    h.test("passes the -- separator so dotnet does not eat the CLI flags", () => {
        h.assert.ok(dotnetLine.includes("-- "), dotnetLine);
    });
    h.test("dotnet fallback still resolves an existing --project", () => {
        const m = /--project\s+"([^"]+)"/.exec(dotnetLine);
        h.assert.ok(m && fs.existsSync(m[1]), dotnetLine);
    });

    console.log("error handling");

    vscode.__state.configuration.tuiLivePreview = {
        cliPath: path.join(root, "does", "not", "exist.exe"),
    };
    h.edit(xaml);
    const badPathMessage = await h.runExpectingError("tuiLivePreview.render");
    vscode.__state.configuration.tuiLivePreview = {};

    h.test("missing cliPath reports a readable error", () => {
        h.assert.ok(badPathMessage, "expected an error message");
        h.assert.ok(/does not exist/.test(badPathMessage), badPathMessage);
    });

    h.setWorkspace(undefined);
    const noFileMessage = await h.runExpectingError("tuiLivePreview.render");
    h.test("no workspace and no open xaml reports an error", () => {
        h.assert.ok(noFileMessage, "expected an error message");
    });

    console.log("stopAll");
    h.setWorkspace(root);
    vscode.__state._commands = {};
    h.ext.activate({ subscriptions: [] });
    h.edit(xaml);
    await vscode.__state._commands["tuiLivePreview.render"]();
    await vscode.__state._commands["tuiLivePreview.stopAll"]();
    h.test("stopAll disposes preview terminals", () => {
        h.assert.ok(
            vscode.window.terminals.every((t) => t.disposed > 0),
            "expected every terminal to be disposed"
        );
    });

    console.log("context-menu uri (regression: the uri argument used to be ignored)");
    const fromMenu = h.writeXaml(root, "FromMenu.tui.xaml", `<Window x:Class="Menu.Target"/>`);
    const active = h.writeXaml(root, "Active.tui.xaml", `<Window x:Class="Active.Target"/>`);
    h.edit(active);

    // The explorer menu offers commands on .tui.cs, so the uri VS Code passes is the
    // code-behind, not the XAML behind it.
    const fromMenuEntry = fromMenu.replace(/\.tui\.xaml$/, ".tui.cs");
    fs.writeFileSync(fromMenuEntry, "public partial class Placeholder { }\n");

    // What VS Code actually does when you right-click one file in the explorer while a
    // different one is the active tab. Before the fix this previewed the active tab.
    const menuRun = await h.run("tuiLivePreview.render", vscode.Uri.file(fromMenuEntry));
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
            lines.some((l) => l.includes("running:") && l.includes("cli.exe")),
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
        for (const n of ["render", "watch", "interactive", "showGeneratedCode", "showLog", "stopAll"]) {
            h.assert.ok(
                entry.includes(`tuiLivePreview.${n}`),
                `registered-commands log is missing ${n}: ${entry}`
            );
        }
    });

    return root;
}

/**
 * The command line has to be valid in the user's *default* shell.
 *
 * PowerShell and cmd.exe disagree about a line that starts with a quoted path, and there is no
 * form both accept: cmd.exe needs the bare quoted path, PowerShell needs its `&` call operator
 * and rejects the bare form with "Unexpected token 'xaml'". Prefixing `&` unconditionally is
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

    const xaml = h.writeXaml(root, "Shell.tui.xaml", '<Window x:Class="App.Shell" />');
    h.edit(xaml);

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
    vscode.__state.configuration = { tuiLivePreview: { cliPath: path.join(root, "cli", "cli.csproj") } };
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
 * The CLI can only read from disk, so an unsaved buffer has to be mirrored to a scratch file
 * that the preview then watches. Two things can silently go wrong here, and both look like an
 * unrelated bug rather than a preview bug:
 *
 *   1. The scratch path changes per edit. The interactive host polls one specific path, so a
 *      fresh temp file per keystroke leaves it watching something that no longer exists and
 *      the preview simply stops updating.
 *   2. The code-behind is not passed explicitly. The pipeline looks for a sibling `.cs`, and
 *      a scratch file has no siblings, so it falls back to a stub with no ViewModel and every
 *      `{Bind ...}` fails to compile with CS0103.
 */
async function liveTests() {
    const h = module.exports;
    const root = h.makeWorkspace();
    h.setWorkspace(root);
    vscode.__state._commands = {};
    h.ext.activate({ subscriptions: [] });

    const saved = '<Window x:Class="App.Live" Title="saved" />';
    const real = h.writeXaml(root, "Live.tui.xaml", saved);

    // A sibling code-behind, which is what has to survive the move to a scratch file.
    fs.writeFileSync(real.replace(/\.xaml$/, ".cs"), "public partial class Live { }");

    let text = '<Window x:Class="App.Live" Title="UNSAVED EDIT" />';
    const doc = {
        uri: vscode.Uri.file(real),
        isDirty: true,
        getText: () => text,
    };
    vscode.__state.textDocuments = [doc];

    // Explicit, not left to the workspace search. The .tui.cs written just above is this
    // file's code-behind, and a search that does not know about that would pick it instead.
    h.edit(real);

    const scratchOf = (line) => {
        const m = line.match(/--xaml "([^"]+)"/);
        h.assert.ok(m, `expected an --xaml path: ${line}`);
        return m[1];
    };

    console.log("unsaved-buffer previewing");
    const t = await h.run("tuiLivePreview.render");
    const first = h.lastLine(t);

    h.test("previews a scratch copy, not the file on disk", () => {
        h.assert.ok(!first.includes(`--xaml "${real}"`), `should not read the real path: ${first}`);
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

    h.test("passes --code-behind so the ViewModel still binds", () => {
        h.assert.ok(first.includes("--code-behind"), `expected --code-behind: ${first}`);
        h.assert.ok(
            first.includes(real.replace(/\.xaml$/, ".cs")),
            `expected the real code-behind: ${first}`
        );
    });

    const firstScratch = scratchOf(first);

    text = '<Window x:Class="App.Live" Title="SECOND UNSAVED EDIT" />';
    const t2 = await h.run("tuiLivePreview.render");

    h.test("edits keep the same scratch path", () => {
        // The interactive host polls one path; if it moved, a running preview goes deaf.
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
            h.lastLine(t3).includes(`--xaml "${real}"`),
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
        // The C# authoring path. It has to match, or right-clicking a .tui.cs view shows
        // nothing at all - which is exactly the "menus silently disappeared" failure mode.
        const re = regexOf(menus["explorer/context"][0].when);
        h.assert.ok(re.test("Counter.tui.cs"), "did not match a C# view");
        h.assert.ok(re.test("My View.tui.cs"), "did not match a C# view with spaces");
    });

    h.test("the when regex rejects a .tui.xaml filename", () => {
        // The menus are narrowed to .tui.cs. A .tui.xaml view is still reachable - through
        // its own .tui.cs code-behind, which resolves to it - so offering a second entry
        // here would only duplicate it.
        const re = regexOf(menus["explorer/context"][0].when);
        h.assert.ok(!re.test("MainWindow.tui.xaml"), "the menus should be .tui.cs only");
    });

    h.test("the when regex rejects other files", () => {
        const re = regexOf(menus["explorer/context"][0].when);
        for (const other of [
            "App.xaml",
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

    h.test("every menu command is declared and has an activation event", () => {
        const declared = new Set(manifest.contributes.commands.map((c) => c.command));
        const events = new Set(
            manifest.activationEvents
                .filter((e) => e.startsWith("onCommand:"))
                .map((e) => e.slice("onCommand:".length))
        );

        for (const [menu, items] of Object.entries(menus)) {
            for (const item of items) {
                h.assert.ok(declared.has(item.command), `${menu}: ${item.command} not declared`);
                h.assert.ok(
                    events.has(item.command),
                    `${menu}: ${item.command} has no onCommand activation event`
                );
            }
        }
    });
}

/**
 * The C# authoring path: a `.tui.cs` file previewed with `--view`.
 *
 * Two things are specific to it and neither is covered anywhere else. The class name has to
 * be *inferred* from C# source, because a C# file has no `x:Class` attribute to read - get
 * that wrong and every preview prompts for a name the file already states. And the command
 * line must carry `--view` rather than `--xaml`, which the CLI treats as a hard error when
 * both or neither are given.
 */
async function tuiCsTests() {
    const h = module.exports;
    const root = h.makeWorkspace();
    h.setWorkspace(root);
    vscode.__state._commands = {};
    h.ext.activate({ subscriptions: [] });

    const write = (name, body) => {
        const file = path.join(root, "tests", name);
        fs.mkdirSync(path.dirname(file), { recursive: true });
        fs.writeFileSync(file, body);
        return file;
    };

    console.log("C# view: class name inference");

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
    ];

    for (const [label, body, expected] of cases) {
        const file = write(`${label.replace(/\W+/g, "")}.tui.cs`, body);
        h.edit(file);
        const t = await h.run("tuiLivePreview.render");
        h.test(`infers the class from ${label}`, () => {
            h.assert.ok(
                h.lastLine(t).includes(`--class "${expected}"`),
                `expected --class "${expected}": ${h.lastLine(t)}`
            );
        });
    }

    // A partial class is half a class: the other file supplies the constructor, and the one
    // the CLI instantiates is not knowable from this file alone. Guessing would produce a
    // confusing runtime failure instead of an honest prompt.
    const partial = write(
        "Partial.tui.cs",
        "namespace N;\npublic partial class Split : Window { }\n"
    );
    h.edit(partial);
    const pt = await h.runExpectingInput("tuiLivePreview.render");
    h.test("refuses to guess for a partial class", () => {
        h.assert.ok(pt, "expected the user to be asked for a class name");
        h.assert.ok(
            pt.prompt.includes("Could not determine"),
            `expected the "cannot determine" prompt, got: ${pt.prompt}`
        );
    });

    console.log("C# view: command line");

    const file = write(
        "Flag.tui.cs",
        "namespace Flags;\npublic class Flagged : Window { }\n"
    );
    h.edit(file);
    const t = await h.run("tuiLivePreview.render");
    const line = h.lastLine(t);

    h.test("uses --view, not --xaml", () => {
        h.assert.ok(line.includes("--view"), `expected --view: ${line}`);
        h.assert.ok(!line.includes("--xaml"), `must not pass --xaml: ${line}`);
    });
    h.test("passes no --code-behind for a C# view", () => {
        // A .tui.cs view is the whole class. Passing a code-behind would make the CLI try to
        // compile the file alongside itself.
        h.assert.ok(!line.includes("--code-behind"), `must not pass --code-behind: ${line}`);
    });
    h.test("the scratch copy keeps the .tui.cs suffix", () => {
        // The suffix decides which authoring path the file is treated as, so a copy named
        // .tui.xaml would be previewed through the generator instead.
        const m = line.match(/(?:--view|--xaml) "([^"]+)"/);
        h.assert.ok(m, `expected a source path: ${line}`);
        h.assert.ok(
            m[1].endsWith(".tui.cs"),
            `the scratch copy must keep .tui.cs: ${m[1]}`
        );
    });

    console.log("C# view: --project is the view's own project");

    // The CLI compiles the view against --project's references and packages, so it must be
    // the project the view belongs to, not the preview tool's own cli.csproj.
    const projectOf = (l) => (/--project\s+"([^"]+)"/.exec(l) || [])[1];

    write("App/App.csproj", "<Project/>");
    h.edit(write("App/Views/Deep.tui.cs", "namespace App.Views;\npublic class Deep : Window { }\n"));
    const nearest = projectOf(h.lastLine(await h.run("tuiLivePreview.render")));
    h.test("passes the nearest .csproj above the view", () => {
        h.assert.strictEqual(nearest, path.join(root, "tests", "App", "App.csproj"));
    });

    h.test("falls back to cli/cli.csproj when no project owns the view", () => {
        // Flag.tui.cs sits in tests/, which has no .csproj anywhere above it.
        h.assert.strictEqual(projectOf(line), path.join(root, "cli", "cli.csproj"));
    });

    write("Outer/Outer.csproj", "<Project/>");
    write("Outer/Two/A.csproj", "<Project/>");
    write("Outer/Two/B.csproj", "<Project/>");
    h.edit(write("Outer/Two/V.tui.cs", "namespace O;\npublic class V : Window { }\n"));
    const ambiguous = projectOf(h.lastLine(await h.run("tuiLivePreview.render")));
    h.test("skips a directory with two projects and keeps walking up", () => {
        h.assert.strictEqual(ambiguous, path.join(root, "tests", "Outer", "Outer.csproj"));
    });

    console.log("XAML view: unchanged");

    const xaml = write("Legacy.tui.xaml", '<Window x:Class="Legacy.Main" />');
    h.edit(xaml);
    const xt = await h.run("tuiLivePreview.render");
    h.test("a .tui.xaml still uses --xaml and x:Class", () => {
        h.assert.ok(
            h.lastLine(xt).includes('--xaml') && h.lastLine(xt).includes('--class "Legacy.Main"'),
            h.lastLine(xt)
        );
    });

    console.log("C# view: a code-behind previews its XAML");

    // Paired.tui.cs is the code-behind of Paired.tui.xaml, not a view of its own: it is a
    // partial class whose controls exist only in the generated half, so instantiating it
    // standalone cannot work. The sibling is what tells them apart, and right-clicking the
    // code-behind therefore previews the XAML.
    write("Paired.tui.xaml", '<Window x:Class="Paired.Main" />');
    const paired = write("Paired.tui.cs", "namespace P;\npublic partial class Paired { }\n");
    h.edit(paired);
    const pt2 = await h.run("tuiLivePreview.render");
    const pairedLine = h.lastLine(pt2);

    h.test("a code-behind previews its sibling XAML, not itself", () => {
        h.assert.ok(pairedLine.includes("--xaml"), `expected --xaml: ${pairedLine}`);
        h.assert.ok(
            pairedLine.includes("Paired.tui.xaml"),
            `expected the sibling XAML: ${pairedLine}`
        );
        h.assert.ok(
            !pairedLine.includes("--view"),
            `must not treat a code-behind as a standalone view: ${pairedLine}`
        );
    });
    h.test("a code-behind is passed as --code-behind", () => {
        h.assert.ok(
            pairedLine.includes("--code-behind") && pairedLine.includes("Paired.tui.cs"),
            `expected the code-behind to be named: ${pairedLine}`
        );
    });
    h.test("the class name still comes from the XAML's x:Class", () => {
        h.assert.ok(
            pairedLine.includes('--class "Paired.Main"'),
            `expected x:Class to win over the partial C# class: ${pairedLine}`
        );
    });

    return root;
}

/**
 * An app that references TuiPreview.Host hosts the preview itself: the extension builds it,
 * runs a shadow copy of its output with --tui-preview, and passes no --project. dotnet is
 * replaced, so no real build runs.
 */
async function hostedAppTests() {
    const h = module.exports;
    const root = h.makeWorkspace();
    h.setWorkspace(root);
    vscode.__state._commands = {};
    h.ext.activate({ subscriptions: [] });

    const write = (name, body) => {
        const file = path.join(root, name);
        fs.mkdirSync(path.dirname(file), { recursive: true });
        fs.writeFileSync(file, body);
        return file;
    };

    // The app's last build: TargetPath must exist, and its folder is what gets copied.
    const bin = path.join(root, "App", "bin", "Debug", "net10.0");
    const targetPath = write("App/bin/Debug/net10.0/App.dll", "MZ");
    write("App/bin/Debug/net10.0/App.deps.json", "{}");

    const calls = [];
    let buildCode = 0;
    h.ext.__setRunProcess(async (command, args) => {
        calls.push([command, ...args].join(" "));
        return args[0] === "build"
            ? { code: buildCode, output: buildCode === 0 ? "" : "error CS0029" }
            : { code: 0, output: `${targetPath}\n` };
    });

    const project = write(
        "App/App.csproj",
        '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType></PropertyGroup>' +
            '<ItemGroup><PackageReference Include="TuiPreview.Host" Version="0.1.0" /></ItemGroup></Project>'
    );
    const view = write("App/MainView.tui.cs", "namespace App;\npublic class MainView : Window { }\n");
    const copies = [];

    console.log("hosted app: launch");

    h.edit(view);
    const t = await h.run("tuiLivePreview.interactive");
    const line = h.lastLine(t);
    const dll = (/^dotnet "([^"]+)" --tui-preview /.exec(line) || [])[1];
    if (dll) {
        copies.push(path.dirname(dll));
    }

    h.test("builds the app first", () => {
        h.assert.ok(calls[0]?.startsWith(`dotnet build ${project}`), `expected a build: ${calls[0]}`);
    });
    h.test("runs the app with --tui-preview", () => {
        h.assert.ok(dll, `expected dotnet "<app>.dll" --tui-preview: ${line}`);
    });
    h.test("runs a shadow copy, so the app's bin stays unlocked", () => {
        h.assert.ok(!dll.startsWith(bin), `must not run from bin: ${dll}`);
        h.assert.ok(fs.existsSync(dll), `the copy must exist: ${dll}`);
        h.assert.ok(fs.existsSync(path.join(path.dirname(dll), "App.deps.json")), "the whole output folder is copied");
    });
    h.test("passes --view and --class but no --project", () => {
        h.assert.ok(line.includes('--class "App.MainView"'), `expected the class: ${line}`);
        h.assert.ok(/--view "[^"]+\.tui\.cs"/.test(line), `expected --view: ${line}`);
        h.assert.ok(!line.includes("--project"), `the running app is the project: ${line}`);
    });

    const t2 = await h.run("tuiLivePreview.interactive");
    copies.push(path.dirname((/^dotnet "([^"]+)"/.exec(h.lastLine(t2)) || [])[1] ?? ""));
    h.test("relaunching replaces the terminal instead of typing into a running preview", () => {
        h.assert.ok(t2 && t2 !== t, "expected a new terminal");
        h.assert.ok(t.disposed > 0, "expected the old terminal to be closed");
    });

    buildCode = 1;
    const t3 = await h.run("tuiLivePreview.interactive");
    copies.push(path.dirname((/^dotnet "([^"]+)"/.exec(h.lastLine(t3) ?? "") || [])[1] ?? ""));
    buildCode = 0;
    h.test("a failed build still launches the last build, so the view's errors reach the log", () => {
        h.assert.ok(t3 && h.lastLine(t3).includes("--tui-preview"), "expected a launch despite the failed build");
    });

    console.log("hosted app: detection");

    write("Lib/Lib.csproj", '<Project Sdk="Microsoft.NET.Sdk"><ItemGroup><PackageReference Include="TuiPreview.Host" /></ItemGroup></Project>');
    const libView = write("Lib/V.tui.cs", "namespace L;\npublic class V : Window { }\n");
    h.edit(libView);
    const lib = h.lastLine(await h.run("tuiLivePreview.render"));
    h.test("a class library still uses the CLI", () => {
        h.assert.ok(lib.includes("cli.exe") && lib.includes("--project"), `expected the CLI: ${lib}`);
    });

    write("Props/Props.csproj", '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>WinExe</OutputType></PropertyGroup></Project>');
    write("Props/obj/project.assets.json", '{"libraries":{"TuiPreview.Host/0.1.0":{}}}');
    h.edit(write("Props/V.tui.cs", "namespace P;\npublic class V : Window { }\n"));
    const props = h.lastLine(await h.run("tuiLivePreview.render"));
    copies.push(path.dirname((/^dotnet "([^"]+)"/.exec(props) || [])[1] ?? ""));
    h.test("finds a reference only the restore output shows (Directory.Build.props)", () => {
        h.assert.ok(props.includes("--tui-preview"), `expected the app launcher: ${props}`);
    });

    console.log("hosted app: launchMode");

    vscode.__state.configuration = { tuiLivePreview: { launchMode: "cli" } };
    h.edit(view);
    const forced = h.lastLine(await h.run("tuiLivePreview.render"));
    h.test('"cli" uses the CLI even for a hosting app', () => {
        h.assert.ok(forced.includes("cli.exe") && !forced.includes("--tui-preview"), forced);
    });

    vscode.__state.configuration = { tuiLivePreview: { launchMode: "app" } };
    h.edit(libView);
    const message = await h.runExpectingError("tuiLivePreview.render");
    h.test('"app" explains why a project cannot host the preview', () => {
        h.assert.ok(message?.includes("TuiPreview.Host"), `expected an explanation: ${message}`);
    });

    vscode.__state.configuration = { tuiLivePreview: {} };
    for (const copy of copies.filter((c) => c && c !== ".")) {
        fs.rmSync(copy, { recursive: true, force: true });
    }
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

    const tuiCsRoot = await tuiCsTests();
    fs.rmSync(tuiCsRoot, { recursive: true, force: true });

    const hostedRoot = await hostedAppTests();
    fs.rmSync(hostedRoot, { recursive: true, force: true });

    console.log(`\n${h.state.passed} passed, ${h.state.failed} failed`);
    process.exitCode = h.state.failed === 0 ? 0 : 1;
});

