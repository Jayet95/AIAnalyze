# AIAnalyze

A local Windows agent that samples CPU and GPU sensors, records FPS sessions with PresentMon, and shows what happened during a session.

## Install as a service

Run in an elevated PowerShell, from the repo root:

```powershell
powershell -ExecutionPolicy Bypass -File scripts\install-service.ps1
```

Check that it runs:

```powershell
Get-Service AIAnalyze
```

Uninstall (data in `C:\ProgramData\AIAnalyze` is kept):

```powershell
powershell -ExecutionPolicy Bypass -File scripts\uninstall-service.ps1
```

## Where things are

- Installed files: `C:\Program Files\AIAnalyze`
- Database: `C:\ProgramData\AIAnalyze\telemetry.db`
- Settings, including the PresentMon path: `C:\Program Files\AIAnalyze\appsettings.json`
- Logs: Windows Event Log, source `AIAnalyze`

## Viewing sessions

```powershell
dotnet "C:\Program Files\AIAnalyze\AIAnalyze.dll" --sessions
dotnet "C:\Program Files\AIAnalyze\AIAnalyze.dll" --timeline <id>
```

`--timeline` prints one row per second: FPS next to CPU and GPU temperature, load, clock and power.

## Testing on a second computer

1. `git pull`
2. Install the service (see above) and check `Get-Service AIAnalyze` shows `Running`.
3. Confirm the database exists: `Test-Path C:\ProgramData\AIAnalyze\telemetry.db`
4. Install PresentMon from its official GitHub releases and run `PresentMon.exe --help`. Save the output.
5. Set `PresentMon:ExecutablePath` in `appsettings.json` to the PresentMon path, then run `Restart-Service AIAnalyze`.
6. Play a game for a few minutes, then run `--sessions` and `--timeline <id>`.

Sessions saved before per-second FPS was added have no timeline rows.

## Tests

```powershell
dotnet test tests\AIAnalyze.Tests\AIAnalyze.Tests.csproj
```
