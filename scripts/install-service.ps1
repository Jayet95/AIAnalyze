# Publishes AIAnalyze and installs it as an automatically starting Windows service.
# Run from PowerShell opened as Administrator, in the repo root:
#   powershell -ExecutionPolicy Bypass -File scripts\install-service.ps1

param(
    [string]$InstallDir = "$env:ProgramFiles\AIAnalyze"
)

$ErrorActionPreference = "Stop"

$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw "Run this script in an elevated PowerShell (Run as administrator)."
}

$repoRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repoRoot "AIAnalyze\AIAnalyze.csproj"
$serviceName = "AIAnalyze"

# Stop an existing service so its files can be replaced.
if (Get-Service -Name $serviceName -ErrorAction SilentlyContinue) {
    Stop-Service -Name $serviceName -Force -ErrorAction SilentlyContinue
    sc.exe delete $serviceName | Out-Null
    Start-Sleep -Seconds 2
}

dotnet publish $project -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -o $InstallDir
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed." }

$exe = Join-Path $InstallDir "AIAnalyze.exe"
New-Service -Name $serviceName `
    -DisplayName "AIAnalyze PC Diagnostics" `
    -Description "Samples CPU and GPU sensors and records FPS sessions locally." `
    -BinaryPathName "`"$exe`"" `
    -StartupType Automatic | Out-Null

# Restart automatically if it crashes.
sc.exe failure $serviceName reset= 86400 actions= restart/60000/restart/60000/restart/60000 | Out-Null

Start-Service -Name $serviceName
Write-Host "Installed and started service '$serviceName' from $InstallDir"
Write-Host "Data folder: $env:ProgramData\AIAnalyze"
Write-Host "To enable FPS capture, set PresentMon:ExecutablePath in $InstallDir\appsettings.json and restart the service."
