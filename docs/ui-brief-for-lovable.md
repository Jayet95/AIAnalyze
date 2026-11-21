# AIAnalyze UI brief (for Lovable)

This brief is everything needed to build a new front end for AIAnalyze in Lovable and drop it into the app. The current UI (`AIAnalyze/wwwroot`) works but looks generic; the new one should look like a product people trust with their PC.

Sample responses for every endpoint are in [`ui-fixtures.json`](ui-fixtures.json). Build against them first.

---

## 1. Prompt to paste into Lovable

> Build a single-page React + TypeScript + Tailwind app called **AIAnalyze**, a PC health assistant for Windows that runs locally on the user's PC. It reads JSON from a local API at relative URLs (`/api/...`) and must also run with mock data from a `fixtures.json` file when the API is unreachable or the URL has `?mock=1`.
>
> Screens (hash routes, no server routing): **Health** (home, `#/`), **Live** (`#/live`), **Sessions** (`#/sessions`, `#/sessions/:id`), **System** (`#/system`). Global: a top bar with status pill, alerts bell and an **Ask AI** button that opens a right-side chat drawer; pop-up alert toasts with an "Ask about this" action.
>
> Feel: a calm, trustworthy check-up for your PC, like a good health or banking app, not a gamer RGB dashboard. Plain language, status first, one primary action per screen, generous spacing, dark and light themes following the system setting. Severity colors mean something: green OK, blue info, amber warning, red critical, and nothing is red unless it's a problem.
>
> The chat streams answers from `POST /api/chat` as server-sent events (`data: {"text": "..."}` chunks, `data: {"tool": {"name": "...", "label": "..."}}` when the assistant looks something up on the PC, then `data: {"done": true}` or `data: {"error": "..."}`). User and assistant messages use `dir="auto"` so Hebrew and Arabic display right-to-left.
>
> No external requests at runtime: bundle fonts and icons, no CDNs, no analytics, no remote images. Use the exact API shapes in `fixtures.json`.

Then attach `ui-fixtures.json` and this brief, and iterate screen by screen with sections 3–6.

---

## 2. Hard constraints (the app breaks without these)

| Constraint | Why |
|---|---|
| Static build only: `vite build` producing `index.html` plus assets, `base: "/"` | The Windows service serves the files from its `wwwroot` folder; there is no Node server. |
| Hash routing (`#/sessions/12`), not path routing | The service has no fallback route; a reload on `/sessions/12` would 404. |
| All API calls relative (`fetch("/api/health")`) and same origin | The service listens on `127.0.0.1:5280` only, and the chat and "Check now" endpoints reject requests from other origins. The UI cannot be hosted on lovable.app and call the PC. |
| `POST` bodies sent as `Content-Type: application/json` | The server rejects other content types (415). |
| No runtime requests to the internet | Works offline; nothing about the PC leaves it except chat questions the user sends. Bundle fonts (for example Inter via `@fontsource`) and icons (for example `lucide-react`). |
| Mock mode | Lovable's preview can't reach the PC. Load `fixtures.json` when `?mock=1` is in the URL or the first `/api/status` request fails. |
| Total bundle under ~2 MB | It ships inside the installer and loads from disk. |

---

## 3. Design direction

**Positioning.** "Your PC's doctor": it notices problems, explains them in plain words, and tells you what to do. Users are ordinary Windows users and gamers, not IT staff.

**Principles**

1. **Verdict first.** Every screen opens with the answer: "Your PC looks healthy" or "2 things to look at". Details come after.
2. **Problems are actionable.** Each problem shows what it means and what to do, plus **Ask about this**.
3. **Calm by default.** Passing checks are quiet (muted green dot, collapsed details). Color and weight go to problems.
4. **Numbers support, never lead.** Show a number with its meaning ("86 °C, hot for this GPU"), not raw tables.
5. **Honest states.** When data is missing (no PresentMon, no API key, waiting for sign-in), say so and say how to fix it. Never show fake zeros.

**Look**

- Neutral surfaces with one accent color (suggestion: a deep teal or indigo), rounded 12–16 px cards, soft shadows in light theme, flat in dark theme.
- Type: Inter (bundled), 15 px base, tabular numbers for all metrics.
- Severity tokens: `ok` green, `info` blue, `warning` amber, `critical` red. Use them for dots, badges and left borders of problem cards.
- Motion: subtle (150–250 ms). Streaming chat text, toasts sliding in, cards expanding.
- Charts: a bundled library such as Recharts. Thin lines, no grid clutter, hover readout, gaps where data is missing (null), not lines dropping to zero.

**Accessibility:** WCAG AA contrast in both themes, full keyboard use (Escape closes the drawer and dropdowns, Enter sends a chat message, Shift+Enter adds a new line), `aria-live="polite"` for toasts, visible focus rings.

---

## 4. Screens

### 4.1 Top bar (every screen)

- Logo + "AIAnalyze" + tabs: Health, Live, Sessions, System.
- **Status pill** from `/api/status` → `capture.state`:
  - `Running` → green "Capturing FPS"
  - `WaitingForUser` → amber "Waiting for sign-in"
  - `Restarting` → amber "Restarting capture"
  - `Disabled` → grey "FPS capture off" (not red: the rest of the app works)
  - request fails → red "Service not reachable"
- "Sensors 3s ago" from `lastSampleUtc`.
- **Bell** with unread count (alerts that arrived while the page was open).
- **Ask AI** primary button.

### 4.2 Health (home)

Data: `GET /api/health` → `{ report, running }`.

1. **Verdict card:** big icon by `report.worst` (null = healthy), headline:
   - any `Critical` → "N serious problem(s) need attention"
   - any `Warning` → "N thing(s) to look at"
   - otherwise → "Your PC looks healthy"
   Subtitle "Checked 2 min ago · 12 checks". Buttons: **Check now** (`POST /api/health/run`, then poll `/api/health` every 2 s until `report.atUtc` changes; show "Checking…" while `running` is true) and **Ask AI about it**.
2. **Problems first:** checks with severity `Critical`/`Warning` as prominent cards: title, `summary`, `evidence` list expanded, **Ask about this**.
3. **Suggestions:** `Info` checks (for example "Restart") as lighter cards.
4. **Everything else:** passing checks (severity null) as a compact list with green dots; evidence collapsed behind "What was checked".
5. **Right now:** memory bar (`report.memory.usedGb / totalGb`), drives with free-space bars (`report.drives`), top programs by CPU (`topByCpu`, `cpuPercent` is the share of the whole CPU) and by memory (`topByMemory`, `memoryMb`).
6. Empty state when `report` is null: "Running the first health check… this takes under a minute after the service starts."

Check ids (stable, use them for icons): `crashes`, `hardware-errors`, `display-driver`, `app-crashes`, `services`, `updates`, `disk-space`, `disk-health`, `memory`, `cpu-hogs`, `uptime`, `battery` (laptops only).

### 4.3 Live

Data: `GET /api/live?minutes=5` every 2 s while the tab is visible → `{ gpu, points: [{ t, m }] }`. `t` is Unix seconds; `m` keys are exactly `"CPU °C"`, `"CPU %"`, `"CPU W"`, `"GPU °C"`, `"GPU %"`, `"GPU MHz"`; any value can be null.

- Six metric tiles from the last point: CPU temp, CPU load, CPU power, GPU temp, GPU load, GPU clock. Temperature tiles turn amber at 85 °C (CPU) / 83 °C (GPU) and red at 95 / 93. Null shows "–" with a hint ("needs administrator rights / the PawnIO driver").
- Two charts: temperatures (CPU and GPU) and load (CPU and GPU, 0–100).
- The GPU name above the charts (`gpu`).
- An FPS capture panel from `/api/status.capture`: state message, last frame (time and application), last saved session.

### 4.3a Cooling (card on Live, and on Health under "Right now")

Data: `GET /api/cooling` every 5 s while visible (503 `{ "error": ... }` until the first reading).

```json
{
  "atUtc": "2026-11-21T13:59:59+00:00",
  "gpus": [
    { "name": "NVIDIA GeForce RTX 4070", "fanCount": 2,
      "fans": [ { "name": "GPU Fan 1", "rpm": 1480, "percent": 52 },
                { "name": "GPU Fan 2", "rpm": 1495, "percent": 52 } ],
      "zeroRpm": false }
  ],
  "systemFans": [ { "name": "CPU Fan", "rpm": 1100, "percent": 45 } ],
  "pumps": [ { "name": "AIO Pump", "rpm": 2700, "percent": 100 } ],
  "coolingType": "Liquid",
  "coolingTypeReason": "A pump is running at 2700 RPM, so this PC uses liquid cooling (an AIO or a custom loop)."
}
```

- `coolingType` is `Liquid`, `Air` or `Unknown`. It is **inferred**, not reported by the hardware: show it as "Liquid cooling (detected)" with `coolingTypeReason` in a tooltip or small text. Icons: a droplet for liquid, a fan for air, a question mark for unknown.
- `rpm` and `percent` can each be null. Show "–" for a missing value, never 0.
- **GPU block:** for each GPU, the card name and "2 fans", then one row per fan with a spinning fan icon (spin speed roughly following `percent`; static when `rpm` is 0), RPM in large tabular numbers and percent as a small bar. When `zeroRpm` is true, show "Fans stopped: normal at low temperature (zero-RPM mode)" in neutral grey, not as a warning.
- **System block:** "CPU and case fans" rows the same way; "Pump" rows with a droplet icon.
- **Unknown:** one calm line with `coolingTypeReason` and no empty tables. On laptops this is the normal state.
- Empty `gpus` with a known GPU elsewhere means the card doesn't report its fans; say "This graphics card doesn't report its fans."

### 4.3b Memory (card on System, and the "Memory setup" check on Health)

Data: `GET /api/memory` (updated with each health check; 503 until the first one). Sample in `ui-fixtures.json`.

- **Slot map:** draw the board's slots as a row of `totalSlots` vertical bars. Filled bars for installed sticks, labeled with `slot` (for example `DIMM_A2`), size (`capacityGb` + `type`, "16 GB DDR5"), and speed. Empty bars are outlined and say "Empty". Windows lists only installed sticks, so draw empty slots as unnamed placeholders.
- **Channels:** colour or group sticks by `channel` (A, B, or a controller-qualified value such as `0A`/`1A`); null means unknown. Two sticks with the same channel on a 4-slot board is the single-channel problem the check warns about.
- **Speed:** show `configuredSpeedMts` as the main number ("4800 MT/s"). When `ratedSpeedMts` is higher, add "reports 6000" in muted text.
- **Model:** `manufacturer` + `partNumber` in small text. No serial numbers are sent.
- **Check:** `check` uses the same shape as the Health checks (severity null, Info, Warning). Show it under the slot map with **Ask about this** when it has a severity.
- **Laptops:** soldered memory often reports names like `Controller0-ChannelA/B-DIMM0` and `totalSlots` equal to the stick count; that's normal.

### 4.4 Sessions

List data: `GET /api/sessions?limit=200` every 30 s. Each row: application, start time, duration (`durationSeconds`), median FPS, worst-severity badge (`worst`, may be null).

Detail data: `GET /api/sessions/{id}`:
- Header: application, start time, GPU used (`gpu`), **Ask about this session**.
- Stat tiles: median FPS, "Your usual" (`baseline.fps`, null → "–"), duration, frames.
- `feedback` (string or null): the assistant's own review, as a highlighted card titled "Assistant's feedback" (render simple Markdown: bold, lists, paragraphs).
- `findings` as cards by severity, with a friendly title per `kind`:

  | kind | Title |
  |---|---|
  | FpsDrop | Below your usual FPS |
  | CpuHot | CPU running hot |
  | GpuHot | GPU running hot |
  | GpuThrottling | GPU throttling |
  | GpuClockVaries | GPU clock varied |
  | FpsCapped | Frame rate capped |
  | GpuBound | Limited by the GPU |
  | NotGpuBound | Not limited by the GPU |
  | Stutter | Stutter |

- `baseline.message` as a muted line.
- Charts from `rows` (`t`, `fps`, `m`): FPS, temperatures, load, GPU clock. Highlight seconds where FPS < half the median.
- Empty list: "No sessions yet. Play a game for a few minutes; a session is saved 30 seconds after it closes."

### 4.5 System

Data: `GET /api/specs` (404 before the first start). Cards: computer (`computerManufacturer computerModel`), processor (cores, threads, max clock), memory, each GPU with driver version, motherboard + BIOS, Windows version and build.

### 4.6 Assistant drawer

- Opens from **Ask AI**, from any "Ask about this", and from a session ("Ask about this session" sets `sessionId`).
- On open, `GET /api/chat/status` → `{ configured }`. If false, show the setup card instead of the input: "The assistant needs your own Anthropic API key. Rerun the installer as Administrator with `-SetApiKey` and paste your key when asked. The key stays on this PC in a file only Windows administrators can read." Send stays disabled.
- Empty conversation: one line of explanation and suggestion chips: "Is anything wrong with my PC right now?", "Why is my PC slow?", "Has my PC crashed recently, and why?", "Are my temperatures OK?", "Why did my FPS drop in the last game?".
- Context line under the title: "Uses this PC's measurements", or "Includes session 12 in detail" when `sessionId` is set.
- Messages: user right, assistant left, `dir="auto"`. Stream tokens into the assistant bubble with a blinking caret. Render simple Markdown. **New** clears the conversation (and aborts a running answer). Keep the conversation in `sessionStorage`.
- Send: `POST /api/chat` with `{ "messages": [{ "role": "user"|"assistant", "content": "..." }], "sessionId": 12 | null }`. Last message must be the user's; at most 20 messages, each 1–4000 characters.
- Response: `text/event-stream`. Split on blank lines; each event is `data: <json>`: `{ "text": "..." }` (append), `{ "tool": { "name": "search_event_log", "label": "Searching the Event Log (crash and unexpected shutdown, last 7 days)" } }` (the assistant is checking something on this PC: show the label as a small step line above the answer, with a spinner until the next event, then a check mark; an answer can have several), `{ "done": true }` (finished), `{ "error": "..." }` (show as an error bubble). Before streaming starts, errors come back as JSON `{ "error": "..." }` with status 400 (bad input), 403 (wrong origin) or 503 (no API key).

### 4.7 Alerts

- `GET /api/alerts?after=<lastId>` every 10 s → newest first: `{ id, atUtc, kind, severity, message, sessionId }`.
- On first load, list existing alerts under the bell without pop-ups. New alerts after that: toast (auto-dismiss after 20 s) with **Dismiss** and **Ask about this** (opens the chat with the alert text, and `sessionId` when present), plus the bell count.
- `kind` values: `Temperature`, `Feedback`, session finding kinds (table above), and `Health:<check id>`.

---

## 5. States to design

| State | Where | What to show |
|---|---|---|
| Service not reachable | Everywhere | Red pill and a full-page message: "AIAnalyze service isn't running. Start it from Services, or reinstall." |
| First health check pending | Health | Skeleton cards and "Running the first health check…" |
| FPS capture off (no PresentMon) | Live, Sessions | Banner with `capture.message`; the rest works |
| Waiting for sign-in | Live | Info banner |
| No sessions | Sessions | Friendly empty state |
| No API key | Assistant | Setup card |
| Sensor value null | Live, charts | "–" and a tooltip with the likely reason |
| Long text from the server | Everywhere | Wraps; messages are written for users and should be shown as-is |

---

## 6. Polling and refresh

| Endpoint | When |
|---|---|
| `/api/status` | every 5 s |
| `/api/assistant/overview` | every 5 s, Assistant tab visible only |
| `/api/live?minutes=5` | every 2 s, Live tab visible only |
| `/api/health` | every 30 s on Health; every 2 s after **Check now** until the report changes |
| `/api/alerts?after=` | every 10 s |
| `/api/sessions` | every 30 s on Sessions |

Pause polling when `document.hidden` is true.

---

## 6a. Update: accounts, privacy and feedback (send this to Lovable as a follow-up)

> Add accounts, a privacy setting and feedback votes to the AIAnalyze dashboard, using these local endpoints (same rules as before: relative URLs, JSON bodies, mock data in fixtures when the service is unreachable).
>
> **Account** (button in the top bar, left of Ask AI)
> - `GET /api/account` → `{ "signInConfigured": true, "signedIn": true, "email": "player@example.com", "name": "Player One", "assistant": "Account" | "ApiKey" | "Off", "quota": { "questionsToday": 4, "dailyLimit": 30 } | null }`
> - Signed out and `signInConfigured`: button "Sign in". Signed in: name or email; menu shows email, "Assistant questions today 4 of 30" with a thin bar, Settings, Sign out. When `signInConfigured` is false, the button just opens Settings.
> - Sign in is a full-page navigation to `/auth/sign-in` (a link, not fetch). The browser comes back to `/#signed-in`: show a toast "Signed in as …" and clear the hash.
> - `POST /api/account/sign-out` → `{ "signedIn": false }`.
>
> **Assistant setup state**: `GET /api/chat/status` → `{ "configured": false, "mode": "Off" | "Account" | "ApiKey", "signInAvailable": true }`. When not configured and `signInAvailable`, the chat drawer shows "Sign in to use the assistant. It's free during the beta." with a Sign in link to `/auth/sign-in`, plus a small "Advanced: use your own Anthropic API key with the installer's -SetApiKey option." When `signInAvailable` is false, keep the current API-key setup card.
>
> **Privacy** (Settings dialog, and a one-time banner on Health)
> - `GET /api/privacy` → `{ "consent": true | false | null, "serverConfigured": true, "preview": { "deviceId": "…", "snapshot": { … }, "feedback": [ … ] } }`
> - `POST /api/privacy` with `{ "share": true | false }`.
> - When `consent` is null, show a calm banner at the top of Health: "Help make AIAnalyze better? Share anonymous diagnostics: hardware models, driver versions, which checks failed and how games ran. Never names of your files or programs, never chat content." Buttons: What is shared? (opens Settings with the preview expanded), No thanks, Share.
> - Settings has a toggle "Share anonymous diagnostics to help improve AIAnalyze" with that explanation, a note "This build has no server set up yet, so nothing is sent" when `serverConfigured` is false, and "Show exactly what is shared" revealing `preview` as formatted JSON.
>
> **Feedback votes**: "Helpful? 👍 👎" on each Health problem or suggestion (`target: "health"`, `kind`: the check id), each session finding (`target: "finding"`, `kind`: the finding kind), and each finished assistant answer (`target: "answer"`, `kind: "chat"`). Send `POST /api/feedback` with `{ "target": "...", "kind": "...", "helpful": true | false }`; afterwards replace the buttons with "Thanks for the feedback". Votes are kept on the PC and uploaded only when sharing is on, so no extra wording is needed.

## 6b. Update: the assistant becomes the home screen (send this to Lovable as a follow-up)

> Make the assistant the center of AIAnalyze. Users open it to hear what is wrong with their PC and what to do; the other screens become details. Same rules as before: relative URLs, mock data from fixtures when the service is unreachable.
>
> **New first tab "Assistant", shown on open.** Desktop layout: two columns. Left (about 45%): a status card and a "What I'm watching" list. Right (about 55%): the chat, docked in the page and as tall as the window (sticky), using the same chat component as the drawer. Below 900 px wide, stack them: status card, list, then chat. On the other tabs the chat stays the slide-in drawer opened from **Ask AI**.
>
> **Data**: `GET /api/assistant/overview` every 5 s while the tab is visible:
>
> ```json
> {
>   "atUtc": "2026-11-21T19:04:05Z",
>   "verdict": "attention",
>   "headline": "2 things need attention: FPS drop in cyberpunk2077.exe; C: filling up.",
>   "playing": { "application": "cyberpunk2077.exe", "fps": 58.4 },
>   "now": { "cpuTemp": 71, "cpuLoad": 64, "gpuTemp": 79, "gpuLoad": 98, "memoryPercent": 71.5 },
>   "items": [
>     { "source": "live", "id": "fps-drop", "severity": "Warning", "title": "FPS drop in cyberpunk2077.exe",
>       "detail": "cyberpunk2077.exe is running at 58 FPS, well below its usual 96 FPS. The GPU is fully loaded, so this part of the game is simply heavier.",
>       "active": true, "sinceUtc": "2026-11-21T19:03:20Z", "question": "Why did my FPS drop in cyberpunk2077.exe just now?" },
>     { "source": "trend", "id": "drive-filling-c", "severity": "Warning", "title": "C: filling up",
>       "detail": "C: lost about 2.4 GB a day over the last two weeks and has 61 GB left: full in about 25 days at this pace.",
>       "active": true, "sinceUtc": null, "question": "My C: drive is filling up fast. What is taking the space?" },
>     { "source": "health", "id": "restart", "severity": "Info", "title": "Restart",
>       "detail": "The PC has been running for 16 days without a restart.", "active": true, "sinceUtc": null,
>       "question": "Tell me more about \"Restart\": what does it mean and what should I do?" }
>   ],
>   "suggestedQuestions": ["Why did my FPS drop in cyberpunk2077.exe just now?", "My C: drive is filling up fast. What is taking the space?", "Is anything wrong with my PC right now?", "Why does my PC feel slow?"],
>   "healthCheckedUtc": "2026-11-21T18:58:00Z",
>   "trendsCheckedUtc": "2026-11-21T18:00:00Z"
> }
> ```
>
> - **Status card**: a big verdict icon (good = green check, attention = amber, problem = red), `headline` as the title, "Health checked 6 min ago · watching live" under it, then small chips: "Playing cyberpunk2077.exe · 58 FPS" (accent color, only when `playing`), "CPU 71 °C · 64%", "GPU 79 °C · 98%", "RAM 72%" (skip null values).
> - **What I'm watching**: one row per item, in the given order. Severity dot, a small uppercase label (`source`: live → a red pulsing "● LIVE NOW" while `active`, health → "Health check", trend → "Trend"; add "· ended" when `active` is false and fade the row; add "· since 2 min ago" from `sinceUtc`), the title, the detail in muted text, and an **Ask** button that sends `question` to the chat. Empty list: "Nothing to report. I'll tell you as soon as something comes up, also while you play."
> - **Chat greeting**: when the conversation is empty, the first assistant bubble is "Good evening. " (by local time) + `headline` + "Ask me anything about this PC, or pick a question below.", and the suggestion chips are `suggestedQuestions`. This greeting is local: it costs nothing and is not sent to the model.
> - **Tool steps** in answers (already in 4.6): show each `tool` event's label as a small step line above the answer.
> - Optional detail views: `GET /api/issues` (live issues only), `GET /api/trends` → `{ atUtc, problems: [{ id, severity, title, detail, question }], history: [{ day: "2026-11-20", values: { "gpu_temp_load": 78.5, "cpu_temp_idle": 41, "drive_free_gb:C:": 61.2 } }] }` for a "Trends" card with small line charts of temperatures under load and free space per drive (45 days).
> - Alerts now also have kinds `Live` (message "Title: detail", raised while the problem happens) and `Trend`; show them like other alerts.

## 7. Putting the build into the app

1. Export the Lovable project to GitHub (or download it).
2. In `vite.config.ts`, set `base: "/"` and, for local development, proxy the API:

   ```ts
   server: { proxy: { "/api": "http://127.0.0.1:5280" } }
   ```

3. `npm install` then `npm run build`.
4. Replace the contents of `AIAnalyze/wwwroot/` with the contents of `dist/` (keep `fixtures.json` out of the production build, or load it only in mock mode).
5. Reinstall (`scripts\install-service.ps1`) or, for a quick look, run the app from the build folder and open <http://127.0.0.1:5280/>.

The API does not change when the UI does, so the UI can be rebuilt any time.
