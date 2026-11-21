# Second computer: checklist

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
