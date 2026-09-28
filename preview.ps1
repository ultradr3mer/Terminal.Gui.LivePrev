<#
.SYNOPSIS
    Live preview for a .tui.xaml file: edit in one pane, see the result in this one.

.DESCRIPTION
    Renders the XAML to characters in this terminal, then re-renders on every save.
    Set -Watch:$false for a single render (useful in CI or for a quick check).

.PARAMETER Xaml
    The .tui.xaml file to preview. Defaults to tests\MainWindow.tui.xaml.

.PARAMETER Class
    Fully qualified class name, i.e. the x:Class attribute in the XAML.

.PARAMETER Watch
    Re-render on every save. On by default.

.PARAMETER Interactive
    Live TUI preview: log pane on top, the real view below. Ctrl+C quits.

.EXAMPLE
    .\preview.ps1

.EXAMPLE
    .\preview.ps1 -Interactive

.EXAMPLE
    .\preview.ps1 -Xaml tests\MainWindow.tui.xaml -Class CoreBridge.Tui.MainWindow -Watch:$false
#>
[CmdletBinding()]
param(
    [string] $Xaml = 'tests\MainWindow.tui.xaml',
    [string] $Class = 'CoreBridge.Tui.MainWindow',
    [bool]   $Watch = $true,
    [switch] $Interactive,
    [int]    $Width = 80,
    [int]    $Height = 24,

    # Preview a view written in plain C# instead of XAML.
    #
    # Mutually exclusive with -Xaml: the CLI rejects both --xaml and --view at once, so the two
    # have to be told apart here rather than by precedence. The default is empty, so omitting it
    # keeps the XAML behaviour above and nothing existing changes.
    [string] $View,

    # Class name for -View. Left empty, it is read out of the file's own namespace and class
    # declaration, which is what the VS Code extension does too.
    [string] $ViewClass
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$project  = Join-Path $root 'cli\cli.csproj'

# A .tui.cs code-behind belongs to a .tui.xaml beside it and cannot be instantiated on its own:
# it is a partial class whose controls come from the generated half. Resolving to the sibling
# here mirrors what the extension does, so both front ends behave the same way.
$useView = -not [string]::IsNullOrWhiteSpace($View)
if ($useView -and $View.EndsWith('.tui.cs') -and -not [string]::IsNullOrWhiteSpace($View)) {
    $xamlSibling = $View -replace '\.tui\.cs$', '.tui.xaml'
    $resolved = if ([System.IO.Path]::IsPathRooted($View)) { $View } else { Join-Path $root $View }
    $resolvedSibling = if ([System.IO.Path]::IsPathRooted($xamlSibling)) { $xamlSibling } else { Join-Path $root $xamlSibling }
    if (Test-Path $resolvedSibling) {
        $View = $resolvedSibling
        $useView = $false
        Write-Host "Note: that .tui.cs is a code-behind; previewing $xamlSibling instead." -ForegroundColor DarkGray
    }
}

$source = if ($useView) { $View } else { $Xaml }
$sourcePath = Join-Path $root $source
$sourceFlag = if ($useView) { '--view' } else { '--xaml' }

if (-not (Test-Path $sourcePath)) {
    Write-Error "Source not found: $sourcePath"
    exit 1
}

# The project the view belongs to, which the CLI compiles it against: the nearest directory
# above the file holding exactly one .csproj, as the extension finds it. $project stays the
# tool itself, which is what `dotnet run` starts.
$viewProject = $null
$dir = Split-Path (Resolve-Path $sourcePath) -Parent
while ($dir) {
    $found = @(Get-ChildItem -Path $dir -Filter *.csproj -File -ErrorAction SilentlyContinue)
    if ($found.Count -eq 1) { $viewProject = $found[0].FullName; break }
    $dir = Split-Path $dir -Parent
}
if (-not $viewProject) { $viewProject = $project }

if ($useView -and [string]::IsNullOrWhiteSpace($ViewClass)) {
    # Same inference the extension performs: namespace plus the first class declaration.
    $text = Get-Content $sourcePath -Raw
    $ns = if ($text -match '(?m)^\s*namespace\s+([A-Za-z_][\w.]*)\s*[;{]') { $Matches[1] } else { $null }
    $cls = if ($text -match '(?m)^\s*(?:(?:public|internal|sealed|abstract|partial|static|unsafe|new)\s+)*class\s+([A-Za-z_]\w*)') { $Matches[1] } else { $null }
    if ($cls) {
        $ViewClass = if ($ns) { "$ns.$cls" } else { $cls }
        Write-Host "Inferred class: $ViewClass" -ForegroundColor DarkGray
    } else {
        Write-Error "Could not determine the class name in $source. Pass -ViewClass."
        exit 1
    }
}

$className = if ($useView) { $ViewClass } else { $Class }

$argList = @(
    $sourceFlag, $sourcePath,
    '--project', $viewProject,
    '--class', $className,
    '--render',
    '--width', $Width,
    '--height', $Height
)

if ($Interactive) {
    # The interactive app fills the terminal itself, so width/height/watch are not used.
    $argList = @(
        $sourceFlag, $sourcePath,
        '--project', $viewProject,
        '--class', $className,
        '--interactive'
    )

    Write-Host "Interactive preview of $source as $className. Ctrl+C to quit." -ForegroundColor Cyan
} else {
    if ($Watch) { $argList += '--watch' }
    Write-Host "Previewing $source as $className. Ctrl+C to stop." -ForegroundColor Cyan
}

& dotnet run --project $project --configuration Debug -- @argList
exit $LASTEXITCODE
