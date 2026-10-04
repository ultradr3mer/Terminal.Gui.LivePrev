<#
.SYNOPSIS
    Builds the tuipreview tool, then tests, packages and installs the Visual Studio extension.

.DESCRIPTION
    The Visual Studio counterpart of install-ext.ps1. The extension (vs-extension\) runs the
    tuipreview tool on a .tui.cs view. The tool is published into vs-extension\tool and ships
    inside the VSIX, so the extension works anywhere with no setup. Inside this repo a build in
    cli\bin takes precedence, so changes to the tool show up in previews right away.

    The extension is a VisualStudio.Extensibility extension: it runs out of process on .NET 10,
    which only Visual Studio 2026 hosts.

    Every build gets a fresh version number, because VSIXInstaller refuses to install a version
    that is already installed.

.PARAMETER Exp
    Install into the experimental instance (devenv /rootSuffix Exp) rather than the normal one,
    so a broken build cannot get in the way of real work.

.PARAMETER SkipTests
    Skip the extension test suite.

.PARAMETER SkipBuild
    Skip building the solution and publishing the bundled tool. An existing vs-extension\tool is
    packaged as it is.

.PARAMETER NoInstall
    Build and package only. The VSIX path is printed; double-click it to install.

    Needs the .NET 10 SDK and Visual Studio 2026.

.EXAMPLE
    .\install-vs-ext.ps1

.EXAMPLE
    .\install-vs-ext.ps1 -Exp
#>
[CmdletBinding()]
param(
    [switch] $Exp,
    [switch] $SkipTests,
    [switch] $SkipBuild,
    [switch] $NoInstall
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$extDir = Join-Path $root 'vs-extension'
$extProj = Join-Path $extDir 'TuiLivePreview.VisualStudio.csproj'
$testProj = Join-Path $extDir 'tests\TuiLivePreview.VisualStudio.Tests.csproj'
$solution = Join-Path $root 'cli.slnx'
$cliProj = Join-Path $root 'cli\TuiPreview.Cli.csproj'
$toolDir = Join-Path $extDir 'tool'

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

# --- 2. the tests, before packaging rather than after ---
if ($SkipTests) {
    Write-Step 'skipping tests (-SkipTests)'
} else {
    Write-Step 'running the extension test suite'
    & dotnet test $testProj --nologo -v quiet
    if ($LASTEXITCODE -ne 0) { Fail 'tests failed; not packaging a known-broken extension' }
}

# --- 3. package ---
# Days since 2026 and minutes since midnight: always increasing, and each part stays below the
# 65535 a version component allows.
$now = Get-Date
$version = '0.1.{0}.{1}' -f ($now.Date - [datetime]'2026-01-01').Days, [int]$now.TimeOfDay.TotalMinutes

Write-Step "packaging the VSIX (version $version)"
& dotnet build $extProj -c Release --nologo -v quiet "-p:Version=$version"
if ($LASTEXITCODE -ne 0) { Fail 'dotnet build of the extension failed' }

$vsix = Get-ChildItem (Join-Path $extDir 'bin\Release') -Recurse -Filter *.vsix |
    Sort-Object LastWriteTime -Descending |
    Select-Object -First 1
if (-not $vsix) { Fail 'no .vsix was produced' }

$packed = [IO.Compression.ZipFile]::OpenRead($vsix.FullName)
try {
    if (-not ($packed.Entries | Where-Object FullName -eq 'tool/tuipreview.dll')) {
        Fail "$($vsix.Name) does not contain tool/tuipreview.dll"
    }
} finally { $packed.Dispose() }

if ($NoInstall) {
    Write-Host "`nPackaged: $($vsix.FullName)" -ForegroundColor Green
    exit 0
}

# --- 4. install ---
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
if (-not (Test-Path $vswhere)) { Fail 'vswhere.exe not found; is Visual Studio installed?' }

# Visual Studio 2026 (18.x) is the first with a .NET 10 extension host.
$instances = & $vswhere -prerelease -version '[18.0,)' -format json | ConvertFrom-Json
if (-not $instances) { Fail 'no Visual Studio 2026 installation found' }

$installer = Join-Path $instances[0].installationPath 'Common7\IDE\VSIXInstaller.exe'
$instanceIds = ($instances | ForEach-Object instanceId) -join ','
$labels = ($instances | ForEach-Object { "$($_.displayName) $($_.installationVersion)" }) -join ', '

$installArgs = @("/instanceIds:$instanceIds")
if ($Exp) { $installArgs += '/rootSuffix:Exp' }
$installArgs += $vsix.FullName

Write-Step "installing into $labels$(if ($Exp) { ' (experimental instance)' })"
# Not /quiet: if Visual Studio is running, the installer has to say so and offer to wait or
# close it, and a quiet install would instead fail without a word.
$process = Start-Process $installer -ArgumentList $installArgs -Wait -PassThru
if ($process.ExitCode -ne 0) { Fail "VSIXInstaller exited with $($process.ExitCode)" }

Write-Host ''
Write-Host 'Installed:' -ForegroundColor Green
Write-Host "  vsix      $($vsix.FullName)"
Write-Host "  version   $version"
Write-Host ''
if ($Exp) {
    Write-Host 'Start the experimental instance with:' -ForegroundColor Green
    Write-Host '  devenv /rootSuffix Exp' -ForegroundColor Yellow
} else {
    Write-Host 'Restart Visual Studio, then right-click a .tui.cs view' -ForegroundColor Green
}
Write-Host '(for example samples\SampleApp\MainView.tui.cs). The log is under View > Output, "TUI Preview".' -ForegroundColor DarkGray
