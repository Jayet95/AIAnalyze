# AIAnalyze

A local Windows agent that watches your PC's health and explains problems in plain words: what is wrong, why, and what to do. It checks the whole PC every 10 minutes, watches CPU and GPU sensors, records FPS in games, and has an assistant you can ask about any of it.

What it checks:

- **Stability:** blue screens and unexpected shutdowns, hardware errors (WHEA), graphics driver resets, programs that keep crashing or hanging, failing Windows services, failed Windows updates (with the meaning of the error code).
- **Storage and memory:** free disk space, drive health as Windows reports it, memory pressure and the programs using the most memory.
- **Load and upkeep:** programs eating the CPU, days since the last restart, laptop battery wear.
- **Temperatures:** CPU and GPU against safe limits, live.
- **Games:** FPS per game against your usual FPS, with findings such as "The GPU was 98% busy, so the graphics card set the frame rate" or "Its clock dropped from 2743 to 2234 MHz at up to 86 °C: thermal throttling".

Everything stays on the PC. The UI is a local web page served by the service on `127.0.0.1`, so nothing is reachable from the network.

## Install

You need the .NET 10 SDK to build. Optional, for full data:

- **PresentMon** for FPS capture: download `PresentMon.exe` from the official PresentMon GitHub releases.
- **PawnIO** driver for CPU temperature and power: install it from <https://pawnio.eu> (signed; the LibreHardwareMonitor installer also adds it). The sensor library uses PawnIO, not the old WinRing0 driver that antivirus software flags. The installer warns if it is missing.

In an elevated PowerShell (Run as administrator), from the repo root:

```powershell
powershell -ExecutionPolicy Bypass -File scripts\install-service.ps1 -PresentMonPath C:\Path\To\PresentMon.exe
```

The script builds the app, scans the installed files with Microsoft Defender (it never adds exclusions), installs it as an automatic Windows service, copies PresentMon next to it, adds an **AIAnalyze** shortcut to the Start menu, and opens the UI. `-PresentMonPath` is only needed the first time; reinstalls keep the copied PresentMon.

Uninstall (collected data is kept):

```powershell
powershell -ExecutionPolicy Bypass -File scripts\uninstall-service.ps1
```

## Using it

Open **AIAnalyze** from the Start menu, or go to <http://127.0.0.1:5280/>.

- **Health** (opens first): an overall verdict, each check in green, yellow or red with its evidence, **Check now**, and the programs using the most CPU and memory.
- **Live**: CPU and GPU temperature, load, power and clock, with the last 5 minutes as charts, plus the state of FPS capture.
- **Sessions**: every game session with its findings, the assistant's feedback, median FPS against your usual FPS for that game, and per-second charts of FPS, temperatures, load and GPU clock.
- **System**: the PC's model, CPU, RAM, GPUs with driver versions, motherboard, BIOS and Windows version.

A session is saved 30 seconds after a game stops presenting frames, and only if it has at least 300 frames. `dwm.exe` is ignored.

## Assistant (chat and automatic feedback)

Click **Ask AI** in the top bar to ask questions about the PC in any language, such as "Why did my FPS drop?" or "Are my temperatures OK?". Each question goes to Claude together with a short briefing of what the app measured: the PC and its drivers, current readings, recent alerts, recent sessions with their findings, and the session you have open. Answers quote those numbers and say when the data doesn't show something.

- **Alerts**: temperature warnings and session problems appear under the bell and as pop-ups while the UI is open. Each has **Ask about this**, which opens the chat with that alert and its session.
- **Automatic feedback**: when a session ends with warnings, the assistant writes a short explanation on its own (what happened, the likely cause, what to do). It shows on the session and as an alert. At most one every 10 minutes.
- The assistant only reads data. It can't change settings or install anything.

The assistant uses **your own Anthropic API key**, and each question or automatic feedback is billed to it. To turn it on, rerun the installer with `-SetApiKey` and paste the key when asked:

```powershell
powershell -ExecutionPolicy Bypass -File scripts\install-service.ps1 -SetApiKey
```

The key is stored in `C:\ProgramData\AIAnalyze\secrets.json`, readable only by SYSTEM and Administrators. Remove it with `-RemoveApiKey`. Without a key, the rest of the app works and the chat explains how to set it up.

Assistant settings, all optional, in `C:\ProgramData\AIAnalyze\appsettings.json`:

```json
{
  "Claude": {
    "AutoFeedback": true,
    "FeedbackLanguage": "English",
    "Model": "claude-opus-5-5"
  }
}
```

## How capture works

- The service runs as LocalSystem. Sensor sampling needs administrator rights for full CPU telemetry.
- Services run in session 0, where PresentMon receives no frame events. So the service starts PresentMon in the signed-in user's session with that user's elevated token, and reads its output through a pipe.
- At boot, or at the sign-in screen, the service waits until someone signs in, then starts capture.
- Before each start and after each stop, the service stops any leftover PresentMon ETW trace session. A stale session can keep capture silent.

On a PC with an integrated and a discrete GPU, the timeline and findings use the GPU that did the work: the one with the highest median load, or the one with more memory when both are idle.

## Footprint and what is stored

Measured on a 20-thread laptop: about 0.6% of one CPU core on average (including the health check every 10 minutes) and about 155 MB of memory.

Only data the app reads back is stored, and everything has a limit:

| Data | Kept for |
|---|---|
| Sensor readings, only the ones the app uses (CPU and GPU temperature, load, power, GPU clock), once a second | 2 days |
| Health reports | 1 day (only the latest is shown) |
| Alerts | 30 days |
| Per-second FPS of each session | 30 days |
| Sessions (median FPS, findings, assistant feedback) | kept, for the per-game baseline |
| Hardware and driver snapshot | only when something changes |

Event Log entries are read for the checks but not copied into the database. Nothing leaves the PC except questions you send to the assistant, together with the briefing described above.

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
dotnet "C:\Program Files\AIAnalyze\AIAnalyze.dll" --health
```

`--health` runs the health checks once and prints them.

`--timeline` prints the findings, then one row per second with FPS next to CPU and GPU readings.

## Troubleshooting

- **No sessions appear.** Check the Live tab's FPS capture panel. "PresentMon was not found" means it is missing from the `PresentMon` folder. "Waiting for a user to sign in" clears once you sign in.
- **Capture runs but records nothing.** Look for leftover trace sessions with `logman query -ets`. The service stops its own (`AIAnalyze`) automatically; other tools that bundle PresentMon (RTSS, FrameView, Adrenalin) can be closed for a clean test.
- **Event Log entries**: `Get-WinEvent -FilterHashtable @{LogName='Application'; ProviderName='AIAnalyze'} -MaxEvents 40 | Format-List TimeCreated, Message`

## Development

A brief for building a new UI (for example in Lovable), with the full API contract and sample data, is in [docs/ui-brief-for-lovable.md](docs/ui-brief-for-lovable.md).


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
