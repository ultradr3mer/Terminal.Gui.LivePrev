# CLAUDE.md

Live preview for Terminal.Gui views, similar to the XAML designer preview: right-click a view file in VS Code and see it rendered as a real Terminal.Gui view.

This repo was split out of a larger project. Some leftovers from that project are still being removed (see "Cleanup in progress").

## The model

The preview follows TerminalGuiDesigner (`src/FromCode/CodeToView.cs`; a local clone is at `F:\source\TerminalGuiDesigner`). A view is split in two, like a `.Designer.cs` and its code-behind:
- **`X.tui.cs`** is the layout half: `partial class X`, controls as fields, and an `InitializeComponent()` that composes them. It may use only the framework, Terminal.Gui and TuiControls.
- **`X.cs`** is the logic half: the real constructor calls `InitializeComponent()` and then wires everything up. It may use anything the app has. The preview never compiles it.

The preview compiles only the `.tui.cs`, adding a stub `public X() { InitializeComponent(); }` when the class has an `InitializeComponent()` and no parameterless constructor of its own. Because a view needs nothing from its app, the preview is a standalone tool: the app gets no package reference, and nothing is built before previewing.

## The parts

1. **`cli/` – the `tuipreview` tool** (`TuiPreview.Cli.csproj`, net10.0, Terminal.Gui `[2.5.0,2.6)`, Roslyn), packed as a dotnet tool.
   - `Options.cs` defines the command-line contract (`--view`, `--class`, `--interactive`, `--watch`, `--extra`, `--display-name`, `--width/--height`, hidden test flags `--ui-snapshot`/`--ui-flood`).
   - `Preview/RenderPipeline.cs` compiles in memory and renders. `DefaultReferences()` is the reference set: the shared framework, Terminal.Gui and TuiControls. It is meant to become configurable, and that method is the place to append to. Anything else in a `.tui.cs` fails with CS0246, which is intended.
   - `Views/` contains the preview chrome (log pane plus splitter). `PreviewChrome.tui.cs`/`PreviewChrome.cs` follow the same split and can be previewed themselves.
2. **`controls/TuiControls/`**: Terminal.Gui controls that views may use (the `Splitter` so far). It is referenced by the tool and packed on its own, so it can grow into a control library.
3. **`vscode-extension/`**: adds "TUI Preview" commands (Interactive, Render Once, Watch) to the explorer and editor context menus for `*.tui.cs` files. It runs the tool on the view and mirrors unsaved buffers to a scratch file so previews stay live. Almost all of the logic is in `src/extension.ts`.
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
