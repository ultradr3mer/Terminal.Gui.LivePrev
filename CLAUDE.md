# CLAUDE.md

Live preview for Terminal.Gui views, similar to the XAML designer preview: right-click a view file in VS Code and see it rendered as a real Terminal.Gui view.

This repo was split out of a larger project. Some leftovers from that project are still being removed (see "Cleanup in progress").

## The model

The preview follows TerminalGuiDesigner (`src/FromCode/CodeToView.cs`; a local clone is at `F:\source\TerminalGuiDesigner`). A view is split in two, like a `.Designer.cs` and its code-behind:
- **`X.tui.cs`** is the layout half: `partial class X`, controls as fields, and an `InitializeComponent()` that composes them. It may use only the framework, Terminal.Gui and TuiControls.
- **`X.cs`** is the logic half: the real constructor calls `InitializeComponent()` and then wires everything up. It may use anything the app has. The preview never compiles it.

The preview compiles only the `.tui.cs`, adding a stub `public X() { InitializeComponent(); }` when the class has an `InitializeComponent()` and no parameterless constructor of its own. Because a view needs nothing from its app, the preview is a standalone tool: the app gets no package reference, and nothing is built before previewing.

## Settings a view carries

Per-view settings live with the view, not in the editor settings, because a view is the only thing that knows its own class name and its own helpers. Two sources, annotation first:

- **`// @tuipreview key=value ...`** in the `.tui.cs` — `class`, `width`, `height`, `extra`, `reference`, `clipath`. Repeatable, several keys per line, values quotable.
- **`MyView.tui.json`**, named after the view. Same keys as JSON, tolerating `//` comments and trailing commas.

`extra`, `reference` and `clipath` paths are relative to the view. A bad value or an unknown key is logged and ignored, never fatal.

`reference` is **additive**: `DefaultReferences()` always yields the framework, Terminal.Gui and TuiControls first, then the requested assemblies, so a view written against a third-party control library keeps all of them. A directory is expanded to the `*.dll` inside it. Those assemblies are also hooked into the preview's `AssemblyLoadContext` via `Resolving` — referenced by the compiler is not the same as loadable at runtime, and without the hook the view compiles cleanly and then throws `FileNotFoundException` on the first line touching a type from it.

`clipath` is read only by the extension: the tool cannot act on it, since it *is* the tool. It outranks the `tuiLivePreview.cliPath` setting, which is the folder-wide default.

When the annotation overrides a single-valued key (`class`, `width`, `height`, `clipath`) that the sidecar states differently, both sides warn: the tool on stderr and in the interactive log pane, the extension in a toast. Lists add up and never warn.

The annotation outranks the sidecar because it is what the author is looking at, and because a sidecar cannot describe an unsaved buffer: the extension reads settings from the open document's text, so an annotation typed but not yet saved is the one that applies.

Full chain for `--class`: explicit flag → annotation → sidecar → `tuiLivePreview.className` → inferred from the file → prompt. `--width`/`--height` are `int?` rather than defaulting to 80/24 precisely so "unset" is distinguishable and a view's own size can win.

Both sides implement this, deliberately: `cli/ViewSettings.cs` (with `InferClassName`) and `readViewSettings()` in `src/extension.ts`. The extension re-reads them so it can pass explicit flags and name a bad `extra` file itself, in a message that can say which setting asked for it.

## The parts

1. **`cli/` – the `tuipreview` tool** (`TuiPreview.Cli.csproj`, net10.0, Terminal.Gui `[2.5.0,2.6)`, Roslyn), packed as a dotnet tool.
   - `Options.cs` defines the command-line contract (`--view`, `--class`, `--interactive`, `--watch`, `--extra`, `--reference`, `--display-name`, `--width/--height`, hidden test flags `--ui-snapshot`/`--ui-flood`). `--class` is optional: the view may state it, and the tool infers it as a last resort.
   - `Preview/RenderPipeline.cs` compiles in memory and renders. `DefaultReferences()` is the reference set: the shared framework, Terminal.Gui and TuiControls, plus anything `--reference` adds. Anything else in a `.tui.cs` fails with CS0246, which is intended.
   - `ResolvedPreview.cs` is the resolved value handed from option parsing to the render, so the two halves cannot disagree about which class, files or references are in play.
   - `Views/` contains the preview chrome (log pane plus splitter). `PreviewChrome.tui.cs`/`PreviewChrome.cs` follow the same split and can be previewed themselves.
2. **`controls/TuiControls/`**: Terminal.Gui controls that views may use (the `Splitter` so far). It is referenced by the tool and packed on its own, so it can grow into a control library.
3. **`vscode-extension/`**: adds "TUI Preview" commands (Interactive, Render Once, Watch, Init) to the explorer and editor context menus for `*.tui.cs` files. Init detects the view's class and size and writes them, after a modal asking where, to a new `.tui.json` sidecar or a `// @tuipreview` line at the top of the view. It writes only keys the view does not already state, and never overwrites. It runs the tool on the view and mirrors unsaved buffers to a scratch file so previews stay live. Almost all of the logic is in `src/extension.ts`.
   - The tool ships inside the VSIX: `install-ext.ps1` publishes it into `vscode-extension/tool/` (gitignored) before packaging, and the extension runs it with `dotnet tool/tuipreview.dll`.
   - The lookup order is `cliPath` setting → `cli/bin` build → `dotnet run` on `cli/` → bundled copy → `tuipreview` on PATH. A workspace copy of the tool wins, so this repo previews with its own changes.

## Layout

- `samples/SampleApp/` is a reference app. `MainView.tui.cs` is the layout half and `MainView.cs` the logic half.
- `tests/` holds sample views to preview manually. The build does not reference them.
- `reference/` holds UI reference snapshots for the preview chrome.
- `cli.slnx` is the solution (tool, controls and sample).

## Commands

```powershell
dotnet build cli.slnx
dotnet run --project cli/TuiPreview.Cli.csproj -- --view samples\SampleApp\MainView.tui.cs --class SampleApp.MainView
.\install-ext.ps1            # build, npm test, package VSIX, install into VS Code
.\install-ext.ps1 -Dev       # open an Extension Development Host instead
.\install-ext.ps1 -Pack      # also pack TuiPreview.Cli (the tool) and TuiControls into .\nupkg
cd vscode-extension; npm test     # tsc + node test/extension.test.js (stubbed vscode API)
```

- Bump `const BUILD` in `src/extension.ts` when you change the extension. The install script compares it with the deployed copy to catch stale installs.
- NuGet caches packages by version. If you repack without bumping `<Version>` in the csproj, the old package keeps being used.
- The user's NuGet config uses package source mapping and maps only nuget.org. A local feed (`F:\Packages` is registered as `Local`) is ignored unless its packages get a mapping entry, and `--add-source` is refused. The VSIX bundles the tool, so this only matters for `-Pack`.

## Cleanup in progress

The XAML authoring path (`.tui.xaml` plus source generator) has been removed. Leftovers still to remove:
- `tests/README.md`, `tests/MainWindow.tui.cs`, `tests/PrevWin.tui.cs`
- `GeneratorDiagnostics`/`HasNoGeneratedCode` in `RenderPipeline` results. Nothing produces generator diagnostics any more.

## Style

- Comments explain *why*, in full sentences. Match the existing density.
- Indentation: C# in `cli/` uses 2 spaces; `controls/`, `samples/` and the TypeScript use 4. Follow the file you are editing.
