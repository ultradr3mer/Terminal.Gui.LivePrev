<#
.SYNOPSIS
    Builds the tuipreview tool, then tests, packages and installs the VS Code extension.

.DESCRIPTION
    The extension runs the tuipreview tool on a .tui.cs view. The tool is published into
    vscode-extension\tool and ships inside the VSIX, so the extension works anywhere with no
    setup. Inside this repo a build in cli\bin takes precedence, so changes to the tool show up
    in previews right away. The view's own app is never built.

    Installing a VSIX over an already-installed copy is an in-place update:
    `code --install-extension` installs or updates, so there is never a reason to uninstall
    first.

    The installed extension runs out of its own copy of out/extension.js, so editing src/ has
    no effect until this script runs again.

.PARAMETER Dev
    Open an Extension Development Host with the repo as the workspace instead of installing
    the packaged extension.

.PARAMETER SkipTests
    Skip the extension test suite.

.PARAMETER SkipBuild
    Skip building the solution and publishing the bundled tool. An existing
    vscode-extension\tool is packaged as it is.

.PARAMETER Pack
    Also pack the tuipreview tool and TuiControls into a local NuGet feed. The tool installs
    from there with `dotnet tool install -g TuiPreview.Cli --add-source <feed>`, and an app
    uses the controls with <PackageReference Include="TuiControls" />. See -Feed.

.PARAMETER Feed
    Where -Pack puts the packages. Defaults to .\nupkg.

.EXAMPLE
    .\install-ext.ps1

.EXAMPLE
    .\install-ext.ps1 -Pack -Feed C:\nuget-local
#>
[CmdletBinding()]
param(
    [switch] $Dev,
    [switch] $SkipTests,
    [switch] $SkipBuild,
    [switch] $Pack,
    [string] $Feed
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$extDir = Join-Path $root 'vscode-extension'
$solution = Join-Path $root 'cli.slnx'
$cliProj = Join-Path $root 'cli\TuiPreview.Cli.csproj'
$toolDir = Join-Path $extDir 'tool'
$packProjects = @(
    (Join-Path $root 'cli\TuiPreview.Cli.csproj'),
    (Join-Path $root 'controls\TuiControls\TuiControls.csproj')
)
if (-not $Feed) { $Feed = Join-Path $root 'nupkg' }

function Write-Step([string] $message) {
    Write-Host "`n==> $message" -ForegroundColor Cyan
}

function Fail([string] $message) {
    Write-Host "FAILED: $message" -ForegroundColor Red
    exit 1
}

# --- 1. the tool, TuiControls and the sample app ---
if ($SkipBuild) {
    Write-Step 'skipping the build (-SkipBuild)'
} else {
    Write-Step 'building cli.slnx'
    & dotnet build $solution --nologo -v quiet
    if ($LASTEXITCODE -ne 0) { Fail 'dotnet build cli.slnx failed' }

    # Emptied first, so a file the tool no longer ships cannot linger in the VSIX.
    Write-Step "publishing the tool into $toolDir"
    if (Test-Path $toolDir) { Remove-Item $toolDir -Recurse -Force }
    & dotnet publish $cliProj -c Release --nologo -v quiet -o $toolDir
    if ($LASTEXITCODE -ne 0) { Fail 'dotnet publish TuiPreview.Cli failed' }
}

if (-not (Test-Path (Join-Path $toolDir 'tuipreview.dll'))) {
    Fail "$toolDir has no tuipreview.dll; run once without -SkipBuild"
}

if ($Pack) {
    Write-Step "packing TuiPreview.Cli and TuiControls into $Feed"
    foreach ($proj in $packProjects) {
        & dotnet pack $proj --nologo -v quiet -o $Feed
        if ($LASTEXITCODE -ne 0) { Fail "dotnet pack $(Split-Path $proj -Leaf) failed" }
    }

    # NuGet caches a package by id and version, so a repack of the same version is ignored
    # until the cached copy is removed.
    foreach ($id in 'tuipreview.cli', 'tuicontrols') {
        $cached = Join-Path $env:USERPROFILE ".nuget\packages\$id"
        if (Test-Path $cached) {
            Write-Host "    note: $cached exists; bump <Version> in the csproj, or delete that folder, to pick up this build" -ForegroundColor DarkGray
        }
    }
    Write-Host "    install the tool with: dotnet tool update -g TuiPreview.Cli --add-source `"$Feed`"" -ForegroundColor DarkGray
    # --add-source is refused when the NuGet config uses package source mapping.
    Write-Host "    (with package source mapping on, map TuiPreview.Cli to that feed in NuGet.config, or pass --configfile)" -ForegroundColor DarkGray
}

# --- 2. dependencies, so a fresh clone works without a separate npm install ---
if (-not (Test-Path (Join-Path $extDir 'node_modules'))) {
    Write-Step 'installing npm dependencies (first run only)'
    Push-Location $extDir
    try {
        & npm install --no-audit --no-fund
        if ($LASTEXITCODE -ne 0) { Fail 'npm install failed' }
    } finally { Pop-Location }
}

# --- 3. the tests, before packaging rather than after ---
if ($SkipTests) {
    Write-Step 'skipping tests (-SkipTests)'
} else {
    Write-Step 'running the extension test suite'
    Push-Location $extDir
    try {
        & npm test
        if ($LASTEXITCODE -ne 0) { Fail 'tests failed; not packaging a known-broken extension' }
    } finally { Pop-Location }
}

if ($Dev) {
    # --- 4a. dev host: no packaging ---
    Write-Step 'launching the Extension Development Host'
    & code --extensionDevelopmentPath="$extDir" --new-window "$root"
    if ($LASTEXITCODE -ne 0) { Fail 'could not launch the development host' }
    Write-Host "`nDone. Right-click samples\SampleApp\MainView.tui.cs in the new window." -ForegroundColor Green
    exit 0
}

# --- 4b. package and install ---
Write-Step 'packaging the VSIX'
Push-Location $extDir
try {
    # --allow-missing-repository: package.json has no `repository` field, which vsce treats
    # as an error. --no-git-tag-version: there are no release tags.
    & npx vsce package --no-git-tag-version --allow-missing-repository
    if ($LASTEXITCODE -ne 0) { Fail 'vsce package failed' }

    $vsix = Get-ChildItem *.vsix | Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if (-not $vsix) { Fail 'no .vsix was produced' }

    Write-Step "installing $($vsix.Name)"
    & code --install-extension $vsix.FullName --force
    if ($LASTEXITCODE -ne 0) { Fail 'code --install-extension failed' }

    $id = 'clara.tui-live-preview'
    if (-not (& code --list-extensions | Select-String -SimpleMatch $id)) {
        Fail "$id is not in the extension list after installing"
    }

    # Prove the file that will actually run is the one just built. A silently stale install
    # looks exactly like a fix that did not work.
    $deployed = Get-ChildItem (Join-Path $env:USERPROFILE ".vscode\extensions\$id-*") -Directory |
        Sort-Object LastWriteTime -Descending |
        Select-Object -First 1
    $deployedJs = Join-Path $deployed.FullName 'out\extension.js'

    $buildId = (Select-String -Path (Join-Path $extDir 'src\extension.ts') -Pattern 'const BUILD = "([^"]+)"').
        Matches[0].Groups[1].Value
    $deployedId = (Select-String -Path $deployedJs -Pattern 'BUILD = "([^"]+)"').Matches[0].Groups[1].Value

    Write-Host ''
    Write-Host 'Installed:' -ForegroundColor Green
    Write-Host "  vsix      $($vsix.FullName)"
    Write-Host "  deployed  $deployedJs"

    if ($deployedId -ne $buildId) {
        Write-Host "  WARNING   deployed build $deployedId is not the one just compiled ($buildId)" -ForegroundColor Yellow
    } else {
        Write-Host "  build id  $deployedId (matches src)" -ForegroundColor Green
    }
} finally { Pop-Location }

Write-Host ''
Write-Host 'Now run this in VS Code:' -ForegroundColor Green
Write-Host '  Developer: Reload Window' -ForegroundColor Yellow
Write-Host ''
Write-Host 'Then check the status bar shows that build id, and right-click a .tui.cs view' -ForegroundColor DarkGray
Write-Host '(for example samples\SampleApp\MainView.tui.cs).' -ForegroundColor DarkGray
