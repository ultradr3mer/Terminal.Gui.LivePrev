# Terminal.Gui XAML Live Preview — VS Code extension

Right-click any `*.tui.xaml` file and preview it as a real Terminal.Gui view, without
leaving the editor.

## Commands

Available from the explorer context menu, the editor tab context menu, and the command
palette (**TUI Preview:**). They only appear on `*.tui.xaml` files, so ordinary WPF
`.xaml` files are unaffected.

| Command | What it does |
|---|---|
| **Interactive (live view)** | Full Terminal.Gui app in a terminal: log pane on top, the real view below, so you can type in `TextField`s and press `Button`s. |
| **Render Once** | Prints one frame as characters. Fast; good for a quick check. |
| **Watch (re-render on save)** | Re-renders on every save of the XAML or its code-behind. |
| **Show Generated C#** | Dumps the C# the source generator produced (`--gen-test`). |
| **Show Log** | Opens the **TUI Preview** output channel (see Troubleshooting). |
| **Stop All** | Kills every preview terminal this extension opened. |

The target file comes from the item you right-clicked. If you invoke from the command
palette instead, the active editor is used.

## The class name is inferred

The CLI requires `--class` and will not infer it, so the extension reads it from the
`x:Class` attribute of the file you right-clicked. You are only prompted if that attribute
is missing. Override it with `tuiLivePreview.className` when previewing a file whose
`x:Class` does not match what you want.

```xml
<Window x:Class="MyApp.Views.MainWindow" ...>   <!-- --class comes from here -->
```

## Finding the CLI

In order:

1. `tuiLivePreview.cliPath`, if set. Accepts a built `cli.exe`/`cli.dll` or a `.csproj`
   (run with `dotnet run`).
2. A built binary at `cli/bin/Debug/net*/cli.exe` in the workspace — **preferred**, because
   a preview is frequent and interactive and a full restore/build on every right-click
   makes the extension feel broken.
3. `dotnet run --project cli/cli.csproj`.

So with no configuration at all, opening this repo and right-clicking
`tests\MainWindow.tui.xaml` just works.

## Terminals

Previews run in integrated terminals, not an output channel: interactive mode drives
Terminal.Gui's ANSI driver, reads keys and owns the screen, so it needs a real TTY.

One terminal is kept per XAML file and reused, because watch and interactive modes run
until stopped — a new terminal per right-click would leak a process each time. Re-running a
preview into the same terminal leaves the previous one running, so use **Stop All** (or the
terminal's trash icon) to clean up.

## Settings

| Setting | Default | Purpose |
|---|---|---|
| `tuiLivePreview.cliPath` | `""` | Explicit CLI location. Auto-detected when empty. |
| `tuiLivePreview.className` | `""` | Override `--class`. Read from `x:Class` when empty. |
| `tuiLivePreview.width` | `80` | Render width. Ignored in interactive mode. |
| `tuiLivePreview.height` | `24` | Render height. Ignored in interactive mode. |
| `tuiLivePreview.ansi` | `false` | Emit ANSI colour in rendered output. |
| `tuiLivePreview.live` | `true` | Preview unsaved changes, not just the last save. |
| `tuiLivePreview.confirmation` | `false` | Ask before starting. |

## Unsaved changes are previewed live

You do not have to save. The editor's buffer is mirrored to a scratch copy of the file, and
the preview watches **that**, so an edit appears as you type it.

This works by rewriting the same scratch file rather than a new one each time, because the
preview notices changes by polling that one path — a fresh temp file per keystroke would leave
a running preview watching a file that no longer exists. Rewriting it needs no restart and no
round trip: the interactive host picks the change up on its next poll.

The code-behind is passed explicitly, because the pipeline normally finds it by looking for a
sibling `.cs` and a scratch file has no siblings. Without that argument every `{Bind ...}`
would fail to compile with `CS0103: The name 'ViewModel' does not exist`.

Turn it off with `"tuiLivePreview.live": false` when a preview has to be reproducible from
what is on disk — a CI render, a screenshot, or a frame you are attaching to a bug report.
The log says so explicitly when it skips an unsaved buffer, rather than quietly previewing
stale content.

## Troubleshooting

### Menu entries do not appear

The context menus are gated on `resourceFilename =~ /\.tui\.xaml$/`. In a `when` clause
that is a **regex**, so the dots must be escaped. Escaping is easy to get wrong because
the manifest is JSON *and* the clause is a regex: a double-escaped `\\.` means "a literal
backslash" and therefore matches nothing, so the menus silently never appear while every
command still works fine from the palette.

`npm test` asserts this directly (match, non-match, and exact backslash count), because
nothing else in the suite can catch it — VS Code is the only thing that evaluates a `when`
clause. If you edit the menus, run the tests.

Also check: the commands are only shown for files whose name **ends** in `.tui.xaml`, so
`MainWindow.xaml` and `MainWindow.tui.xaml.bak` intentionally show nothing.

### Nothing happens when a command runs

Every step is logged to the **TUI Preview** output channel. Open it with
**TUI Preview: Show Log** (or the `$(beaker) TUI …` status bar item, or **View → Output**).
Each entry records which file was targeted, which class name was resolved, which CLI was
found, and the exact command line that ran:

```
[10:22:31] extension activated
[10:22:41] --- tuiLivePreview.interactive invoked ---
[10:22:41] target from context menu: F:\...\tests\MainWindow.tui.xaml
[10:22:41] resolved: xaml=... class=CoreBridge.Tui.MainWindow launcher=...\cli.exe
[10:22:41] created terminal "TUI Preview (interactive)" (env {"TUI_ANSI":"0"})
[10:22:41] running: "...cli.exe" --xaml "..." --class "..." --interactive
```

If a preview does not start, the last log line says why. The usual causes:

| Log / message | Meaning |
|---|---|
| No `--- ... invoked ---` line | The extension did not activate. Reload the window. |
| `Could not find the preview CLI` | The workspace root has no `cli/`. Open the repo root, or set `tuiLivePreview.cliPath`. |
| `No .tui.xaml file could be determined` | No context-menu target, no open editor, no match in the workspace. |
| `tuiLivePreview.cliPath does not exist` | The configured path is wrong. |
| Command runs but the terminal looks empty | The CLI printed diagnostics; check for `error:` above the frame. |

## Which files get the menus

The commands appear on files ending in **`.tui.cs`** or **`.tui.xaml`**:

| File | Previewed as | Class name comes from |
|---|---|---|
| `Counter.tui.cs` | `--view` (plain C#, no generator) | `namespace` + the class declaration |
| `Main.tui.xaml` | `--xaml` (through the source generator) | the `x:Class` attribute |

A bare `.cs` is deliberately excluded: it would put every source file in the workspace in
scope, so the menus would appear on files that are not views.

**A `.tui.cs` next to a `.tui.xaml` of the same stem is that file's code-behind, not a view.**
Both names are the documented convention for different things, so the sibling is what tells
them apart. Right-clicking a code-behind reports which `.tui.xaml` to use instead of failing
obscurely, because a code-behind is a `partial` class whose controls only exist in the
generated half and can never be instantiated alone.

A `partial` class also cannot be previewed, so the class name is not inferred for one — the
extension asks instead. Guessing would give a confusing runtime failure rather than an honest
question.

## Development

```powershell
# build, test, package and install in one step
..\install-extension.ps1

# or iterate faster with a watch/reload dev host (F5 equivalent)
..\install-extension.ps1 -Dev
```

**You never need to uninstall first.** `code --install-extension` on a VSIX is an in-place
update — it is documented as "installs or updates" and simply replaces the files under
`%USERPROFILE%\.vscode\extensions`. Re-running the script over an existing install is normal
and expected.

The one manual step that remains is **Developer: Reload Window**, and it matters: the
installed extension runs its own copy of `out/extension.js`, so a reload is what makes the new
code live. A stale host runs old JavaScript while showing the new menus from `package.json`,
which is indistinguishable from a fix that did not work. The script prints the build id it
deployed, and the status bar shows the same id — if those differ, you are looking at a stale
host.

The script also stops any running `cli` process first, because a live preview holds
`cli.exe` open and the build then fails with `MSB3021`.

### Running the tests directly

```powershell
npm install
npm run compile     # tsc -> out/
npm test            # compile, then run the extension against a stubbed vscode host
```

`npm test` loads the real compiled `out/extension.js` with a stubbed `vscode` module and
drives the real command handlers, asserting on the command lines they produce. It covers
`x:Class` parsing (including single quotes and the missing-attribute prompt), CLI discovery
and its `dotnet run` fallback, path quoting for paths containing spaces, terminal reuse,
shell-aware command lines, unsaved-buffer mirroring, the error paths, and the context-menu
`when` clauses.

### Debugging

The CLI has its own developer seams, which the extension exposes:

- `TUI_DUMP=1` (set by **Show Generated C#**) prints the generated C#.
- `TUI_DUMP_STUB=1` prints the synthesised code-behind stub when no real code-behind exists.
