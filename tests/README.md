# tests

Sample views to preview. Nothing here is referenced by the build.

| File | What it is |
|---|---|
| `Counter.tui.cs` | A view in plain C# — the preferred authoring path. Right-click it, or `.\preview.ps1 -View tests\Counter.tui.cs`. |
| `MainWindow.tui.xaml` + `.tui.cs` | A view in XAML plus its code-behind and `MainViewModel`. Still supported, but the generator behind it is the most experimental part of the tool. |
| `PrevWin.tui.xaml` + `.tui.cs` | A scratch file for reproducing layout problems. |
| `*.previewrc` | Per-file preview settings — currently the log pane height. Written by the preview host. |

For a view previewed inside its own app, see `samples/HostedApp/`.

## Where the preview chrome lives

**Not here.** The log pane and the splitter are split across
**`host/Preview/PreviewChrome.tui.cs`** (the visual component),
**`host/Preview/Splitter.cs`** (the split, the drag and the collapse) and
**`host/Preview/PreviewBuilder.cs`** (the runner).

It has to be: the host compiles every `.cs` under `host/`, so a file in `tests/` is never part of
`TuiPreview.Host.dll`. A copy of the chrome placed here would be dead code — and a dead copy of
the chrome is worse than none, because it compiles, renders, and looks authoritative while
diverging from the real thing. That happened once already: an XAML prototype of the shell sat in
`tests/` and was never wired to anything.

### Previewing the chrome itself

`PreviewChrome` is itself previewable, and that is how its layout is checked:

```powershell
cli\bin\Debug\net10.0\cli.exe --view host\Preview\PreviewChrome.tui.cs `
    --project host\TuiPreview.Host.csproj --class TuiPreview.Host.Preview.PreviewChrome
```

`--project` is what makes this work. The view is compiled against that project's references,
NuGet packages, implicit usings and `Nullable` setting, plus the project's own built
`TuiPreview.Host.dll`. So `Splitter` and `DiagnosticsLog` come from the last host build, and
internals are reachable through `InternalsVisibleTo("TuiPreview")` in `TuiPreview.Host.csproj`.

When you are editing a dependency at the same time, pass it as `--extra` to use the file
instead of the last build, for example `--extra host\Preview\Splitter.cs`. A type in source
takes precedence over the same type in `TuiPreview.Host.dll`.
