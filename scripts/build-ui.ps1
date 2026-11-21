# Builds the UI in ui/ (the AIAnalyze-UI submodule) into ui\dist-local. Used by install-service.ps1 and
# build-installer.ps1. Returns $true on success. Prints the real failure when it fails.
#
# Uses bun when it is installed (the project's lock file is bun.lock), otherwise npm. If the build fails,
# it deletes node_modules and npm's lock file and tries once more: a half-installed node_modules (for
# example after an interrupted install) otherwise makes every later build fail.

param(
    [Parameter(Mandatory = $true)][string]$UiDir
)

function Invoke-Step([string]$Exe, [string[]]$Arguments, [string]$Log) {
    # Run through cmd so npm/bun warnings on stderr don't become PowerShell errors; keep all output for the report.
    # The command line is handed to cmd verbatim through ProcessStartInfo: PowerShell 5.1 re-quotes native
    # arguments that contain quotes and spaces, and cmd strips the first and last quote of "npm" ... "log",
    # which made npm.cmd look for itself in the current folder ("Cannot find module ...\node_modules\npm\bin\
    # npm-prefix.js"). With /s and one outer pair of quotes, cmd removes exactly that pair.
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = "$env:SystemRoot\System32\cmd.exe"
    $psi.Arguments = "/d /s /c `"$Exe $($Arguments -join ' ') > `"$Log`" 2>&1`""
    $psi.WorkingDirectory = (Get-Location).Path
    $psi.UseShellExecute = $false
    $process = [System.Diagnostics.Process]::Start($psi)
    $process.WaitForExit()
    return $process.ExitCode -eq 0
}

function Invoke-Build([string]$Tool, [string]$LogDir) {
    if ($Tool -eq "bun") {
        if (-not (Invoke-Step "bun" @("install", "--frozen-lockfile") (Join-Path $LogDir "install.log"))) {
            if (-not (Invoke-Step "bun" @("install") (Join-Path $LogDir "install.log"))) { return "install" }
        }
        if (-not (Invoke-Step "bun" @("run", "build:local") (Join-Path $LogDir "build.log"))) { return "build" }
    } else {
        if (-not (Invoke-Step "npm" @("install", "--no-audit", "--no-fund") (Join-Path $LogDir "install.log"))) { return "install" }
        if (-not (Invoke-Step "npm" @("run", "build:local") (Join-Path $LogDir "build.log"))) { return "build" }
    }
    return $null
}

if (-not (Test-Path (Join-Path $UiDir "package.json"))) {
    Write-Warning "No UI found in $UiDir."
    return $false
}

$tool = if (Get-Command bun -ErrorAction SilentlyContinue) { "bun" } elseif (Get-Command npm -ErrorAction SilentlyContinue) { "npm" } else { $null }
if (-not $tool) {
    Write-Warning "Neither bun nor Node.js (npm) is installed, so the UI can't be built. Install Node.js LTS and run again."
    return $false
}

$logDir = Join-Path ([IO.Path]::GetTempPath()) "aianalyze-ui-build"
New-Item -ItemType Directory -Force $logDir | Out-Null

Push-Location $UiDir
try {
    Write-Host "Building the UI with $tool..."
    $failed = Invoke-Build $tool $logDir
    if ($failed) {
        Write-Host "The UI $failed step failed; retrying from a clean install..."
        Remove-Item -Recurse -Force (Join-Path $UiDir "node_modules") -ErrorAction SilentlyContinue
        Remove-Item -Force (Join-Path $UiDir "package-lock.json") -ErrorAction SilentlyContinue
        $failed = Invoke-Build $tool $logDir
    }

    if ($failed) {
        $log = Join-Path $logDir "$failed.log"
        Write-Warning "The UI $failed step failed with $tool. Last lines of $log :"
        Get-Content $log -Tail 25 | ForEach-Object { Write-Host "    $_" }
        return $false
    }
    if (-not (Test-Path (Join-Path $UiDir "dist-local\index.html"))) {
        Write-Warning "The UI build finished but produced no dist-local\index.html."
        return $false
    }
    return $true
} finally {
    Pop-Location
}
