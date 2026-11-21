# Second computer: checklist

This build adds the local UI, session findings, the fixes from Rounds 10 and 11 (GPU choice, AMD CPU power, waiting for sign-in, false stutter, Event Log noise, database size), automatic cleanup of stale ETW trace sessions, alerts, and the assistant (chat and automatic feedback). Commands run in PowerShell. Steps marked **Administrator** need an elevated window.

## 1. Pull the latest code

```powershell
git pull
```

## 2. Reinstall (Administrator)

PresentMon is already in `C:\Program Files\AIAnalyze\PresentMon` on the first computer, and the reinstall keeps it:

```powershell
powershell -ExecutionPolicy Bypass -File scripts\install-service.ps1
```

On a new computer, add the path to PresentMon the first time:

```powershell
powershell -ExecutionPolicy Bypass -File scripts\install-service.ps1 -PresentMonPath C:\Path\To\PresentMon.exe
```

The service must run as LocalSystem, which is the default. The script opens the UI when it finishes.

The existing `C:\ProgramData\AIAnalyze\appsettings.json` still works. It is no longer needed: with PresentMon in the `PresentMon` folder, the defaults are the same as the values in it.

## 3. Check the UI

Open **AIAnalyze** from the Start menu, or <http://127.0.0.1:5280/>.

- **Live**: all six cards show numbers. The top-right pill says **Capturing FPS**. The GPU named above the temperature chart is the RTX 5080, not the integrated Radeon.
- **System**: the PC's model, CPU, RAM and both GPUs with driver versions.

## 4. Play for at least 5 minutes, then close the game and wait 1 minute

A session is saved 30 seconds after the game stops, and only with at least 300 frames.

## 5. Check the session

On the **Sessions** tab, open the game's session.

- The GPU line under the title is the RTX 5080.
- The GPU % and GPU clock charts show real values, not 0 and 600.
- The CPU W column has values (`--timeline <id>` shows it).
- The findings make sense for what you played, for example "Limited by the GPU" in a demanding game, or "Frame rate capped" with V-Sync on.

## 6. Reboot test

Restart the PC, sign in, wait a minute, and play again. Capture should work without reinstalling. Before you sign in, the Event Log should show one `Waiting for a user to sign in` warning, not an error every 30 seconds.

## 7. Assistant (optional, uses your own API key)

Rerun the installer with `-SetApiKey` (Administrator) and paste your own Anthropic API key when asked. Each question is billed to that key.

```powershell
powershell -ExecutionPolicy Bypass -File scripts\install-service.ps1 -SetApiKey
```

- Click **Ask AI** and ask "What limits my FPS?" with a session open. The answer should quote numbers from that session.
- Under the bell, open an alert and click **Ask about this**.
- After a session that ends with warnings, the session shows **Assistant's feedback** within a minute or two.

Without a key, the chat panel should say the assistant needs your own key, and nothing else should change.

## What to send back

- Screenshots of the Live tab and of one session.
- Whether each point in steps 3, 5, 6 and 7 matched, and one example question with its answer.
- The Event Log, if anything did not match:

  ```powershell
  Get-WinEvent -FilterHashtable @{LogName='Application'; ProviderName='AIAnalyze'} -MaxEvents 40 | Format-List TimeCreated, Message
  ```
