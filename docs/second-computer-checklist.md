# Second computer: checklist

Commands run in PowerShell. Steps marked **Administrator** need an elevated window.

## 1. Pull the latest code

```powershell
git pull
```

## 2. Install the latest build as a service (Administrator)

```powershell
powershell -ExecutionPolicy Bypass -File scripts\install-service.ps1
```

The script publishes the new build, recreates the service, and starts it.

## 3. Check the service

```powershell
Get-Service AIAnalyze
```

Expected: `Running`.

## 4. Check the PresentMon override file

`C:\ProgramData\AIAnalyze\appsettings.json` must hold the PresentMon path. It survives reinstalls. If it is missing, create it as in the README (second computer test, step 5).

## 5. Make sure no other PresentMon is running

RivaTuner (RTSS), NVIDIA FrameView and AMD Adrenalin all bundle PresentMon, and their presence changed the result in the earlier test. Check what is running:

```powershell
Get-Process RTSS, RTSSHooksLoader64, MSIAfterburner, FrameView*, RadeonSoftware -ErrorAction SilentlyContinue | Select-Object Name, Id
```

Close them all before the first test. MSI Afterburner starts RTSS again, so close Afterburner from the tray (right-click, Exit), not only RTSS. Leave `nvcontainer` running, because it belongs to the NVIDIA driver.

## 6. Play a game for at least 5 minutes, then close it and wait 1 minute

A session is saved only after 30 seconds without frames, and only with at least 300 frames. Running the next step right after quitting shows nothing.

## 7. Read the warnings in the Event Log

```powershell
Get-WinEvent -FilterHashtable @{LogName='Application'; ProviderName='AIAnalyze'} -MaxEvents 40 | Format-List TimeCreated, Message
```

The Event Log keeps only Warning and above, so these lines are the ones to look for:

- `PresentMon: ...` is PresentMon's own error output.
- `PresentMon output before header (skipped): ...` shows the first line PresentMon printed.
- `PresentMon exited. Restarting ...` means PresentMon stopped.

Session saves are Information and do not appear here. Check them with step 8.

## 8. Check sessions and the timeline

```powershell
dotnet "C:\Program Files\AIAnalyze\AIAnalyze.dll" --sessions
dotnet "C:\Program Files\AIAnalyze\AIAnalyze.dll" --timeline <id>
```

Replace `<id>` with the game's id from the `--sessions` output.

## First computer only (Administrator): delete the junk test sessions

Skip this on a new machine. A count of 0 is expected there.
Run this section in an elevated window. The database is owned by the service account, and `Stop-Service` needs Administrator.

The 9,766 junk sessions from the 30-second run exist only on the first computer. Stop the service, count, delete, and start it again. Stop the service first, so the delete does not compete with it for the database.

```powershell
Stop-Service AIAnalyze
dotnet "C:\Program Files\AIAnalyze\AIAnalyze.dll" --count-sessions 1795165768000 1795165787000
```

If the count is about 9,766:

```powershell
dotnet "C:\Program Files\AIAnalyze\AIAnalyze.dll" --delete-sessions 1795165768000 1795165787000
Start-Service AIAnalyze
```

## What to send back

- The output of step 3, and the output of the process check in step 5.
- The Event Log output from step 7.
- The `--sessions` output from step 8.
- For the first computer, the counts from the cleanup.

## Known open problem

If no frames arrive while the service runs alone, PresentMon may not see the rendering app from session 0. See `docs/fix-presentmon-capture.md` on the branch `fix/presentmon-capture-notes`, section 4.

## Test: run the service under an administrator account

Use this only after the clean test (step 5 and 6) shows no frames. Run it with all other PresentMon-related tools closed.

**Note:** Windows services run in session 0 whatever account they use. This test checks whether the account matters. It may not fix the problem, and a negative result is still useful.

1. Reinstall the service under your admin account. The script prompts for the credential:

   ```powershell
   powershell -ExecutionPolicy Bypass -File scripts\install-service.ps1 -ServiceCredential (Get-Credential)
   ```

   Enter the account as `.\<username>` (for a local account) or `DOMAIN\<username>`.

2. Check the account:

   ```powershell
   Get-CimInstance Win32_Service -Filter "Name='AIAnalyze'" | Select-Object Name, StartName, State
   ```

3. Play the game for 5 minutes, close it, and wait 1 minute.

4. Check the Event Log and sessions (steps 7 and 8 above). Send both back, together with the output of step 2.

To return to LocalSystem, reinstall without `-ServiceCredential`.

## Test: user-session mode (PresentMon runs in your logged-on session)

Use this after the admin-account test. It needs the service to run as **LocalSystem**, which is the default. Reinstall without `-ServiceCredential` if the service runs as another account.

1. Check that PresentMon accepts the output flag:

   ```powershell
   & "C:\Program Files\AIAnalyze\PresentMon\PresentMon.exe" --help | Select-String output_stdout
   ```

   If nothing is printed, send the full `--help` output instead of continuing.

2. Add `"UserSession": true` to the `PresentMon` section of `C:\ProgramData\AIAnalyze\appsettings.json`, then restart the service:

   ```json
   "PresentMon": {
     "ExecutablePath": "C:\\Program Files\\AIAnalyze\\PresentMon\\PresentMon.exe",
     "Arguments": "--no_console_stats --v1_metrics --session_name AIAnalyze --stop_existing_session",
     "UserSession": true
   }
   ```

   The service runs PresentMon with `--output_stdout` and redirects its output to `C:\ProgramData\AIAnalyze\presentmon.csv` through `cmd.exe`. Leave the arguments as above.

3. Make sure you are logged on at the console, close other PresentMon tools, and play for 5 minutes, then close the game and wait 1 minute.

4. Send back the Event Log (step 7), the `--sessions` output (step 8), and whether `C:\ProgramData\AIAnalyze\presentmon.csv` grows while the game runs.

Event Log lines to expect: `Started PresentMon (pid ...) in console session ...`. If the start fails, the error includes the Win32 code, which is the important part.
