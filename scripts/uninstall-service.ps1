# Stops and removes the AIAnalyze Windows service. Collected data in ProgramData is kept.
# Run from an elevated PowerShell:
#   powershell -ExecutionPolicy Bypass -File scripts\uninstall-service.ps1

$ErrorActionPreference = "Stop"

$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw "Run this script in an elevated PowerShell (Run as administrator)."
}

$serviceName = "AIAnalyze"
if (Get-Service -Name $serviceName -ErrorAction SilentlyContinue) {
    Stop-Service -Name $serviceName -Force -ErrorAction SilentlyContinue
    sc.exe delete $serviceName | Out-Null
    Write-Host "Removed service '$serviceName'."
} else {
    Write-Host "Service '$serviceName' is not installed."
}
