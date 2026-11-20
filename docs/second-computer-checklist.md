# Second computer: checklist

Work through these in order. Commands run in PowerShell. Steps 2 and 4 need an elevated (Administrator) window.

The installed service still runs the old build until step 4 reinstalls it.

## 1. Pull the latest code

```bash
git pull
```

## 2. Stop the service and delete the junk test sessions

```bash
Stop-Service AIAnalyze
sqlite3 C:\ProgramData\AIAnalyze\telemetry.db "SELECT COUNT(*) FROM sessions WHERE start_utc BETWEEN 1795165768000 AND 1795165787000;"
```

If the count is about 9,766, delete the sessions and their per-second FPS rows:

```bash
sqlite3 C:\ProgramData\AIAnalyze\telemetry.db "DELETE FROM fps_seconds WHERE session_id IN (SELECT id FROM sessions WHERE start_utc BETWEEN 1795165768000 AND 1795165787000); DELETE FROM sessions WHERE start_utc BETWEEN 1795165768000 AND 1795165787000;"
```

Delete `fps_seconds` first, because it refers to `sessions`.

If `sqlite3` is not installed, run `Get-Command sqlite3` and send the output.

## 3. Check the PresentMon override file

The file `C:\ProgramData\AIAnalyze\appsettings.json` must hold the PresentMon path. It survives reinstalls. If it does not exist, create it as in the README (second computer test, step 5).

## 4. Reinstall the service with the latest code

```bash
powershell -ExecutionPolicy Bypass -File scripts\install-service.ps1
```

The script publishes the new build, recreates the service, and starts it.

## 5. Check that the service is running

```bash
Get-Service AIAnalyze
```

Expected output: `Running`.

## 6. Play a game for at least 5 minutes

Five minutes gives enough frames for a session to be saved.

## 7. Send the service log

```bash
Get-EventLog -LogName Application -Source AIAnalyze -Newest 40 | Format-List TimeGenerated, Message
```

Look for:

- `PresentMon: ...` lines. These are PresentMon's own error output. An error here explains missing frames.
- `PresentMon output before header (skipped): ...`. This shows the first line PresentMon printed.

## 8. Check sessions and the timeline

```bash
dotnet "C:\Program Files\AIAnalyze\AIAnalyze.dll" --sessions
dotnet "C:\Program Files\AIAnalyze\AIAnalyze.dll" --timeline <id>
```

Replace `<id>` with the game's id from the `--sessions` output.

## What to send back

- The output of step 2's count query.
- The output of step 5.
- The Event Log output from step 7.
- The `--sessions` output from step 8.

## Known open problem

If no frames arrive while the service runs alone, PresentMon may not see the rendering app from session 0. See `docs/fix-presentmon-capture.md` on the branch `fix/presentmon-capture-notes`, section 4.
