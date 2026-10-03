# Terminal.Gui.LivePrev

Live preview for Terminal.Gui views in VS Code: right-click a `*.tui.cs` view and see it
rendered as a real Terminal.Gui view, much like the XAML designer preview. See
[vscode-extension/README.md](vscode-extension/README.md) for how to use it.

## Installing the VS Code extension

You need:

- the [.NET 10 SDK](https://dotnet.microsoft.com/download)
- [Node.js](https://nodejs.org) 22 or later
- VS Code, with its `code` command on PATH (on Windows the installer adds it; on macOS run
  **Shell Command: Install 'code' command in PATH** from the command palette)

Then, from the repository root in PowerShell:

```powershell
.\install-ext.ps1
```

This builds the `tuipreview` tool, bundles it into the extension, runs the extension tests,
packages a `.vsix` and installs it into VS Code. Afterwards run **Developer: Reload Window** in
VS Code; the status bar then shows `TUI` and a build id that matches the one the script printed.

Running the script again over an installed copy updates it in place, so there is never a need
to uninstall first.

Useful switches:

| Switch | Effect |
|---|---|
| `-Dev` | Open an Extension Development Host instead of installing. |
| `-SkipTests` | Skip the extension tests. |
| `-SkipBuild` | Package the already-published tool as it is. |
| `-CodeProfile <name>` | Install into a specific VS Code profile. |
| `-Pack` | Also pack the tool and TuiControls as NuGet packages into `.\nupkg`. |

### Installing the `.vsix` by hand

The script leaves the package in `vscode-extension\`. To install it on another machine, copy the
`.vsix` over and run:

```powershell
code --install-extension tui-live-preview-0.1.0.vsix
```

or use **Extensions: Install from VSIX...** in VS Code. The other machine needs only the .NET 10
runtime, because the tool ships inside the package.
