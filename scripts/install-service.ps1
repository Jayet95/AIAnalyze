# Publishes AIAnalyze, installs it as an automatically starting Windows service, and opens its UI.
# Run from PowerShell opened as Administrator, in the repo root:
#   powershell -ExecutionPolicy Bypass -File scripts\install-service.ps1 -PresentMonPath C:\Downloads\PresentMon.exe -SetApiKey
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
    [switch]$NoBrowser,

    # Ask for your own Anthropic API key for the chat assistant. It is stored in
    # C:\ProgramData\AIAnalyze\secrets.json, readable only by SYSTEM and Administrators.
    [switch]$SetApiKey,

    # Delete the stored API key; the chat assistant is then turned off.
    [switch]$RemoveApiKey,

    # Skip the Microsoft Defender scan of the installed files.
    [switch]$SkipScan,

    # Keep the built-in UI instead of building the one in ui/ (needs Node.js).
    [switch]$SkipUiBuild
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
# CPU temperature and power need the PawnIO driver (signed; used by LibreHardwareMonitor instead of the old
# WinRing0 driver that antivirus flags). It is not bundled; LibreHardwareMonitor's installer adds it.
if (-not (Get-Service -Name PawnIO -ErrorAction SilentlyContinue)) {
    Write-Warning "The PawnIO driver is not installed, so CPU temperature and power will be blank. Install it from https://pawnio.eu (signed), then restart the AIAnalyze service."
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

# The chat assistant uses the installing user's own Anthropic API key.
$secretsPath = Join-Path $dataDir "secrets.json"
if ($RemoveApiKey -and (Test-Path $secretsPath)) {
    Remove-Item $secretsPath -Force
    Write-Host "Removed the stored API key."
}
if ($SetApiKey) {
    $secure = Read-Host "Paste your Anthropic API key (input is hidden)" -AsSecureString
    $bstr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure)
    try {
        $key = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($bstr).Trim()
    } finally {
        [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr)
    }
    if (-not $key) { throw "No key entered." }

    New-Item -ItemType Directory -Force $dataDir | Out-Null
    # Create and lock the file before the key is written: only SYSTEM (S-1-5-18) and Administrators (S-1-5-32-544).
    Set-Content -Path $secretsPath -Value "{}" -Encoding ascii
    icacls $secretsPath /inheritance:r /grant:r "*S-1-5-18:F" "*S-1-5-32-544:F" | Out-Null
    if ($LASTEXITCODE -ne 0) { Remove-Item $secretsPath -Force; throw "Could not restrict access to $secretsPath." }
    @{ Claude = @{ ApiKey = $key } } | ConvertTo-Json | Set-Content -Path $secretsPath -Encoding ascii
    $key = $null
    Write-Host "Stored the API key in $secretsPath (readable only by SYSTEM and Administrators)."
}

# The UI in ui/ (built in Lovable) replaces the built-in one when Node.js is available.
$uiDir = Join-Path $repoRoot "ui"
if (-not $SkipUiBuild -and (Test-Path (Join-Path $uiDir "package.json"))) {
    if (Get-Command npm -ErrorAction SilentlyContinue) {
        Write-Host "Building the UI from $uiDir..."
        Push-Location $uiDir
        try {
            npm install --no-audit --no-fund
            if ($LASTEXITCODE -ne 0) { throw "npm install failed" }
            npm run build:local
            if ($LASTEXITCODE -ne 0) { throw "the UI build failed" }

            $web = Join-Path $InstallDir "wwwroot"
            Remove-Item -Recurse -Force $web -ErrorAction SilentlyContinue
            New-Item -ItemType Directory $web | Out-Null
            Copy-Item (Join-Path $uiDir "dist-local\*") $web -Recurse -Force
            Write-Host "Installed the new UI."
        } catch {
            Write-Warning "Could not build the UI ($($_.Exception.Message)). The built-in UI is used instead."
        } finally {
            Pop-Location
        }
    } else {
        Write-Warning "Node.js is not installed, so the built-in UI is used. Install Node.js LTS and rerun to get the new UI."
    }
}

# Scan the installed files with Microsoft Defender before starting the service, so a detection shows up here
# instead of the service being blocked later. This never adds exclusions.
if (-not $SkipScan -and (Get-Command Start-MpScan -ErrorAction SilentlyContinue)) {
    Write-Host "Scanning $InstallDir with Microsoft Defender..."
    try {
        $scanStart = Get-Date
        Start-MpScan -ScanType CustomScan -ScanPath $InstallDir
        $hits = Get-MpThreatDetection -ErrorAction SilentlyContinue |
            Where-Object { $_.InitialDetectionTime -ge $scanStart -and ($_.Resources -join ' ') -like "*$InstallDir*" }
        if ($hits) {
            Write-Warning "Microsoft Defender flagged files in $InstallDir. Do not add an exclusion; report it on the project page with this output:"
            $hits | Format-List ThreatID, Resources, InitialDetectionTime | Out-String | Write-Host
        } else {
            Write-Host "Microsoft Defender found nothing in $InstallDir."
        }
    } catch {
        Write-Warning "The Defender scan could not run ($($_.Exception.Message)). If another antivirus is installed, it scans the files instead."
    }
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
if (Test-Path $secretsPath) {
    Write-Host "  Assistant:   on (uses your API key from $secretsPath)"
} else {
    Write-Host "  Assistant:   off. To turn it on, rerun with -SetApiKey and paste your own Anthropic API key."
}

if (-not $NoBrowser) {
    Start-Sleep -Seconds 3
    Start-Process $uiUrl
}
