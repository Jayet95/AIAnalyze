# Fix: FPS capture stops right after the service starts

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
