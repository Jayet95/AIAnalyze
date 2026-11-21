# Builds AIAnalyze-Setup-<version>.exe in dist\. Run on a build machine (not the user's PC) with:
#   - .NET 10 SDK
#   - Node.js LTS (for the UI; without it the simpler built-in UI is packaged)
#   - Inno Setup 6 (https://jrsoftware.org/isdl.php)
# Usage, from the repo root:
#   powershell -ExecutionPolicy Bypass -File scripts\build-installer.ps1 [-PresentMonPath C:\Downloads\PresentMon.exe]

param(
    # PresentMon.exe to bundle (MIT-licensed; download it from the official PresentMon GitHub releases).
    [string]$PresentMonPath,

    # Path to the Inno Setup compiler, if it is not in the default folder.
    [string]$IsccPath = "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe"
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repoRoot "AIAnalyze\AIAnalyze.csproj"
$app = Join-Path $repoRoot "build\app"

if (-not (Test-Path $IsccPath)) {
    throw "Inno Setup 6 was not found at $IsccPath. Install it from https://jrsoftware.org/isdl.php or pass -IsccPath."
}
if ($PresentMonPath -and -not (Test-Path $PresentMonPath -PathType Leaf)) {
    throw "PresentMon not found at $PresentMonPath."
}

# Version from the project file, so the installer and the app agree.
$version = ([xml](Get-Content $project)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
if (-not $version) { throw "No <Version> in $project." }
Write-Host "Building AIAnalyze $version"

# 1. The app, self-contained so the user needs no .NET.
Remove-Item -Recurse -Force $app -ErrorAction SilentlyContinue
dotnet publish $project -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -o $app
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed." }

# 2. The UI from the ui/ submodule, when Node.js is available.
$uiDir = Join-Path $repoRoot "ui"
if (Get-Command git -ErrorAction SilentlyContinue) {
    git -C $repoRoot submodule update --init ui
}
if ((Test-Path (Join-Path $uiDir "package.json")) -and (Get-Command npm -ErrorAction SilentlyContinue)) {
    Push-Location $uiDir
    try {
        npm install --no-audit --no-fund
        if ($LASTEXITCODE -ne 0) { throw "npm install failed." }
        npm run build:local
        if ($LASTEXITCODE -ne 0) { throw "The UI build failed." }
    } finally {
        Pop-Location
    }
    $web = Join-Path $app "wwwroot"
    Remove-Item -Recurse -Force $web
    New-Item -ItemType Directory $web | Out-Null
    Copy-Item (Join-Path $uiDir "dist-local\*") $web -Recurse -Force
    Write-Host "Packaged the new UI."
} else {
    Write-Warning "Node.js or the ui/ submodule is missing; packaging the built-in UI."
}

# 3. Optional PresentMon, which the service finds in the PresentMon folder next to it.
if ($PresentMonPath) {
    $pmDir = Join-Path $app "PresentMon"
    New-Item -ItemType Directory -Force $pmDir | Out-Null
    Copy-Item $PresentMonPath (Join-Path $pmDir "PresentMon.exe") -Force
    Write-Host "Bundled PresentMon. Include its MIT license notice when you distribute the installer."
} else {
    Write-Warning "PresentMon is not bundled; FPS capture stays off until it is added."
}

# 4. The setup wizard.
& $IsccPath "/DMyAppVersion=$version" (Join-Path $repoRoot "installer\AIAnalyze.iss")
if ($LASTEXITCODE -ne 0) { throw "Inno Setup failed." }

$setup = Join-Path $repoRoot "dist\AIAnalyze-Setup-$version.exe"

# A copy with a fixed name, so the website can link to the newest release:
#   https://github.com/Jayet95/AIAnalyze/releases/latest/download/AIAnalyze-Setup.exe
$latest = Join-Path $repoRoot "dist\AIAnalyze-Setup.exe"
Copy-Item $setup $latest -Force
$hash = (Get-FileHash $setup -Algorithm SHA256).Hash
Set-Content (Join-Path $repoRoot "dist\SHA256SUMS.txt") "$hash  AIAnalyze-Setup-$version.exe`n$hash  AIAnalyze-Setup.exe" -Encoding ascii

Write-Host ""
Write-Host "Installer ready: $setup"
Write-Host "Upload both exe files and SHA256SUMS.txt to a GitHub release (tag v$version)."
Write-Host "Sign it before publishing (signtool sign /fd SHA256 ...), so Windows and antivirus software trust it."
