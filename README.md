# AIAnalyze

A local Windows agent that watches your PC while you play and explains what it sees. It samples CPU and GPU sensors every second, records FPS per game with PresentMon, and turns each gaming session into plain-language findings, for example:

- "The GPU was 98% busy, so the graphics card set the frame rate. Lower resolution or upscaling will raise FPS; a faster CPU will not."
- "While the GPU was fully loaded, its clock dropped from 2743 to 2234 MHz at up to 86 °C. That pattern is thermal throttling."
- "FPS stayed at about 144, which matches a frame cap: V-Sync, an in-game limiter, or the monitor's refresh rate."

Everything stays on the PC. The UI is a local web page served by the service on `127.0.0.1`, so nothing is reachable from the network.

## Install

You need the .NET 10 SDK to build, and PresentMon for FPS capture. Download `PresentMon.exe` from the official PresentMon GitHub releases.

In an elevated PowerShell (Run as administrator), from the repo root:

```powershell
powershell -ExecutionPolicy Bypass -File scripts\install-service.ps1 -PresentMonPath C:\Path\To\PresentMon.exe
```

The script builds the app, installs it as an automatic Windows service, copies PresentMon next to it, adds an **AIAnalyze** shortcut to the Start menu, and opens the UI. `-PresentMonPath` is only needed the first time; reinstalls keep the copied PresentMon.

Uninstall (collected data is kept):

```powershell
powershell -ExecutionPolicy Bypass -File scripts\uninstall-service.ps1
```

## Using it

Open **AIAnalyze** from the Start menu, or go to <http://127.0.0.1:5280/>.

- **Live**: CPU and GPU temperature, load, power and clock, with the last 5 minutes as charts, plus the state of FPS capture.
- **Sessions**: every game session with its findings, median FPS against your usual FPS for that game, and per-second charts of FPS, temperatures, load and GPU clock.
- **System**: the PC's model, CPU, RAM, GPUs with driver versions, motherboard, BIOS and Windows version.

A session is saved 30 seconds after a game stops presenting frames, and only if it has at least 300 frames. `dwm.exe` is ignored.

## How capture works

- The service runs as LocalSystem. Sensor sampling needs administrator rights for full CPU telemetry.
- Services run in session 0, where PresentMon receives no frame events. So the service starts PresentMon in the signed-in user's session with that user's elevated token, and reads its output through a pipe.
- At boot, or at the sign-in screen, the service waits until someone signs in, then starts capture.
- Before each start and after each stop, the service stops any leftover PresentMon ETW trace session. A stale session can keep capture silent.

On a PC with an integrated and a discrete GPU, the timeline and findings use the GPU that did the work: the one with the highest median load, or the one with more memory when both are idle.

## Where things are

- Installed files: `C:\Program Files\AIAnalyze` (PresentMon in its `PresentMon` subfolder)
- Database: `C:\ProgramData\AIAnalyze\telemetry.db` (sensor samples are kept for 2 days, sessions and findings indefinitely)
- Optional settings overrides: `C:\ProgramData\AIAnalyze\appsettings.json`, kept across reinstalls
- Logs: Windows Event Log, source `AIAnalyze`

Settings you can override, all optional:

```json
{
  "PresentMon": {
    "ExecutablePath": "C:\\Path\\To\\PresentMon.exe",
    "Arguments": "--output_stdout --no_console_stats --v1_metrics --session_name AIAnalyze --stop_existing_session",
    "UserSession": true
  },
  "Web": { "Port": 5280 }
}
```

## Command line

Run these against the installed app:

```powershell
dotnet "C:\Program Files\AIAnalyze\AIAnalyze.dll" --sessions
dotnet "C:\Program Files\AIAnalyze\AIAnalyze.dll" --timeline <id>
dotnet "C:\Program Files\AIAnalyze\AIAnalyze.dll" --dump-sensors
```

`--timeline` prints the findings, then one row per second with FPS next to CPU and GPU readings.

## Troubleshooting

- **No sessions appear.** Check the Live tab's FPS capture panel. "PresentMon was not found" means it is missing from the `PresentMon` folder. "Waiting for a user to sign in" clears once you sign in.
- **Capture runs but records nothing.** Look for leftover trace sessions with `logman query -ets`. The service stops its own (`AIAnalyze`) automatically; other tools that bundle PresentMon (RTSS, FrameView, Adrenalin) can be closed for a clean test.
- **Event Log entries**: `Get-WinEvent -FilterHashtable @{LogName='Application'; ProviderName='AIAnalyze'} -MaxEvents 40 | Format-List TimeCreated, Message`

## Development

```powershell
dotnet test tests\AIAnalyze.Tests\AIAnalyze.Tests.csproj
```

To work on the UI without a game, seed a demo session into a scratch folder and run the app from there:

```powershell
$env:AIANALYZE_DATA_DIR = "$env:TEMP\aianalyze-demo"
dotnet run --project AIAnalyze -- --seed-demo
dotnet AIAnalyze\bin\Debug\net10.0\AIAnalyze.dll
```

Then open <http://127.0.0.1:5280/>. The `AIANALYZE_DATA_DIR` variable moves the database away from ProgramData.
