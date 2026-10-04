# Terminal.Gui Live Preview — Visual Studio extension

The Visual Studio counterpart of [the VS Code extension](../vscode-extension/README.md). Right-click
any `*.tui.cs` view and preview it as a real Terminal.Gui view, without building the app it
belongs to. Both extensions run the same `tuipreview` tool, read the same per-view settings and
offer the same commands.

Requires Visual Studio 2022 17.14 or later, or Visual Studio 2026, and the .NET 10 runtime for the
bundled tool. It is a [VisualStudio.Extensibility](https://learn.microsoft.com/visualstudio/extensibility/visualstudio.extensibility/)
extension, so it runs out of process and cannot slow down or crash Visual Studio itself.

## Commands

On the context menu of a `*.tui.cs` file, in Solution Explorer and in the code editor, and under
**Extensions → TUI Live Preview**:

| Command | What it does |
|---|---|
| **TUI Preview: Interactive (live view)** | Full Terminal.Gui app in a console window: log pane on top, the real view below. |
| **TUI Preview: Render Once** | Prints one frame as characters, and waits for a key. |
| **TUI Preview: Watch (re-render on change)** | Re-renders whenever the view changes. |
| **TUI Preview: Init (create preview settings)** | Writes the view's class and size to a `.tui.json` sidecar or a `// @tuipreview` line. |
| **Stop All Previews** | Closes every preview window this extension opened. *(Extensions menu only.)* |
| **Show Log** | Opens a copy of the log. *(Extensions menu only.)* |

The menu entries only appear on `.tui.cs` files: a plain `.cs` is the logic half of a view, which
the preview must not compile.

## Differences from VS Code

The behaviour is the same; what Visual Studio offers around it is not.

| | VS Code | Visual Studio |
|---|---|---|
| Where a preview runs | An integrated terminal | A console window of its own (or a Windows Terminal tab, if that is your default terminal). Visual Studio has no API for its own terminal. |
| A second preview of the same view | Typed into the same terminal | Replaces the first: the old window is closed |
| Settings | `tuiLivePreview.*` in settings.json | **Tools → Options → TUI Live Preview**, same names |
| Warnings and errors | Toasts | A message box, shown after the preview has started |
| The log | Output channel, status bar item | **View → Output → TUI Preview**, or **Show Log** |
| Finding the tool in this repo | `cli/` in a workspace folder | `cli/` in any folder above the view |
| Watch | Uses the view's own size, or 80×24 | Also uses the **Render width/height** settings |

## Settings a view carries with it

Exactly as in VS Code: a `// @tuipreview class=... width=... height=... extra=... reference=... clipath=...`
annotation in the view, or a `MyView.tui.json` sidecar next to it. The annotation wins, and an
unsaved annotation in the editor is the one that applies. This extension compiles in the tool's
own parser (`cli/ViewSettings.cs`), so the two cannot disagree about the grammar. See the
[VS Code README](../vscode-extension/README.md#settings-a-view-carries-with-it) for the details.

## Settings

**Tools → Options → TUI Live Preview**:

| Setting | Default | Purpose |
|---|---|---|
| Tool path | empty | Explicit tool location: a `tuipreview.exe`, a `tuipreview.dll`, or a `.csproj`. |
| Class name | empty | Override the class to preview. Read from the file when empty. |
| Render width | `80` | Ignored in interactive mode. A view's own `width` wins. |
| Render height | `24` | Ignored in interactive mode. A view's own `height` wins. |
| ANSI colour | off | Emit ANSI colour in rendered output (`TUI_ANSI=1`). |
| Preview unsaved changes | on | Preview the editor buffer, not just the last save. |
| Ask before starting | off | Confirm before starting a preview. |

## Finding the tool

1. The view's own `clipath`, then the **Tool path** setting.
2. A build at `cli/bin/<config>/net*/tuipreview.exe` in a folder above the view, then
   `dotnet run --project cli/TuiPreview.Cli.csproj`. Only this repo has those, and they win so
   that changes to the tool show up in previews right away.
3. The copy bundled with the extension (`tool/tuipreview.dll`, run with `dotnet`).
4. `tuipreview` on PATH, from `dotnet tool install -g TuiPreview.Cli`.

## Unsaved changes are previewed live

When the view is open in the editor, its buffer is mirrored to a scratch file under
`%TEMP%\tui-live-preview`, and the preview runs on that. Edits are mirrored again as you type
(debounced by 250 ms), and the interactive preview and watch pick them up without a restart.
Closing the editor without saving puts the scratch file back to what is on disk.

## Troubleshooting

Every step is logged to **View → Output → TUI Preview** and to `%TEMP%\tui-live-preview\extension.log`:
which file was targeted, which class and tool were resolved, and the exact command line that ran.
The first line names the build, which `PreviewService.Build` sets; bump it with every change, so a
stale install is recognisable.

Each preview runs from a script, `%TEMP%\tui-live-preview\launch-<view>-<mode>.cmd`. Running that
by hand reproduces exactly what the extension did.

## Development

```powershell
# test, package and install (Visual Studio asks to be closed, or waits for it)
..\install-vs-ext.ps1

# into the experimental instance instead, then: devenv /rootSuffix Exp
..\install-vs-ext.ps1 -Exp

# just the tests
dotnet test tests
```

To debug, open `TuiLivePreview.VisualStudio.slnx` in Visual Studio with the "Visual Studio
extension development" workload and press F5, which starts the experimental instance with the
extension loaded.

The tests cover everything that does not need Visual Studio (`Core/`): tool discovery, command
lines and their quoting, the launch script, settings precedence, init, and the scratch copies. One
test runs a generated launch script against the real tool from a folder named `tui A&B 100%`,
because spaces, `&` and `%` are what break batch files. The parts that talk to Visual Studio
(`PreviewService`, the commands, `LiveMirror`) are thin and are checked by hand.
