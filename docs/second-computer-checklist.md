# Second computer: checklist

## Round 14 additions: the live assistant

After reinstalling (steps 2 and 3), test these:

- **Assistant home.** The window opens on the new **Assistant** tab. On the left: a verdict, a one-line headline, chips with CPU, GPU and RAM, and a "What I'm watching" list. On the right: the chat, part of the page. Click **Ask** on a row and check that its question goes into the chat. (The Lovable UI gets this layout later; the built-in UI has it now.)
- **Live issues while playing.** Play a game for at least 3 sessions of 5 minutes or more, so it has a usual FPS. In the next session, force a drop for 30 seconds or more: cap the FPS in the game or the NVIDIA control panel to about half, or run a heavy program next to it. Within about 30 seconds you should get:
  - a Windows notification titled **FPS drop in <game>.exe**, and
  - a **● LIVE NOW** row on the Assistant tab.

  Notifications don't show over an exclusive fullscreen game, or when Windows "Do not disturb while gaming" is on, so try borderless window mode if none appears. Then read the live issues:

  ```powershell
  (Invoke-WebRequest -UseBasicParsing http://127.0.0.1:5280/api/issues).Content
  ```

- **Memory pressure (optional).** Open many browser tabs until RAM use stays above 92% for a minute: **Memory almost full** should appear.
- **Trends.** About 2 minutes after the service starts, it records today's figures and checks trends. They need a week or more of history before they can say anything, so for now just send the output:

  ```powershell
  (Invoke-WebRequest -UseBasicParsing http://127.0.0.1:5280/api/trends).Content
  (Invoke-WebRequest -UseBasicParsing http://127.0.0.1:5280/api/assistant/overview).Content
  ```

- **With your own key (step 7):** ask "When did my PC start crashing?" or "Compare my last two sessions of <game>". The answer should show steps such as "Searching the Event Log…" above it, and quote numbers from this PC.
- **Lightweight check.** In Task Manager, AIAnalyze.exe should stay around 1% CPU or less while idle (the live monitor runs every 5 seconds).

- **Email alerts.** Settings has a new Notifications section. Until sign-in and the server are set up, the toggle is disabled and says why. Check that the text is clear.

## Round 13 additions (test these too)

- **AIAnalyze window app:** after reinstalling, **AIAnalyze** in the Start menu opens its own window (not the browser), with an icon next to the clock. Closing the window keeps the icon; right-click it for Open, Check health now, Start with Windows, Quit. Trigger an alert (for example **Check now** with a known warning) and check that a Windows notification appears while the window is closed. If it fails, send `%LocalAppData%\AIAnalyze\desktop.log`.
- **UI build:** the installer now uses bun if present, otherwise npm, retries once from a clean `node_modules`, and prints the real error. Send the output if it says `THE NEW UI WAS NOT INSTALLED`.
- **Round 12 FurMark case:** browsers and launchers no longer become sessions. While FurMark runs, read `(Invoke-WebRequest -UseBasicParsing http://127.0.0.1:5280/api/status).Content` and send the `capture.applications` list: it shows which program PresentMon attributes FurMark's frames to.

This round tests the current build: the new UI from Lovable, whole-PC health checks, cooling and RAM details, the opt-in sharing banner and feedback buttons. Sign-in is not set up yet (it waits for Clerk), so the account button opens Settings only.

Commands run in PowerShell. Steps marked **Administrator** need an elevated window. Do not paste any API key into a chat or a file.

## 1. Prerequisites

- **Node.js LTS** (needed to build the new UI). Check with `node --version`. If it is missing, install it from nodejs.org. Without it, the installer keeps the old built-in UI.
- **PawnIO** driver (CPU temperature, power, and motherboard fans). Check with `Get-Service PawnIO`. It is already installed on the first computer.
- PresentMon stays where it is (`C:\Program Files\AIAnalyze\PresentMon`).

## 2. Get the latest code, including the UI

The UI is now a separate repository linked as a submodule, so a plain `git pull` is not enough:

```powershell
git pull
git submodule update --init ui
```

## 3. Reinstall (Administrator)

```powershell
powershell -ExecutionPolicy Bypass -File scripts\install-service.ps1
```

Watch the output for:

- `Building the UI from ...` then `Installed the new UI.` (or a warning that the built-in UI is used).
- `Microsoft Defender found nothing in C:\Program Files\AIAnalyze.` (or a list of detections, which must not be excluded).
- No warning about PawnIO.

The UI opens in the browser at the end.

## 4. Check the new UI

Open **AIAnalyze** from the Start menu, or <http://127.0.0.1:5280/>.

- The UI is the new Lovable design and is **not** labelled "Sample" (that label means it fell back to fixture data).
- **Health:** a verdict at the top and about 13 checks, with problems first. Click **Check now** and see the time update.
- **Sharing banner** on Health ("Help make AIAnalyze better?"). Click **What is shared?** and look at the preview, then choose **No thanks** or **Share** (nothing is sent: no server is set up yet).
- **Helpful / Not helpful** on a check: click one and see "Thanks".
- **Live:** temperatures, loads and power with values; the RTX 5080 is the GPU shown.
- **Cooling:** the RTX 5080's fans with RPM and %, CPU and case fans, and the cooling type (Liquid if an AIO pump is detected, otherwise Air).
- **System / Memory:** each RAM stick with its slot, channel, size, type and speed, and the "Memory setup" result.

## 5. Read the raw data (to compare with the UI)

```powershell
dotnet "C:\Program Files\AIAnalyze\AIAnalyze.dll" --health
(Invoke-WebRequest -UseBasicParsing http://127.0.0.1:5280/api/cooling).Content
(Invoke-WebRequest -UseBasicParsing http://127.0.0.1:5280/api/memory).Content
```

## 6. Play for 5 minutes, close the game, wait 1 minute

On **Sessions**, open the game's session: FPS chart, findings, and Helpful / Not helpful on each finding.

## 7. Optional: the assistant with your own key (Administrator)

Only if you want to test the assistant before the account server exists. The key is billed to its owner.

```powershell
powershell -ExecutionPolicy Bypass -File scripts\install-service.ps1 -SetApiKey
```

Ask "Is anything wrong with my PC right now?" and check the answer quotes this PC's health checks, cooling and RAM.

## What to send back

- The output of step 3 (UI build, Defender scan, PawnIO).
- Screenshots of Health, Cooling and Memory in the new UI.
- The output of the three commands in step 5.
- Anything that looks wrong or empty, with the Event Log:

  ```powershell
  Get-WinEvent -FilterHashtable @{LogName='Application'; ProviderName='AIAnalyze'} -MaxEvents 40 | Format-List TimeCreated, Message
  ```
