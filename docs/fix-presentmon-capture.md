# Fix: FPS capture stops right after the service starts

> **Status:** fixes 1–3 below were done in `2657d5b` on `main`. A capture test on that commit found three new problems; see [Round 2](#round-2-capture-test-on-2657d5b) at the end.

Found on 2026-11-20 on the first machine (Ryzen 7 7800X3D, RTX 5080), service installed with `scripts\install-service.ps1`.

## Setup used

- PresentMon 2.3.1 (official Intel build) copied to `C:\Program Files\AIAnalyze\PresentMon\PresentMon.exe`
- `C:\Program Files\AIAnalyze\appsettings.json`:

```json
"PresentMon": {
  "ExecutablePath": "C:\\Program Files\\AIAnalyze\\PresentMon\\PresentMon.exe",
  "Arguments": "--output_stdout --no_console_stats --v1_metrics --session_name AIAnalyze --stop_existing_session"
}
```

`--v1_metrics` is needed: PresentMon 2.x writes 2.x column names by default, and the parser looks for 1.x names.

## 1. Parser throws on the first line and kills FPS capture (must fix)

About one second after `Restart-Service AIAnalyze`, the Event Log shows:

```
FPS capture stopped unexpectedly
System.FormatException: PresentMon header is missing Application, TimeInSeconds or MsBetweenPresents.
   at AIAnalyze.Analysis.PresentMonLineParser.TryParse(...) in Analysis\PresentMonLineParser.cs:line 34
   at AIAnalyze.Capture.FpsMonitorService.ExecuteAsync(...) in Capture\FpsMonitorService.cs:line 38
```

`PresentMonLineParser.TryParse` treats the first non-empty line as the CSV header and throws if it is not.
`FpsMonitorService` catches that once and returns, so FPS capture stays off until the service restarts. Sensor sampling keeps working.

Fix:

- Until a header has been found, skip lines that do not contain all three columns, and return `false` instead of throwing.
- Match column names case-insensitively. PresentMon's 1.x-style output uses `msBetweenPresents`, but the parser looks for `MsBetweenPresents`. Both spellings appear in the 2.3.1 binary, so it is not certain which one it prints.
- Log the first skipped non-header line once, at Information level, so the next failure shows what PresentMon actually printed.
- Apply the same change to `PresentMonParser.Parse`, which has the same header logic.
- Tests to add: a status line before the header, a lowercase `msBetweenPresents` header, and a stream with no header at all, which should produce no frames and no exception.

## 2. FpsMonitorService should not give up after one error (should fix)

If PresentMon exits or the parser fails, the service logs one error and FPS capture is off until a restart.
Restart PresentMon with a delay (for example 30 seconds, then backing off) instead of returning.

## 3. Reinstalling erases the PresentMon setting (should fix)

`install-service.ps1` runs `dotnet publish -o "C:\Program Files\AIAnalyze"`, which overwrites `appsettings.json` with the repo copy where `ExecutablePath` is empty.
Every reinstall silently turns FPS capture off.

Fix, either:

- back up `appsettings.json` before publishing and restore it afterwards when it exists, or
- read overrides from a file the installer never touches, such as `C:\ProgramData\AIAnalyze\appsettings.json`, added with `builder.Configuration.AddJsonFile(path, optional: true)`.

## How the capture was checked

PresentMon was run elevated for 10–15 seconds with the same arguments while nothing was rendering. It wrote nothing to stdout, and stderr showed only `warning: N ETW events were lost.`
So the exact first line the service received is still unknown. The logging from fix 1 will show it.

## Round 2: capture test on 2657d5b

Tested on 2026-11-20 on the same machine. The service was reinstalled from `main` at `2657d5b`, and the override was written to `C:\ProgramData\AIAnalyze\appsettings.json` with the same PresentMon path and arguments as above.
FurMark 2 (`furmark.exe --demo furmark-gl --width 1280 --height 720 --max-time N`) was used as the rendering app.

What works now: the service starts PresentMon with no errors, the parser finds the header (PresentMon 2.3.1 prints `msBetweenPresents`, lowercase, which the case-insensitive match handles), and frames reach the `sessions` and `fps_seconds` tables.

The real header PresentMon 2.3.1 prints with `--v1_metrics`:

```
Application,ProcessID,SwapChainAddress,Runtime,SyncInterval,PresentFlags,Dropped,TimeInSeconds,msInPresentAPI,msBetweenPresents,AllowsTearing,PresentMode,msUntilRenderComplete,msUntilDisplayed,msBetweenDisplayChange,msUntilRenderStart,msGPUActive,msSinceInput
```

### 4. The service's PresentMon gets no frames on its own (must fix)

| Run | Another PresentMon running at the same time | Rows saved by the service |
|---|---|---|
| FurMark 60 s | no | 0 |
| FurMark 30 s | yes: an elevated copy in the user session, `--session_name AIAnalyzeProbe` | about 17,800 frames |
| FurMark 20 s | no | 0 |

During the runs the service's PresentMon was alive: a child of the service process, started at service start, and never restarted.
It wrote nothing to the service log, not even the "output before header" line, so it most likely produced no output at all while running alone.

Not proven, but the likely cause: PresentMon runs as LocalSystem in session 0, and the ETW trace session it starts there does not enable the providers it needs, so no present events arrive.
When a second PresentMon session enables those providers from the user session, events flow to both.
This would also explain captures that seem to work only while RivaTuner, NVIDIA FrameView or AMD Adrenalin (which all bundle PresentMon) are running.

To check:

- Redirect and log PresentMon's **stderr** in `PresentMonRunner`. It is not redirected now, so any error PresentMon prints under the service is lost.
- Run the same arguments as LocalSystem by hand (for example `psexec -s -i`) and see whether a CSV header appears.
- If session 0 is the cause, options are: run the service as an admin account, start PresentMon in the user session, or use PresentMon's own service/API (`PresentMonService`) instead of the console app.

### 5. Sessions break into one-frame pieces when several apps render (must fix)

PresentMon reports every process that presents, not just the game. In the 30-second run, frames from four apps were interleaved:

| Application | Sessions saved | Frames | Median FPS |
|---|---|---|---|
| furmark.exe | 2,496 | 9,486 | 580 |
| dwm.exe | 2,494 | 3,532 | 128 |
| Code.exe | 2,489 | 2,495 | 145 |
| Surfshark.exe | 2,287 | 2,296 | 142 |

That is 9,766 sessions in about 18 seconds. `SessionTracker.AddFrame` closes the session whenever `frame.Application` differs from the current one, so every switch between apps saves a session of one or a few frames.
This also fills the per-game baseline with junk sessions.

Fix:

- Track one open session per application, for example a dictionary keyed by application with its own frames, start and last time.
- Ignore system and background presenters: at least `dwm.exe`, plus a configurable exclude list. PresentMon's `--exclude dwm.exe` can be added to the default arguments as well.
- Optionally only keep sessions for the foreground app, or require a minimum length (for example 30 seconds and 300 frames) before a session is saved.
- Tests: interleaved frames from two apps give one session each, `dwm.exe` frames give no session, and a session shorter than the minimum is not saved.

### 6. A session only ends when the next frame arrives (should fix)

`SessionTracker` checks the idle gap only inside `AddFrame`. When the game exits and nothing else renders, its session stays open until the next frame from anywhere, or until the service stops.
Add a timer, for example once per second in `FpsMonitorService`, that closes every session whose last frame is older than `IdleGap`.

### Test data to remove

The 30-second run left 9,766 junk sessions, with matching `fps_seconds` rows, in `C:\ProgramData\AIAnalyze\telemetry.db`, all with `start_utc` between `1795165768000` and `1795165787000`.
Delete them after fix 5, or they will skew the baselines for these apps.

## Round 3: review of `docs/second-computer-checklist.md` (main, e580a37)

Reviewed on 2026-11-20. The code it relies on (`ff39c83`) builds, and all 38 tests pass. The order of steps, deleting `fps_seconds` before `sessions`, and the ProgramData override in step 3 are correct.

### 7. Step 7 cannot show the lines it asks for (must fix)

Step 7 says to look in the Event Log for `PresentMon: ...` (stderr) and `PresentMon output before header (skipped): ...`.
Both are logged with `LogInformation` (`FpsMonitorService.cs` lines 58 and 65), and so is `Session saved`.
The .NET Event Log provider writes only Warning and above unless configured otherwise. On this machine the log has no `Application started` line and no AIAnalyze messages at all, only the service controller's `Service started successfully.`
So the step meant to explain missing frames will come back empty.

Fix, either:

- add an Event Log level to `appsettings.json`:

  ```json
  "Logging": {
    "LogLevel": { "Default": "Information", "Microsoft.Hosting.Lifetime": "Information" },
    "EventLog": { "LogLevel": { "Default": "Information" } }
  }
  ```

- or log PresentMon's stderr and the before-header line at Warning.

### 8. Step 2 depends on `sqlite3`, which is not installed (must fix)

`sqlite3` is not on this machine, and it is not part of Windows. The checklist only says to send `Get-Command sqlite3`, so the cleanup cannot be done.
Add a command to the app itself, which already references Microsoft.Data.Sqlite. For example `AIAnalyze --count-sessions <fromUtcMs> <toUtcMs>` and `AIAnalyze --delete-sessions <fromUtcMs> <toUtcMs>`, deleting `fps_seconds` first in one transaction.
The service must be stopped, or the command must tolerate the WAL lock.

### 9. Step 2 belongs to the first computer (should fix)

The 9,766 junk sessions and their time range exist only in the first computer's database. On a real second computer the count is 0, which reads like a failure.
Move the cleanup to a separate "first computer only" section, or say that 0 is expected on a new machine.

### 10. Step 6 does not say to wait after closing the game (should fix)

A session is saved only after `IdleGap` (30 s) without frames, and only with at least `MinFrames` (300) frames.
Running `--sessions` right after quitting shows nothing. Add: "Close the game and wait at least one minute before step 8."

### 11. Step 6 will likely record nothing because of fix 4 (should fix)

Section 4 (no frames while the service's PresentMon runs alone) is still open. In the FurMark test the service saved frames only while a second PresentMon was running.
RivaTuner (RTSS), NVIDIA FrameView and AMD Adrenalin all bundle PresentMon, so whether they are open changes the result.
Have the tester write down which of them were running, and repeat the test once with all of them closed, so a pass is not caused by them.

### 12. Small fixes

- The checklist's code blocks are labeled `bash` but contain PowerShell. Use `powershell`.
- The last line of `scripts/install-service.ps1` still says to set `PresentMon:ExecutablePath` in `C:\Program Files\AIAnalyze\appsettings.json`. It should point to `C:\ProgramData\AIAnalyze\appsettings.json`, as the README now does.
- `Get-EventLog` exists only in Windows PowerShell 5.1. Use `Get-WinEvent -FilterHashtable @{LogName='Application'; ProviderName='AIAnalyze'} -MaxEvents 40 | Format-List TimeCreated, Message`, which works in 5.1 and 7.

## Round 4: running the revised checklist on the first computer (main, 31a619d)

Run on 2026-11-20. Steps 1–4 passed: the build has 39 passing tests, the service reinstalled and is `Running`, and the ProgramData override survived the reinstall.
The cleanup found 9,767 sessions in the range, deleted them all, and a second count returned 0.

### 13. The cleanup section does not say it needs Administrator (should fix)

"First computer only: delete the junk test sessions" runs `Stop-Service` and `Start-Service`, and writes to `C:\ProgramData\AIAnalyze\telemetry.db`, which the service account (LocalSystem) created.
Both need an elevated window, but the section is not marked **Administrator** the way step 2 is. In a normal window `Stop-Service` fails, and the delete may fail on file permissions or on the running service's lock.
Add **(Administrator)** to the section title.

### 14. Step 5 should say how to check and close the tools (small)

- Give a command to see what is running, for example:

  ```powershell
  Get-Process RTSS, RTSSHooksLoader64, MSIAfterburner, FrameView*, RadeonSoftware -ErrorAction SilentlyContinue | Select-Object Name, Id
  ```

- MSI Afterburner starts RTSS, and both run elevated, so they must be closed from the tray (right-click → Exit) or from an elevated window. Closing only RTSS lets Afterburner start it again.
- `nvcontainer` belongs to the NVIDIA driver and is not FrameView. Leave it running.
- In "What to send back", step 5 has no command output, so ask for the output of the command above instead.
