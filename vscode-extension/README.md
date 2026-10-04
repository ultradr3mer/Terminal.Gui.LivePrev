# Terminal.Gui Live Preview — VS Code extension

Right-click any `*.tui.cs` view and preview it as a real Terminal.Gui view, without leaving
the editor and without building the app it belongs to.

## Commands

Available from the explorer context menu, the editor tab context menu, and the command
palette (**TUI Preview:**). They only appear on `*.tui.cs` files.

| Command | What it does |
|---|---|
| **Interactive (live view)** | Full Terminal.Gui app in a terminal: log pane on top, the real view below, so you can type in `TextField`s and press `Button`s. |
| **Render Once** | Prints one frame as characters. Fast; good for a quick check. |
| **Watch (re-render on save)** | Re-renders on every save of the view. |
| **Init (create preview settings)** | Gives the view its own settings: detects what it would preview with and writes it to a `.tui.json` sidecar or a `// @tuipreview` line at the top of the view. See below. |
| **Show Log** | Opens the **TUI Preview** output channel (see Troubleshooting). |
| **Stop All** | Kills every preview terminal this extension opened. |

The target file comes from the item you right-clicked. If you invoke from the command
palette instead, the active editor is used.

## What a view looks like

A view is split in two, like a designer file and its code-behind:

```csharp
// MainView.tui.cs - the layout half. Only Terminal.Gui and TuiControls.
public sealed partial class MainView : Window
{
    private readonly Label _summary = new() { X = 1, Y = 1, Text = "0 items" };

    private void InitializeComponent()
    {
        Title = "Sample App";
        Add(_summary);
    }
}

// MainView.cs - the logic half. Anything the app has; never compiled by the preview.
public sealed partial class MainView
{
    public MainView()
    {
        InitializeComponent();
        _summary.Text = $"{LoadItems().Count} items";
    }
}
```

The preview compiles only the `.tui.cs` together with a stub constructor that calls
`InitializeComponent()`, so what you see is the layout with its placeholder values. A
self-contained `.tui.cs` that declares its own parameterless constructor is compiled as it is.

## Settings a view carries with it

Everything in the **Settings** section below is one bag for the whole workspace. A view that
needs something different says so itself, in a `// @tuipreview` annotation:

```csharp
// @tuipreview class=SampleApp.MainView width=120 height=40 extra=Layout/Shared.tui.cs
public sealed partial class MainView : Window { ... }
```

| Key | Meaning |
|---|---|
| `class` | The fully qualified class to preview. |
| `width`, `height` | Render size in columns and rows. Ignored in interactive mode, which fills the terminal. |
| `extra` | Another `.cs` file to compile with the view — layout-only helpers. Repeat the key for several. |
| `reference` | An assembly to add to the preview's reference set, for a control library beyond Terminal.Gui and TuiControls. A `.dll`, or a directory of them. |
| `project` | A `.csproj` whose build output is added to the reference set, together with its NuGet packages: the preview's version of a `<ProjectReference>`. See below. |
| `cliPath` | Where to find the `tuipreview` tool for this view. Beats the `tuiLivePreview.cliPath` setting. |

Several keys fit on one line, and a value with spaces goes in quotes:

```csharp
// @tuipreview class=MyApp.Dialog width=100 extra="Layout/Helpers With Spaces.tui.cs"
```

`extra`, `reference`, `project` and `cliPath` paths are relative to the view, not to your terminal's working
directory.

### The sidecar: `MyView.tui.json`

For a view with more to say than one line of comment — several `extra` files, say — put a JSON
file next to it, named after it. `MainView.tui.cs` goes with `MainView.tui.json`:

```jsonc
{
  // Comments and trailing commas are fine.
  "class": "SampleApp.MainView",
  "width": 120,
  "extra": ["Layout/Shared.tui.cs", "Layout/Table.cs"],
}
```

Same keys as the annotation. **The annotation wins** where both state the same key, because it is
the one you are looking at — and because a sidecar cannot describe an unsaved buffer. With the
file open and edited but not saved, the annotation on screen is what applies.

Precedence, highest first: an explicit `--class` on the command line → the annotation → the
sidecar → the `tuiLivePreview.*` settings → what the file declares.

An unknown key or a bad value is reported in the log and ignored; it never stops a preview.
When the annotation overrides a value the sidecar states differently, the preview says so with a
warning — in VS Code, in the tool's output and in the interactive log pane — because otherwise
the sidecar loses without a word.

### Control libraries from your solution: `project`

A view that uses controls from another project in the solution names that project:

```csharp
// @tuipreview project=../../MyControls/MyControls.csproj
```

The tool asks MSBuild where the project builds to, without building it, and references its
output dll, the dlls next to it (the projects it references in turn) and the runtime assemblies
of its NuGet packages. **The project has to be built already**; if it is not, the preview says so
and names the `dotnet build` command to run. Build the library after changing it, as you would
before running the app: a running interactive or watch preview notices the new build and
refreshes, and the dll is never locked, so the build does not fail while a preview is open.

A multi-targeted project is evaluated for the newest framework the tool can load (.NET 10 or
older, else netstandard2.x).

### Starting one: **Init**

**TUI Preview: Init** detects what the view would preview with today — the class, and the width
and height — and asks where to write it down:

- **Sidecar** creates `MyView.tui.json`, with the optional keys (`extra`, `reference`, `project`, `clipath`)
  as commented-out examples.
- **Annotation in the view** adds a `// @tuipreview class=... width=... height=...` line at the
  top of the `.tui.cs`. It is an ordinary edit, so it can be undone.

Writing the values down changes nothing about how the view renders; it pins it. Init only writes
what the view does not already state, so choosing the annotation for a view with a sidecar adds
just the missing keys rather than shadowing the sidecar's. It never overwrites: an existing
sidecar or annotation is opened instead.

## The class name is inferred

If a view states nothing, the extension — and the tool — read the namespace and the class
declaration out of the file. You are only prompted if the file declares no class either.
Override with `tuiLivePreview.className`.

## Finding the tool

In order:

1. `tuiLivePreview.cliPath`, if set. Accepts a built `tuipreview.exe`, a `tuipreview.dll`
   (run with `dotnet`), or a `.csproj` (run with `dotnet run`).
2. A built binary at `cli/bin/Debug/net*/tuipreview.exe` in the workspace, then
   `dotnet run --project cli/TuiPreview.Cli.csproj`. These only exist in this repo, and they
   win so that changes to the tool show up in previews right away.
3. **The copy bundled with the extension** (`tool/tuipreview.dll`, run with `dotnet`). This
   is what everyone else gets: it needs no setup and always matches the extension version.
4. `tuipreview` on PATH, installed with `dotnet tool install -g TuiPreview.Cli`.

The only prerequisite is the .NET 10 runtime, which the bundled tool runs on.

## Terminals

Previews run in integrated terminals, not an output channel: interactive mode drives
Terminal.Gui's ANSI driver, reads keys and owns the screen, so it needs a real TTY.

One terminal is kept per view and reused, because watch and interactive modes run until
stopped, and a new terminal per right-click would leak a process each time. Re-running a
preview into the same terminal leaves the previous one running, so use **Stop All** (or the
terminal's trash icon) to clean up.

## Settings

| Setting | Default | Purpose |
|---|---|---|
| `tuiLivePreview.cliPath` | `""` | Explicit tool location. Auto-detected when empty. |
| `tuiLivePreview.className` | `""` | Override `--class`. Read from the file when empty. |
| `tuiLivePreview.width` | `80` | Render width. Ignored in interactive mode. |
| `tuiLivePreview.height` | `24` | Render height. Ignored in interactive mode. |
| `tuiLivePreview.ansi` | `false` | Emit ANSI colour in rendered output. |
| `tuiLivePreview.live` | `true` | Preview unsaved changes, not just the last save. |
| `tuiLivePreview.confirmation` | `false` | Ask before starting. |

## Unsaved changes are previewed live

You do not have to save. The editor's buffer is mirrored to a scratch copy of the file, and
the preview watches **that**, so an edit appears as you type it.

This works by rewriting the same scratch file rather than a new one each time, because the
preview notices changes by polling that one path. A fresh temp file per keystroke would leave
a running preview watching a file that no longer exists. Rewriting it needs no restart: the
interactive preview picks the change up on its next poll.

Turn it off with `"tuiLivePreview.live": false` when a preview has to be reproducible from
what is on disk (a CI render, a screenshot, or a frame you are attaching to a bug report).
The log says so explicitly when it skips an unsaved buffer, rather than quietly previewing
stale content.

## Troubleshooting

### Menu entries do not appear

The context menus are gated on `resourceFilename =~ /\.tui\.cs$/`. In a `when` clause that
is a **regex**, so the dots must be escaped. Escaping is easy to get wrong because the
manifest is JSON *and* the clause is a regex: a double-escaped `\\.` means "a literal
backslash" and therefore matches nothing, so the menus silently never appear while every
command still works fine from the palette.

`npm test` asserts this directly (match, non-match, and exact backslash count), because
nothing else in the suite can catch it. VS Code is the only thing that evaluates a `when`
clause. If you edit the menus, run the tests.

A bare `.cs` is deliberately excluded. It would put every source file in scope, and the
logic half of a view is exactly the file the preview must not compile.

### Nothing happens when a command runs

Every step is logged to the **TUI Preview** output channel. Open it with
**TUI Preview: Show Log** (or the `$(beaker) TUI …` status bar item, or **View → Output**).
Each entry records which file was targeted, which class name was resolved, which launcher
was found, and the exact command line that ran:

```
[10:22:31] extension activated
[10:22:41] --- tuiLivePreview.interactive invoked ---
[10:22:41] target from context menu: F:\...\samples\SampleApp\MainView.tui.cs
[10:22:41] resolved: view=... class=SampleApp.MainView launcher=...\tuipreview.exe
[10:22:41] created terminal "TUI Preview (interactive)" (env {"TUI_ANSI":"0"})
[10:22:41] running: & "...tuipreview.exe" --view "..." --class "..." --interactive
```

If a preview does not start, the last log line says why. The usual causes:

| Log / message | Meaning |
|---|---|
| No `--- ... invoked ---` line | The extension did not activate. Reload the window. |
| `tuipreview` is not recognised | The VSIX was packaged without its bundled tool. Rebuild it with `install-ext.ps1`, or set `tuiLivePreview.cliPath`. |
| `No .tui.cs file could be determined` | No context-menu target, no open editor, no match in the workspace. |
| `tuiLivePreview.cliPath does not exist` | The configured path is wrong. |
| `CS0246` in the preview log | The `.tui.cs` uses something beyond Terminal.Gui and TuiControls. Move that code to the `.cs` half. |

## Development

```powershell
# build, test, package and install in one step
..\install-ext.ps1

# or iterate faster with a dev host (F5 equivalent)
..\install-ext.ps1 -Dev
```

**You never need to uninstall first.** `code --install-extension` on a VSIX is an in-place
update: it is documented as "installs or updates" and simply replaces the files under
`%USERPROFILE%\.vscode\extensions`. Re-running the script over an existing install is normal.

The one manual step that remains is **Developer: Reload Window**, and it matters: the
installed extension runs its own copy of `out/extension.js`, so a reload is what makes the new
code live. A stale host runs old JavaScript while showing the new menus from `package.json`,
which is indistinguishable from a fix that did not work. The script prints the build id it
deployed, and the status bar shows the same id. If those differ, you are looking at a stale
host.

### Running the tests directly

```powershell
npm install
npm run compile     # tsc -> out/
npm test            # compile, then run the extension against a stubbed vscode host
```

`npm test` loads the real compiled `out/extension.js` with a stubbed `vscode` module and
drives the real command handlers, asserting on the command lines they produce. It covers
class-name inference (including partial layout halves and the prompt), a view's own settings
(annotations, sidecars and which wins), tool discovery and its fallbacks, path quoting for
paths containing spaces, terminal reuse, shell-aware command lines, unsaved-buffer mirroring,
the error paths, and the context-menu `when` clauses.
