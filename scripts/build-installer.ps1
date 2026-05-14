param(
    [string] $Configuration = "Release",
    [string] $Runtime = "win-x64",
    [string] $Version = "0.1.0",
    [string] $InnoCompilerPath = ""
)

$ErrorActionPreference = "Stop"

if ($Runtime -ne "win-x64") {
    throw "The Inno Setup installer is configured for win-x64. Use -Runtime win-x64."
}

if ($Version -notmatch '^\d+(\.\d+){0,3}$') {
    throw "Version must be numeric for Inno Setup version metadata, for example 0.1.0 or 1.2.3.4."
}

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$projectPath = Join-Path $repoRoot "src\Rawr.App\Rawr.App.csproj"
$innoScriptPath = Join-Path $repoRoot "installer\RAWR.iss"
$publishDir = Join-Path $repoRoot "artifacts\publish\RAWR-$Runtime"
$installerOutputDir = Join-Path $repoRoot "artifacts\installer"

Write-Host "Publishing RAWR ($Configuration, $Runtime)..."
dotnet publish $projectPath `
    -c $Configuration `
    -r $Runtime `
    --self-contained true `
    -o $publishDir `
    /p:PublishSingleFile=false `
    /p:DebugType=None `
    /p:DebugSymbols=false

$exePath = Join-Path $publishDir "RAWR.exe"
if (-not (Test-Path $exePath)) {
    throw "Publish did not produce $exePath"
}

if ([string]::IsNullOrWhiteSpace($InnoCompilerPath)) {
    $isccCommand = Get-Command "ISCC.exe" -ErrorAction SilentlyContinue
    if ($isccCommand) {
        $InnoCompilerPath = $isccCommand.Source
    }
}

if ([string]::IsNullOrWhiteSpace($InnoCompilerPath)) {
    $knownPaths = @(
        "C:\Program Files (x86)\Inno Setup 6\ISCC.exe",
        "C:\Program Files\Inno Setup 6\ISCC.exe",
        "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
    )

    foreach ($candidate in $knownPaths) {
        if (Test-Path $candidate) {
            $InnoCompilerPath = $candidate
            break
        }
    }
}

if ([string]::IsNullOrWhiteSpace($InnoCompilerPath)) {
    $uninstallRoots = @(
        "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*",
        "HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*",
        "HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*"
    )

    $innoInstall = Get-ItemProperty -Path $uninstallRoots -ErrorAction SilentlyContinue |
        Where-Object { $_.DisplayName -like "Inno Setup*" -and $_.InstallLocation } |
        Select-Object -First 1

    if ($innoInstall) {
        $candidate = Join-Path $innoInstall.InstallLocation "ISCC.exe"
        if (Test-Path $candidate) {
            $InnoCompilerPath = $candidate
        }
    }
}

if ([string]::IsNullOrWhiteSpace($InnoCompilerPath) -or -not (Test-Path $InnoCompilerPath)) {
    Write-Host ""
    Write-Host "Publish complete: $publishDir"
    Write-Warning "Inno Setup Compiler was not found. Install Inno Setup 6, then rerun this script to create RAWR-Setup.exe."
    Write-Host "Install with: winget install JRSoftware.InnoSetup"
    exit 0
}

New-Item -ItemType Directory -Force -Path $installerOutputDir | Out-Null

$env:RAWR_VERSION = $Version
$env:RAWR_PUBLISH_DIR = $publishDir
$env:RAWR_INSTALLER_OUTPUT_DIR = $installerOutputDir

Write-Host "Building installer with Inno Setup..."
& $InnoCompilerPath $innoScriptPath

if ($LASTEXITCODE -ne 0) {
    throw "Inno Setup failed with exit code $LASTEXITCODE"
}

Write-Host ""
Write-Host "Installer created in: $installerOutputDir"
