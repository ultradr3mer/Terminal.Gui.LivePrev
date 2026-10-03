// Minimal stand-in for the `vscode` module, which the extension host injects at runtime and
// which is therefore not resolvable from a plain `node` process. Enough surface to load
// out/extension.js and drive its pure logic (x:Class parsing, CLI discovery, quoting).
const path = require("path");
const fs = require("fs");

function makeWindow() {
    return {
        activeTextEditor: undefined,
        terminals: [],
        showErrorMessage: () => undefined,
        showInformationMessage: () => undefined,
        showInputBox: async () => undefined,
        showWarningMessage: () => undefined,
        async showTextDocument(doc) {
            (this.shownDocuments ??= []).push(doc.uri.fsPath);
            return { document: doc };
        },
        createTerminal(options) {
            const t = {
                name: options?.name ?? "terminal",
                options,
                sent: [],
                shown: 0,
                disposed: 0,
                sendText(text) {
                    this.sent.push(text);
                },
                show() {
                    this.shown++;
                },
                dispose() {
                    this.disposed++;
                },
            };
            this.terminals.push(t);
            return t;
        },
        createOutputChannel(name) {
            const ch = {
                name,
                lines: [],
                shown: 0,
                appendLine(line) {
                    this.lines.push(line);
                },
                append(text) {
                    this.lines.push(text);
                },
                show() {
                    this.shown++;
                },
                dispose() {},
                clear() {
                    this.lines.length = 0;
                },
            };
            (this.channels ??= []).push(ch);
            return ch;
        },
        createStatusBarItem(alignment, priority) {
            const item = {
                alignment,
                priority,
                text: "",
                tooltip: "",
                command: undefined,
                shown: 0,
                show() {
                    this.shown++;
                },
                hide() {
                    this.shown--;
                },
                dispose() {},
            };
            (this.statusBarItems ??= []).push(item);
            return item;
        },
        onDidCloseTerminal() {
            return { dispose() {} };
        },
        async withProgress(_options, task) {
            return task({ report() {} });
        },
        setStatusBarMessage(text) {
            (this.statusMessages ??= []).push(text);
            return { dispose() {} };
        },
    };
}

const state = {
    configuration: {},
    workspaceFolders: undefined,
    _window: undefined,
};

function getConfiguration(section) {
    return {
        get(key, fallback) {
            const bag = state.configuration[section] ?? {};
            return key in bag ? bag[key] : fallback;
        },
    };
}

const vscode = {
    __state: state,
    get window() {
        state._window ??= makeWindow();
        return state._window;
    },
    // The user's default terminal profile, which is what a plain createTerminal opens.
    // Settable so tests can exercise both PowerShell and cmd.exe command lines.
    env: {
        get shell() {
            return state.shell;
        },
    },
    workspace: {
        get workspaceFolders() {
            return state.workspaceFolders;
        },
        get textDocuments() {
            return state.textDocuments ?? [];
        },
        getConfiguration,
        onDidChangeTextDocument(handler) {
            (state._docChangeHandlers ??= []).push(handler);
            return { dispose() {} };
        },
        // A document backed directly by the file: edits land on disk at once, which is what an
        // applied-then-saved edit amounts to, and is all the tests need to observe.
        async openTextDocument(uri) {
            const open = (state.textDocuments ?? []).find((d) => d.uri.fsPath === uri.fsPath);
            return (
                open ?? {
                    uri,
                    isDirty: false,
                    getText: () => fs.readFileSync(uri.fsPath, "utf8"),
                    async save() {
                        (state.saved ??= []).push(uri.fsPath);
                        return true;
                    },
                }
            );
        },
        async applyEdit(edit) {
            for (const { uri, position, text } of edit.inserts) {
                const lines = fs.readFileSync(uri.fsPath, "utf8").split("\n");
                const line = lines[position.line] ?? "";
                lines[position.line] =
                    line.slice(0, position.character) + text + line.slice(position.character);
                fs.writeFileSync(uri.fsPath, lines.join("\n"));
            }
            (state.appliedEdits ??= []).push(edit);
            return true;
        },
    },
    WorkspaceEdit: class {
        constructor() {
            this.inserts = [];
        }
        insert(uri, position, text) {
            this.inserts.push({ uri, position, text });
        }
    },
    Position: class {
        constructor(line, character) {
            this.line = line;
            this.character = character;
        }
    },
    commands: {
        registerCommand(name, fn) {
            (state._commands ??= {})[name] = fn;
            return { dispose() {} };
        },
        async getCommands() {
            return Object.keys(state._commands ?? {});
        },
    },
    ProgressLocation: { Notification: 15 },
    StatusBarAlignment: { Left: 1, Right: 2 },
    Uri: {
        file: (p) => ({ scheme: "file", fsPath: p, path: p }),
    },
};

module.exports = vscode;
