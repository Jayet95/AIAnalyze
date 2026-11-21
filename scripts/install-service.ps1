# Publishes AIAnalyze, installs it as an automatically starting Windows service, and opens its UI.
# Run from PowerShell opened as Administrator, in the repo root:
#   powershell -ExecutionPolicy Bypass -File scripts\install-service.ps1 -PresentMonPath C:\Downloads\PresentMon.exe
#
# -PresentMonPath is needed only the first time: the file is copied to <InstallDir>\PresentMon\PresentMon.exe,
# which the service finds on its own, and reinstalls keep it.

param(
    [string]$InstallDir = "$env:ProgramFiles\AIAnalyze",

    # PresentMon.exe to copy next to the app (download it from the official PresentMon GitHub releases).
    [string]$PresentMonPath,

    # Port of the local web UI.
    [int]$Port = 5280,

    # Optional account to run the service as. Omit it: FPS capture in the signed-in user's session
    # needs LocalSystem (the default).
    [System.Management.Automation.PSCredential]$ServiceCredential,

    # Do not open the UI in the browser at the end.
    [switch]$NoBrowser
)

$ErrorActionPreference = "Stop"

$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw "Run this script in an elevated PowerShell (Run as administrator)."
}

if ($PresentMonPath -and -not (Test-Path $PresentMonPath -PathType Leaf)) {
    throw "PresentMon not found at $PresentMonPath."
}

$repoRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repoRoot "AIAnalyze\AIAnalyze.csproj"
$serviceName = "AIAnalyze"
$dataDir = Join-Path $env:ProgramData "AIAnalyze"
$uiUrl = "http://127.0.0.1:$Port/"

# Stop an existing service so its files can be replaced.
if (Get-Service -Name $serviceName -ErrorAction SilentlyContinue) {
    Stop-Service -Name $serviceName -Force -ErrorAction SilentlyContinue
    sc.exe delete $serviceName | Out-Null
    Start-Sleep -Seconds 2
}

dotnet publish $project -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -o $InstallDir
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed." }

# PresentMon next to the app: the service uses it without any configuration.
$bundledPresentMon = Join-Path $InstallDir "PresentMon\PresentMon.exe"
if ($PresentMonPath) {
    New-Item -ItemType Directory -Force (Split-Path $bundledPresentMon) | Out-Null
    Copy-Item $PresentMonPath $bundledPresentMon -Force
    Write-Host "Copied PresentMon to $bundledPresentMon"
}
if (-not (Test-Path $bundledPresentMon)) {
    Write-Warning "PresentMon is not installed, so FPS capture will be off. Rerun with -PresentMonPath <path to PresentMon.exe>."
}

# Port override goes to the ProgramData settings, which survive reinstalls.
if ($Port -ne 5280) {
    New-Item -ItemType Directory -Force $dataDir | Out-Null
    $overridePath = Join-Path $dataDir "appsettings.json"
    $settings = if (Test-Path $overridePath) { Get-Content $overridePath -Raw | ConvertFrom-Json } else { [pscustomobject]@{} }
    $settings | Add-Member -NotePropertyName Web -NotePropertyValue ([pscustomobject]@{ Port = $Port }) -Force
    $settings | ConvertTo-Json -Depth 5 | Set-Content $overridePath -Encoding utf8
}

$exe = Join-Path $InstallDir "AIAnalyze.exe"
$serviceArgs = @{
    Name           = $serviceName
    DisplayName    = "AIAnalyze PC Diagnostics"
    Description    = "Samples CPU and GPU sensors, records FPS sessions, and explains what it finds. UI: $uiUrl"
    BinaryPathName = "`"$exe`""
    StartupType    = "Automatic"
}
if ($ServiceCredential) {
    $serviceArgs["Credential"] = $ServiceCredential
    Write-Warning "Running as $($ServiceCredential.UserName). FPS capture in your session needs LocalSystem; omit -ServiceCredential for that."
}
New-Service @serviceArgs | Out-Null

# Restart automatically if it crashes.
sc.exe failure $serviceName reset= 86400 actions= restart/60000/restart/60000/restart/60000 | Out-Null

Start-Service -Name $serviceName

# Start menu shortcut to the UI, for every user.
$shortcut = Join-Path $env:ProgramData "Microsoft\Windows\Start Menu\Programs\AIAnalyze.url"
Set-Content -Path $shortcut -Value "[InternetShortcut]`r`nURL=$uiUrl" -Encoding ascii

Write-Host ""
Write-Host "AIAnalyze is installed and running."
Write-Host "  UI:          $uiUrl  (also in the Start menu as 'AIAnalyze')"
Write-Host "  Data:        $dataDir"
Write-Host "  Settings:    $dataDir\appsettings.json (optional overrides, kept across reinstalls)"

if (-not $NoBrowser) {
    Start-Sleep -Seconds 3
    Start-Process $uiUrl
}
