"use strict";

// Metric labels as the API returns them (keys of each row's "m" object).
const M = {
  cpuTemp: "CPU °C",
  cpuLoad: "CPU %",
  cpuPower: "CPU W",
  gpuTemp: "GPU °C",
  gpuLoad: "GPU %",
  gpuClock: "GPU MHz",
};

// Card thresholds match the service's default thermal limits.
const CARDS = [
  { key: M.cpuTemp, label: "CPU temp", unit: "°C", max: 100, warn: 85, crit: 95, digits: 0 },
  { key: M.cpuLoad, label: "CPU load", unit: "%", max: 100, digits: 0 },
  { key: M.cpuPower, label: "CPU power", unit: "W", max: 200, digits: 0 },
  { key: M.gpuTemp, label: "GPU temp", unit: "°C", max: 100, warn: 83, crit: 93, digits: 0 },
  { key: M.gpuLoad, label: "GPU load", unit: "%", max: 100, digits: 0 },
  { key: M.gpuClock, label: "GPU clock", unit: "MHz", max: 3200, digits: 0 },
];

const css = (name) => getComputedStyle(document.documentElement).getPropertyValue(name).trim();
const $ = (sel) => document.querySelector(sel);

function el(tag, attrs = {}, ...children) {
  const node = document.createElement(tag);
  for (const [k, v] of Object.entries(attrs)) {
    if (k === "class") node.className = v;
    else if (k.startsWith("on")) node.addEventListener(k.slice(2), v);
    else node.setAttribute(k, v);
  }
  for (const child of children.flat()) {
    if (child == null) continue;
    node.append(child instanceof Node ? child : document.createTextNode(String(child)));
  }
  return node;
}

async function getJson(url) {
  const res = await fetch(url, { cache: "no-store" });
  if (!res.ok) throw new Error(`${url}: ${res.status}`);
  return res.json();
}

const fmt = {
  num: (v, d = 0) => (v == null || Number.isNaN(v) ? "–" : Number(v).toFixed(d)),
  time: (unixSec) => new Date(unixSec * 1000).toLocaleTimeString([], { hour: "2-digit", minute: "2-digit", second: "2-digit" }),
  date: (iso) => new Date(iso).toLocaleString([], { month: "short", day: "numeric", hour: "2-digit", minute: "2-digit" }),
  duration: (sec) => {
    const s = Math.round(sec);
    if (s < 60) return `${s}s`;
    const m = Math.floor(s / 60);
    if (m < 60) return `${m}m ${s % 60}s`;
    return `${Math.floor(m / 60)}h ${m % 60}m`;
  },
  ago: (iso) => {
    if (!iso) return "never";
    const s = Math.max(0, Math.round((Date.now() - new Date(iso).getTime()) / 1000));
    if (s < 60) return `${s}s ago`;
    if (s < 3600) return `${Math.floor(s / 60)}m ago`;
    return `${Math.floor(s / 3600)}h ago`;
  },
};

// ---------------------------------------------------------------- charts

/**
 * Draws a responsive SVG line chart with a hover readout.
 * series: [{ label, color, points: [[unixSec, value|null], ...] }]
 */
function lineChart(container, series, { unit = "", yMin, yMax, height = 200 } = {}) {
  container.replaceChildren();
  const W = 800, H = height, padL = 44, padR = 12, padT = 10, padB = 24;
  const svgNs = "http://www.w3.org/2000/svg";
  const svg = document.createElementNS(svgNs, "svg");
  svg.setAttribute("viewBox", `0 0 ${W} ${H}`);
  const add = (tag, attrs) => {
    const n = document.createElementNS(svgNs, tag);
    for (const [k, v] of Object.entries(attrs)) n.setAttribute(k, v);
    svg.append(n);
    return n;
  };

  const all = series.flatMap((s) => s.points.filter((p) => p[1] != null));
  if (all.length < 2) {
    add("text", { x: W / 2, y: H / 2, "text-anchor": "middle", class: "nodata" }).textContent = "No data yet";
    container.append(svg);
    return;
  }

  const tMin = Math.min(...all.map((p) => p[0]));
  const tMax = Math.max(...all.map((p) => p[0]));
  let lo = yMin ?? Math.min(...all.map((p) => p[1]));
  let hi = yMax ?? Math.max(...all.map((p) => p[1]));
  if (yMin == null) lo = Math.floor(lo * 0.9);
  if (yMax == null) hi = Math.ceil(hi * 1.08);
  if (hi <= lo) hi = lo + 1;

  const x = (t) => padL + ((t - tMin) / Math.max(1, tMax - tMin)) * (W - padL - padR);
  const y = (v) => H - padB - ((v - lo) / (hi - lo)) * (H - padT - padB);

  for (let i = 0; i <= 4; i++) {
    const v = lo + ((hi - lo) * i) / 4;
    add("line", { x1: padL, x2: W - padR, y1: y(v), y2: y(v), class: i === 0 ? "axis" : "grid" });
    add("text", { x: padL - 6, y: y(v) + 4, "text-anchor": "end" }).textContent = `${Math.round(v)}`;
  }
  for (const t of [tMin, (tMin + tMax) / 2, tMax]) {
    add("text", { x: x(t), y: H - 6, "text-anchor": t === tMin ? "start" : t === tMax ? "end" : "middle" })
      .textContent = fmt.time(Math.round(t));
  }

  for (const s of series) {
    // Break the line where values are missing instead of drawing across gaps.
    let d = "";
    let pen = false;
    for (const [t, v] of s.points) {
      if (v == null) { pen = false; continue; }
      d += `${pen ? "L" : "M"}${x(t).toFixed(1)},${y(v).toFixed(1)}`;
      pen = true;
    }
    if (d) add("path", { d, class: "line", stroke: s.color });
  }

  const cursor = add("line", { x1: 0, x2: 0, y1: padT, y2: H - padB, class: "cursor", visibility: "hidden" });
  const tip = $("#tooltip");
  const times = [...new Set(all.map((p) => p[0]))].sort((a, b) => a - b);

  svg.addEventListener("mousemove", (ev) => {
    const box = svg.getBoundingClientRect();
    const px = ((ev.clientX - box.left) / box.width) * W;
    const t = tMin + ((px - padL) / (W - padL - padR)) * (tMax - tMin);
    let nearest = times[0];
    for (const tt of times) if (Math.abs(tt - t) < Math.abs(nearest - t)) nearest = tt;
    cursor.setAttribute("x1", x(nearest));
    cursor.setAttribute("x2", x(nearest));
    cursor.setAttribute("visibility", "visible");
    tip.replaceChildren(
      el("div", { class: "t" }, fmt.time(nearest)),
      ...series.map((s) => {
        const p = s.points.find((q) => q[0] === nearest);
        return el("div", { class: "row" },
          el("span", {}, el("i", { style: `display:inline-block;width:8px;height:8px;border-radius:2px;margin-right:5px;background:${s.color}` }), s.label),
          el("b", {}, p && p[1] != null ? `${fmt.num(p[1], s.digits ?? 0)} ${s.unit ?? unit}` : "–"));
      }));
    tip.classList.remove("hidden");
    tip.style.left = `${Math.min(ev.clientX + 14, window.innerWidth - tip.offsetWidth - 8)}px`;
    tip.style.top = `${ev.clientY + 14}px`;
  });
  svg.addEventListener("mouseleave", () => {
    cursor.setAttribute("visibility", "hidden");
    tip.classList.add("hidden");
  });

  container.append(svg);
  container.append(el("div", { class: "legend" },
    series.map((s) => el("span", {}, el("i", { style: `background:${s.color}` }), s.label))));
}

const pointsOf = (rows, key) => rows.map((r) => [r.t, r.m[key] ?? null]);

// ---------------------------------------------------------------- status

const STATE_TEXT = {
  Running: ["Capturing FPS", "ok"],
  WaitingForUser: ["Waiting for sign-in", "warn"],
  Restarting: ["Restarting capture", "warn"],
  Disabled: ["FPS capture off", "crit"],
  Starting: ["Starting", ""],
};

let lastStatus = null;

async function refreshStatus() {
  try {
    const s = await getJson("/api/status");
    lastStatus = s;
    const [text, cls] = STATE_TEXT[s.capture.state] ?? [s.capture.state, ""];
    const pill = $("#capture-pill");
    pill.textContent = text;
    pill.className = `pill ${cls}`;
    $("#sample-age").textContent = s.lastSampleUtc ? `Sensors ${fmt.ago(s.lastSampleUtc)}` : "No sensor data yet";

    const banner = $("#capture-banner");
    if (s.capture.state === "Disabled" || s.capture.state === "WaitingForUser" || s.capture.state === "Restarting") {
      banner.textContent = s.capture.message;
      banner.className = `banner ${s.capture.state === "Disabled" ? "warn" : "info"}`;
    } else {
      banner.className = "banner hidden";
    }

    $("#capture-details").replaceChildren(
      el("dt", {}, "State"), el("dd", {}, s.capture.message),
      el("dt", {}, "Mode"), el("dd", {}, s.capture.userSession ? "PresentMon runs in your signed-in session" : "PresentMon runs inside the service"),
      el("dt", {}, "Last frame"), el("dd", {}, s.capture.lastFrameUtc ? `${fmt.ago(s.capture.lastFrameUtc)} (${s.capture.currentApplication ?? "?"})` : "none yet"),
      el("dt", {}, "Last saved session"), el("dd", {}, fmt.ago(s.capture.lastSessionUtc)),
      el("dt", {}, "Database"), el("dd", {}, `${(s.database.sizeBytes / 1048576).toFixed(0)} MB · ${s.database.path}`),
    );
  } catch {
    const pill = $("#capture-pill");
    pill.textContent = "Service not reachable";
    pill.className = "pill crit";
  }
}

// ---------------------------------------------------------------- live

function renderCards(latest) {
  $("#live-cards").replaceChildren(...CARDS.map((c) => {
    const v = latest ? latest[c.key] : null;
    const level = v != null && c.crit != null && v >= c.crit ? "crit" : v != null && c.warn != null && v >= c.warn ? "warn" : "";
    const pct = v == null ? 0 : Math.min(100, (v / c.max) * 100);
    return el("div", { class: `card ${level}` },
      el("div", { class: "card-label" }, c.label),
      el("div", { class: "card-value" }, fmt.num(v, c.digits), el("span", { class: "card-unit" }, c.unit)),
      el("div", { class: "bar" }, el("span", { style: `width:${pct}%` })));
  }));
}

async function refreshLive() {
  try {
    const data = await getJson("/api/live?minutes=5");
    const rows = data.points;
    renderCards(rows.length ? rows[rows.length - 1].m : null);
    $("#live-gpu").textContent = data.gpu ?? "";
    lineChart($("#live-temp"), [
      { label: "CPU", color: css("--c-cpu"), points: pointsOf(rows, M.cpuTemp), unit: "°C" },
      { label: "GPU", color: css("--c-gpu"), points: pointsOf(rows, M.gpuTemp), unit: "°C" },
    ], { unit: "°C" });
    lineChart($("#live-load"), [
      { label: "CPU", color: css("--c-cpu"), points: pointsOf(rows, M.cpuLoad), unit: "%" },
      { label: "GPU", color: css("--c-gpu"), points: pointsOf(rows, M.gpuLoad), unit: "%" },
    ], { unit: "%", yMin: 0, yMax: 100 });
  } catch {
    renderCards(null);
  }
}

// ---------------------------------------------------------------- sessions

let selectedSession = null;

async function refreshSessions() {
  const list = $("#session-list");
  let sessions;
  try {
    sessions = await getJson("/api/sessions?limit=200");
  } catch {
    list.replaceChildren(el("div", { class: "empty" }, "Could not load sessions."));
    return;
  }
  $("#session-count").textContent = sessions.length ? `${sessions.length}` : "";
  if (!sessions.length) {
    list.replaceChildren(el("div", { class: "empty" }, "No sessions yet. Play a game for a few minutes; a session is saved 30 seconds after it closes."));
    return;
  }
  list.replaceChildren(...sessions.map((s) =>
    el("div", {
      class: `session-item ${s.id === selectedSession ? "selected" : ""}`,
      onclick: () => openSession(s.id),
    },
      el("div", { class: "session-app" }, s.application),
      el("div", { class: "session-fps" }, `${fmt.num(s.medianFps)} FPS`),
      el("div", { class: "session-meta" }, `${fmt.date(s.startUtc)} · ${fmt.duration(s.durationSeconds)}`),
      s.worst ? el("span", { class: `badge ${s.worst}` }, s.worst) : el("span"))));
}

async function openSession(id) {
  selectedSession = id;
  document.querySelectorAll(".session-item").forEach((n) => n.classList.remove("selected"));
  refreshSessions();
  const box = $("#session-detail");
  box.replaceChildren(el("div", { class: "empty" }, "Loading…"));

  let r;
  try {
    r = await getJson(`/api/sessions/${id}`);
  } catch {
    box.replaceChildren(el("div", { class: "empty" }, "Could not load this session."));
    return;
  }

  const s = r.session;
  const duration = (new Date(s.endUtc) - new Date(s.startUtc)) / 1000;
  const fpsBlock = el("div", { class: "chart" });
  const tempBlock = el("div", { class: "chart" });
  const loadBlock = el("div", { class: "chart" });
  const clockBlock = el("div", { class: "chart" });

  box.replaceChildren(
    el("div", { class: "detail-head" },
      el("div", {},
        el("h3", {}, s.application),
        el("div", { class: "muted" }, `${new Date(s.startUtc).toLocaleString()} · ${r.gpu ? `GPU: ${r.gpu}` : "no GPU data"}`))),
    el("div", { class: "stats" },
      stat("Median FPS", fmt.num(s.medianFps)),
      stat("Your usual", r.baseline.fps != null ? fmt.num(r.baseline.fps) : "–"),
      stat("Duration", fmt.duration(duration)),
      stat("Frames", s.frameCount.toLocaleString())),
    r.findings.length
      ? el("div", { class: "findings" }, r.findings.map((f) =>
          el("div", { class: `finding ${f.severity}` },
            el("span", { class: `badge ${f.severity}` }, f.severity),
            el("div", {}, el("div", { class: "kind" }, humanKind(f.kind)), f.message,
              el("div", { style: "margin-top:6px" }, voteButtons("finding", f.kind))))))
      : el("div", { class: "findings" }, el("div", { class: "finding" }, el("span", { class: "badge Info" }, "OK"),
          el("div", {}, r.rows.length < 10 ? "Too little data in this session to judge." : "Nothing unusual found in this session."))),
    r.feedback ? feedbackBlock(r.feedback) : "",
    el("div", { class: "detail-actions" },
      el("span", { class: "muted" }, r.baseline.message),
      el("button", { class: "primary-btn", onclick: () => openChat({ sessionId: s.id, question: "What happened in this session, and what should I do about it?" }) }, "Ask about this session")),
    el("div", { class: "chart-block" }, el("div", { class: "chart-title" }, "Frames per second"), fpsBlock),
    el("div", { class: "chart-block" }, el("div", { class: "chart-title" }, "Temperatures"), tempBlock),
    el("div", { class: "chart-block" }, el("div", { class: "chart-title" }, "Load"), loadBlock),
    el("div", { class: "chart-block" }, el("div", { class: "chart-title" }, "GPU clock"), clockBlock),
  );

  const rows = r.rows;
  lineChart(fpsBlock, [{ label: "FPS", color: css("--c-fps"), points: rows.map((x) => [x.t, x.fps]), unit: "FPS" }], { yMin: 0 });
  lineChart(tempBlock, [
    { label: "CPU", color: css("--c-cpu"), points: pointsOf(rows, M.cpuTemp), unit: "°C" },
    { label: "GPU", color: css("--c-gpu"), points: pointsOf(rows, M.gpuTemp), unit: "°C" },
  ]);
  lineChart(loadBlock, [
    { label: "CPU", color: css("--c-cpu"), points: pointsOf(rows, M.cpuLoad), unit: "%" },
    { label: "GPU", color: css("--c-gpu"), points: pointsOf(rows, M.gpuLoad), unit: "%" },
  ], { yMin: 0, yMax: 100 });
  lineChart(clockBlock, [{ label: "GPU clock", color: css("--c-clock"), points: pointsOf(rows, M.gpuClock), unit: "MHz" }]);
}

function feedbackBlock(text) {
  const body = el("div", { dir: "auto" });
  body.innerHTML = renderAnswer(text);
  return el("div", { class: "feedback" }, el("div", { class: "feedback-title" }, "Assistant's feedback"), body);
}

function stat(label, value) {
  return el("div", { class: "stat" }, el("div", { class: "card-label" }, label), el("div", { class: "card-value" }, value));
}

function humanKind(kind) {
  return {
    FpsDrop: "Below your usual FPS",
    CpuHot: "CPU running hot",
    GpuHot: "GPU running hot",
    GpuThrottling: "GPU throttling",
    GpuClockVaries: "GPU clock varied",
    FpsCapped: "Frame rate capped",
    GpuBound: "Limited by the GPU",
    NotGpuBound: "Not limited by the GPU",
    Stutter: "Stutter",
  }[kind] ?? kind;
}

// ---------------------------------------------------------------- health

const SEVERITY_RANK = { Info: 1, Warning: 2, Critical: 3 };
let healthAt = null;
let healthPoll = null;

function healthIcon(worst) {
  const ok = '<svg viewBox="0 0 24 24" width="28" height="28"><path d="M5 12.5l4.5 4.5L19 7.5" fill="none" stroke="currentColor" stroke-width="2.4" stroke-linecap="round" stroke-linejoin="round"/></svg>';
  const warn = '<svg viewBox="0 0 24 24" width="28" height="28"><path d="M12 4l9 16H3L12 4z M12 10v4 M12 17.5v.5" fill="none" stroke="currentColor" stroke-width="2.2" stroke-linecap="round" stroke-linejoin="round"/></svg>';
  const span = el("div", { class: `health-badge ${worst ?? ""}` });
  span.innerHTML = worst === "Warning" || worst === "Critical" ? warn : ok;
  return span;
}

function headline(report) {
  const problems = report.checks.filter((c) => c.severity === "Warning" || c.severity === "Critical");
  const critical = problems.filter((c) => c.severity === "Critical");
  if (critical.length) return `${critical.length} serious problem${critical.length > 1 ? "s" : ""} need attention`;
  if (problems.length) return `${problems.length} thing${problems.length > 1 ? "s" : ""} to look at`;
  return "Your PC looks healthy";
}

function procList(container, items, value, max, unit) {
  if (!items.length) {
    container.replaceChildren(el("div", { class: "muted" }, "Not available."));
    return;
  }
  container.replaceChildren(...items.slice(0, 6).map((p) =>
    el("div", { class: "proc" },
      el("span", { class: "name", title: p.name }, p.name),
      el("span", { class: "val" }, `${fmt.num(value(p), 0)} ${unit}`),
      el("div", { class: "meter" }, el("span", { style: `width:${Math.min(100, (value(p) / max) * 100)}%` })))));
}

async function refreshHealth() {
  let data;
  try {
    data = await getJson("/api/health");
  } catch {
    return;
  }
  const overview = $("#health-overview");
  const report = data.report;
  if (!report) {
    overview.replaceChildren(el("div", { class: "empty" }, "Running the first health check… this takes under a minute after the service starts."));
    return;
  }
  const changed = healthAt !== report.atUtc;
  healthAt = report.atUtc;

  const checkNow = el("button", { class: "primary-btn", onclick: runHealthNow }, data.running ? "Checking…" : "Check now");
  if (data.running) checkNow.disabled = true;
  overview.replaceChildren(
    healthIcon(report.worst),
    el("div", { class: "grow" },
      el("div", { class: "health-headline" }, headline(report)),
      el("div", { class: "muted" }, `Checked ${fmt.ago(report.atUtc)} · ${report.checks.length} checks · runs every 10 minutes`)),
    el("button", { class: "text-btn", onclick: () => openChat({ question: "Look at my PC's health checks. Is anything wrong, and what should I do first?", sessionId: null }) }, "Ask AI about it"),
    checkNow);

  if (!changed && $("#health-checks").childElementCount) return;

  const sorted = [...report.checks].sort((a, b) => (SEVERITY_RANK[b.severity] ?? 0) - (SEVERITY_RANK[a.severity] ?? 0));
  const problems = sorted.filter((c) => c.severity === "Warning" || c.severity === "Critical").length;
  $("#health-count").textContent = problems ? `${problems} need attention` : "all passed";
  $("#health-checks").replaceChildren(...sorted.map((c) => {
    const problem = c.severity === "Warning" || c.severity === "Critical";
    const row = el("div", { class: `check ${problem ? "problem" : ""}` },
      el("span", { class: `dot ${c.severity ?? ""}` }),
      el("div", {},
        el("div", { class: "check-title" }, c.title),
        el("div", { class: "check-summary" }, c.summary),
        problem || c.severity === "Info" ? el("div", { style: "margin-top:4px" }, voteButtons("health", c.id)) : null),
      problem || c.severity === "Info"
        ? el("button", { class: "text-btn", onclick: () => openChat({ question: `About "${c.title}": ${c.summary} What does this mean for me, and what should I do?`, sessionId: null }) }, "Ask about this")
        : el("span"));
    if (c.evidence.length) {
      const details = el("details", { class: "check-evidence" }, el("summary", {}, problem ? "Details" : "What was checked"),
        el("ul", {}, c.evidence.map((e) => el("li", { dir: "auto" }, e))));
      if (problem) details.open = true;
      row.append(details);
    }
    return row;
  }));

  procList($("#health-cpu"), report.topByCpu, (p) => p.cpuPercent, 100, "%");
  procList($("#health-memory"), report.topByMemory, (p) => p.memoryMb, Math.max(1, ...report.topByMemory.map((p) => p.memoryMb)), "MB");
}

async function runHealthNow() {
  try {
    await fetch("/api/health/run", { method: "POST" });
  } catch {
    return;
  }
  const started = healthAt;
  clearInterval(healthPoll);
  let tries = 0;
  healthPoll = setInterval(async () => {
    await refreshHealth();
    if (healthAt !== started || ++tries > 30) clearInterval(healthPoll);
  }, 2000);
  refreshHealth();
}

// ---------------------------------------------------------------- system

async function refreshSpecs() {
  const box = $("#specs");
  let s;
  try {
    s = await getJson("/api/specs");
  } catch {
    box.replaceChildren(el("div", { class: "empty" }, "System details appear after the service has started once."));
    return;
  }
  const spec = (label, value, sub) =>
    el("div", { class: "spec" }, el("div", { class: "card-label" }, label), el("div", { class: "value" }, value || "–"), sub ? el("div", { class: "sub" }, sub) : null);
  box.replaceChildren(
    spec("Computer", `${s.computerManufacturer} ${s.computerModel}`.trim()),
    spec("Processor", s.cpuName, `${s.cpuCores} cores · ${s.cpuLogicalProcessors} threads · up to ${s.cpuMaxClockMhz} MHz`),
    spec("Memory", `${fmt.num(s.ramTotalGb, 0)} GB`),
    ...s.gpus.map((g) => spec("Graphics", g.name, `Driver ${g.driverVersion}`)),
    spec("Motherboard", `${s.motherboardManufacturer} ${s.motherboardProduct}`.trim(), `BIOS ${s.biosVersion}`),
    spec("Windows", s.osName, `Version ${s.osVersion} · build ${s.osBuild}`),
  );
}

// ---------------------------------------------------------------- tabs and timers

let activeTab = "assistant";

// On the Assistant tab the chat is part of the page; elsewhere it is the slide-in drawer.
function dockChat(docked) {
  const drawer = $("#chat");
  if (docked) {
    $("#assistant-chat-slot").append(drawer);
    drawer.classList.add("docked", "open");
    drawer.setAttribute("aria-hidden", "false");
    ensureChatStatus().then(renderChat);
  } else if (drawer.classList.contains("docked")) {
    document.body.append(drawer);
    drawer.classList.remove("docked", "open");
    drawer.setAttribute("aria-hidden", "true");
  }
}

function showTab(name) {
  activeTab = name;
  dockChat(name === "assistant");
  if (name === "assistant") refreshAssistant();
  document.querySelectorAll(".tab").forEach((t) => t.classList.toggle("active", t.dataset.tab === name));
  document.querySelectorAll(".tab-panel").forEach((p) => p.classList.toggle("active", p.id === `tab-${name}`));
  if (name === "health") refreshHealth();
  if (name === "live") refreshLive();
  if (name === "sessions") refreshSessions();
  if (name === "system") refreshSpecs();
}

document.querySelectorAll(".tab").forEach((t) => t.addEventListener("click", () => showTab(t.dataset.tab)));

// ---------------------------------------------------------------- assistant home

const assistantState = { overview: null };

function greeting() {
  const h = new Date().getHours();
  return h < 5 ? "Hi." : h < 12 ? "Good morning." : h < 18 ? "Good afternoon." : "Good evening.";
}

const SOURCE_LABEL = { live: "Live", health: "Health check", trend: "Trend" };

async function refreshAssistant() {
  let o;
  try { o = await getJson("/api/assistant/overview"); } catch { return; }
  const firstLoad = !assistantState.overview;
  assistantState.overview = o;

  const sev = o.verdict === "problem" ? "Critical" : o.verdict === "attention" ? "Warning" : "ok";
  const icon = o.verdict === "good"
    ? '<svg viewBox="0 0 24 24" width="26" height="26"><path d="M5 12.5l4.5 4.5L19 7.5" fill="none" stroke="currentColor" stroke-width="2.4" stroke-linecap="round" stroke-linejoin="round"/></svg>'
    : '<svg viewBox="0 0 24 24" width="26" height="26"><path d="M12 4l9 16H3L12 4zm0 6v4m0 3v.5" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"/></svg>';
  const badge = el("div", { class: `health-badge ${sev}` });
  badge.innerHTML = icon;

  const chips = [];
  if (o.playing) chips.push(el("span", { class: "now-chip playing" }, `Playing ${o.playing.application} · ${fmt.num(o.playing.fps)} FPS`));
  if (o.now) {
    if (o.now.cpuTemp != null) chips.push(el("span", { class: "now-chip" }, `CPU ${fmt.num(o.now.cpuTemp)} °C · ${fmt.num(o.now.cpuLoad)}%`));
    if (o.now.gpuTemp != null) chips.push(el("span", { class: "now-chip" }, `GPU ${fmt.num(o.now.gpuTemp)} °C · ${fmt.num(o.now.gpuLoad)}%`));
    if (o.now.memoryPercent != null) chips.push(el("span", { class: "now-chip" }, `RAM ${fmt.num(o.now.memoryPercent)}%`));
  }
  $("#assistant-hero").replaceChildren(
    el("div", { class: "top" }, badge, el("div", { class: "grow" },
      el("div", { class: "headline" }, o.headline),
      el("div", { class: "muted small" }, o.healthCheckedUtc ? `Health checked ${fmt.ago(o.healthCheckedUtc)} · watching live` : "Watching live"))),
    chips.length ? el("div", { class: "now-strip" }, chips) : null);

  const open = o.items.filter((i) => i.active && i.severity !== "Info").length;
  $("#assistant-count").textContent = open ? `${open} need attention` : "all clear";
  $("#assistant-items").replaceChildren(...(o.items.length ? o.items.map((i) =>
    el("div", { class: `a-item${i.active ? "" : " resolved"}` },
      el("span", { class: `dot ${i.severity}` }),
      el("div", {},
        el("div", { class: "meta" },
          i.source === "live" && i.active ? el("span", { class: "live" }, "● Live now") : (SOURCE_LABEL[i.source] ?? i.source),
          i.active ? "" : " · ended",
          i.sinceUtc ? ` · since ${fmt.ago(i.sinceUtc)}` : ""),
        el("div", { class: "check-title" }, i.title)),
      el("button", { class: "chip ask", type: "button", onclick: () => ask(i.question) }, "Ask"),
      el("div", { class: "detail" }, i.detail)))
    : [el("div", { class: "empty" }, "Nothing to report. I'll tell you as soon as something comes up, also while you play.")]));

  if (firstLoad && !chat.messages.length) renderChat();
}

// ---------------------------------------------------------------- chat

const chat = {
  messages: [],          // { role: "user" | "assistant", content }
  focusSessionId: null,  // session the question is about, if any
  configured: null,
  signInAvailable: false,
  busy: false,
  controller: null,
};

const SUGGESTIONS = [
  "Is anything wrong with my PC right now?",
  "Why is my PC slow?",
  "Has my PC crashed recently, and why?",
  "Are my temperatures OK?",
  "Why did my FPS drop in the last game?",
];

function saveChat() {
  try { sessionStorage.setItem("aianalyze-chat", JSON.stringify(chat.messages)); } catch { /* storage unavailable */ }
}

function loadChat() {
  try { chat.messages = JSON.parse(sessionStorage.getItem("aianalyze-chat") || "[]"); } catch { chat.messages = []; }
}

/** Minimal, safe formatting for answers: escapes HTML, then **bold**, bullet and numbered lists, paragraphs. */
function renderAnswer(text) {
  const esc = (s) => s.replace(/[&<>"']/g, (c) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c]));
  const inline = (s) => esc(s).replace(/\*\*(.+?)\*\*/g, "<b>$1</b>").replace(/`([^`]+)`/g, "<code>$1</code>");
  const out = [];
  let list = null;
  for (const raw of text.split("\n")) {
    const line = raw.trimEnd();
    const bullet = line.match(/^\s*[-*•]\s+(.*)$/);
    const numbered = line.match(/^\s*\d+[.)]\s+(.*)$/);
    if (bullet || numbered) {
      const tag = bullet ? "ul" : "ol";
      if (list !== tag) { if (list) out.push(`</${list}>`); out.push(`<${tag}>`); list = tag; }
      out.push(`<li>${inline((bullet || numbered)[1])}</li>`);
      continue;
    }
    if (list) { out.push(`</${list}>`); list = null; }
    if (line.trim()) out.push(`<p>${inline(line.replace(/^#+\s*/, ""))}</p>`);
  }
  if (list) out.push(`</${list}>`);
  return out.join("");
}

function renderChat() {
  const box = $("#chat-messages");
  if (chat.configured === false) {
    box.replaceChildren(chat.signInAvailable
      ? el("div", { class: "setup-note" },
          el("b", {}, "Sign in to use the assistant. It's free during the beta."),
          el("div", { style: "margin:8px 0" }, "Sign-in opens in your browser and brings you back here."),
          el("a", { class: "primary-btn", href: "/auth/sign-in" }, "Sign in"),
          el("div", { class: "muted small", style: "margin-top:10px" }, "Advanced: you can also use your own Anthropic API key by rerunning the installer with ",
            el("code", {}, "-SetApiKey"), "."))
      : el("div", { class: "setup-note" },
          el("b", {}, "The assistant needs your own Anthropic API key."),
          el("div", { style: "margin-top:6px" }, "Rerun the installer as Administrator with ", el("code", {}, "-SetApiKey"),
            " and paste your key when asked. The key stays on this PC in a file only Windows administrators can read.")));
    $("#chat-suggestions").replaceChildren();
    $("#chat-send").disabled = true;
    return;
  }
  $("#chat-send").disabled = chat.busy;

  if (!chat.messages.length) {
    box.replaceChildren(assistantState.overview
      ? el("div", { class: "assistant-greeting", dir: "auto" },
          el("b", {}, greeting() + " "), assistantState.overview.headline,
          el("div", { class: "muted small", style: "margin-top:6px" }, "Ask me anything about this PC, or pick a question below."))
      : el("div", { class: "chat-empty" },
          "Ask about temperatures, FPS, a session or an alert. Answers use what this PC has measured."));
  } else {
    box.replaceChildren(...chat.messages.flatMap((m, i) => {
      const bubble = el("div", { class: `bubble ${m.role}${m.error ? " error" : ""}`, dir: "auto" });
      if (m.role === "assistant" && !m.error) bubble.innerHTML = renderAnswer(m.content);
      else bubble.textContent = m.content;
      const streaming = chat.busy && i === chat.messages.length - 1 && m.role === "assistant";
      if (streaming) bubble.classList.add("pending");
      // What the assistant looked up on this PC while answering.
      const steps = m.checks?.length
        ? [el("div", { class: "tool-steps" }, ...m.checks.map((c, k) =>
            el("div", { class: `tool-step${streaming && k === m.checks.length - 1 && m.content.length === m.checkAt ? " running" : ""}` }, c)))]
        : [];
      const parts = m.role === "assistant" && !m.content && !m.error && steps.length ? steps : [...steps, bubble];
      return m.role === "assistant" && !m.error && !streaming && m.content
        ? [...parts, voteButtons("answer", "chat", m)]
        : parts;
    }));
    box.scrollTop = box.scrollHeight;
  }

  $("#chat-suggestions").replaceChildren(...(chat.messages.length ? [] : (assistantState.overview?.suggestedQuestions ?? SUGGESTIONS).map((q) =>
    el("button", { class: "chip", type: "button", onclick: () => ask(q) }, q))));
  $("#chat-context").textContent = chat.focusSessionId
    ? `Includes session ${chat.focusSessionId} in detail`
    : "Uses this PC's measurements";
}

async function openChat({ question, sessionId } = {}) {
  if (sessionId !== undefined) chat.focusSessionId = sessionId;
  else if (activeTab === "sessions" && selectedSession) chat.focusSessionId = selectedSession;
  $("#chat").classList.add("open");
  $("#chat").setAttribute("aria-hidden", "false");
  await ensureChatStatus();
  renderChat();
  if (question) ask(question);
  else $("#chat-input").focus();
}

async function ensureChatStatus() {
  if (chat.configured !== null) return;
  try {
    const status = await getJson("/api/chat/status");
    chat.configured = status.configured;
    chat.signInAvailable = status.signInAvailable;
  } catch {
    chat.configured = false;
  }
}

function closeChat() {
  if ($("#chat").classList.contains("docked")) return;
  $("#chat").classList.remove("open");
  $("#chat").setAttribute("aria-hidden", "true");
}

async function ask(question) {
  question = question.trim();
  if (!question || chat.busy || chat.configured === false) return;

  chat.messages.push({ role: "user", content: question });
  const answer = { role: "assistant", content: "" };
  chat.messages.push(answer);
  chat.busy = true;
  $("#chat-input").value = "";
  renderChat();

  const history = chat.messages.slice(0, -1).filter((m) => !m.error && m.content).map(({ role, content }) => ({ role, content }));
  chat.controller = new AbortController();
  try {
    const res = await fetch("/api/chat", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ messages: history, sessionId: chat.focusSessionId }),
      signal: chat.controller.signal,
    });
    if (!res.ok) {
      const body = await res.json().catch(() => ({}));
      throw new Error(body.error || `The assistant is unavailable (${res.status}).`);
    }

    // Server-sent events: lines of "data: {json}" separated by blank lines.
    const reader = res.body.getReader();
    const decoder = new TextDecoder();
    let buffer = "";
    for (;;) {
      const { value, done } = await reader.read();
      if (done) break;
      buffer += decoder.decode(value, { stream: true });
      let cut;
      while ((cut = buffer.indexOf("\n\n")) >= 0) {
        const line = buffer.slice(0, cut).trim();
        buffer = buffer.slice(cut + 2);
        if (!line.startsWith("data:")) continue;
        const event = JSON.parse(line.slice(5));
        if (event.text) { answer.content += event.text; renderChat(); }
        if (event.tool) { (answer.checks ??= []).push(event.tool.label); answer.checkAt = answer.content.length; renderChat(); }
        if (event.error) throw new Error(event.error);
      }
    }
    if (!answer.content) answer.content = "(No answer.)";
  } catch (err) {
    if (err.name !== "AbortError") {
      answer.content = err.message;
      answer.error = true;
    }
  } finally {
    chat.busy = false;
    chat.controller = null;
    saveChat();
    renderChat();
  }
}

$("#chat-btn").addEventListener("click", () => (activeTab === "assistant" ? $("#chat-input").focus() : openChat()));
$("#chat-close").addEventListener("click", closeChat);
$("#chat-clear").addEventListener("click", () => {
  chat.controller?.abort();
  chat.messages = [];
  chat.focusSessionId = activeTab === "sessions" ? selectedSession : null;
  saveChat();
  renderChat();
});
$("#chat-form").addEventListener("submit", (ev) => { ev.preventDefault(); ask($("#chat-input").value); });
$("#chat-input").addEventListener("keydown", (ev) => {
  if (ev.key === "Enter" && !ev.shiftKey) { ev.preventDefault(); ask($("#chat-input").value); }
});
document.addEventListener("keydown", (ev) => { if (ev.key === "Escape") { closeChat(); $("#alerts-panel").classList.add("hidden"); } });

// ---------------------------------------------------------------- alerts

const alertsState = { lastId: null, items: [], unseen: 0 };

function askAboutAlert(a) {
  openChat({
    question: `Explain this alert in plain words: "${a.message}". What most likely caused it, and what should I do?`,
    sessionId: a.sessionId ?? null,
  });
}

function renderAlertCount() {
  const badge = $("#alert-count");
  badge.textContent = alertsState.unseen > 9 ? "9+" : String(alertsState.unseen);
  badge.classList.toggle("hidden", alertsState.unseen === 0);
}

function renderAlertsPanel() {
  const panel = $("#alerts-panel");
  if (!alertsState.items.length) {
    panel.replaceChildren(el("div", { class: "empty" }, "No alerts. You'll see temperature warnings and session problems here."));
    return;
  }
  panel.replaceChildren(...alertsState.items.map((a) =>
    el("div", { class: "alert-row" },
      el("div", { class: "meta" }, el("span", { class: `badge ${a.severity}` }, a.severity), fmt.date(a.atUtc)),
      el("div", { dir: "auto" }, a.message),
      el("div", { class: "actions" }, el("button", { class: "text-btn", onclick: () => askAboutAlert(a) }, "Ask about this")))));
}

function showToast(a) {
  const toast = el("div", { class: `toast ${a.severity}` },
    el("div", { class: "meta" }, el("span", { class: `badge ${a.severity}` }, a.severity)),
    el("div", { dir: "auto" }, a.message),
    el("div", { class: "actions" },
      el("button", { class: "text-btn", onclick: () => toast.remove() }, "Dismiss"),
      el("button", { class: "primary-btn", onclick: () => { toast.remove(); askAboutAlert(a); } }, "Ask about this")));
  $("#toasts").append(toast);
  setTimeout(() => toast.remove(), 20000);
}

async function refreshAlerts() {
  let fresh;
  try {
    fresh = await getJson(`/api/alerts?after=${alertsState.lastId ?? 0}`);
  } catch {
    return;
  }
  if (!fresh.length) return;
  const firstLoad = alertsState.lastId === null;
  alertsState.lastId = Math.max(alertsState.lastId ?? 0, ...fresh.map((a) => a.id));
  alertsState.items = [...fresh, ...alertsState.items].slice(0, 50);
  // On first load only list existing alerts; pop up the ones that arrive while the page is open.
  if (!firstLoad) {
    fresh.slice(0, 3).forEach(showToast);
    alertsState.unseen += fresh.length;
  }
  renderAlertCount();
  if (!$("#alerts-panel").classList.contains("hidden")) renderAlertsPanel();
}

$("#alerts-btn").addEventListener("click", (ev) => {
  ev.stopPropagation();
  const panel = $("#alerts-panel");
  panel.classList.toggle("hidden");
  if (!panel.classList.contains("hidden")) {
    alertsState.unseen = 0;
    renderAlertCount();
    renderAlertsPanel();
  }
});
document.addEventListener("click", (ev) => {
  const panel = $("#alerts-panel");
  if (!panel.contains(ev.target) && ev.target !== $("#alerts-btn")) panel.classList.add("hidden");
});

// ---------------------------------------------------------------- feedback votes

/** "Helpful? 👍 👎" for a finding, health check or answer. Votes stay on this PC unless sharing is on. */
function voteButtons(target, kind, memo) {
  const box = el("span", { class: "vote" });
  const thanks = () => { box.className = "vote done"; box.replaceChildren("Thanks for the feedback"); };
  if (memo?.voted) { thanks(); return box; }
  const icon = (up) => {
    const b = el("button", { type: "button", title: up ? "Helpful" : "Not helpful", "aria-label": up ? "Helpful" : "Not helpful" });
    b.innerHTML = up
      ? '<svg viewBox="0 0 24 24" width="14" height="14"><path d="M7 11v9H4v-9h3zm2 9h8.5a2 2 0 0 0 2-1.6l1.2-6A2 2 0 0 0 18.7 10H14l.8-4a1.6 1.6 0 0 0-3-1L9 11v9z" fill="none" stroke="currentColor" stroke-width="1.7" stroke-linejoin="round"/></svg>'
      : '<svg viewBox="0 0 24 24" width="14" height="14"><path d="M7 13V4H4v9h3zm2-9h8.5a2 2 0 0 1 2 1.6l1.2 6A2 2 0 0 1 18.7 14H14l.8 4a1.6 1.6 0 0 1-3 1L9 13V4z" fill="none" stroke="currentColor" stroke-width="1.7" stroke-linejoin="round"/></svg>';
    b.addEventListener("click", async () => {
      try {
        await fetch("/api/feedback", { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ target, kind, helpful: up }) });
        if (memo) { memo.voted = true; saveChat(); }
        thanks();
      } catch { /* ignore */ }
    });
    return b;
  };
  box.append("Helpful?", icon(true), icon(false));
  return box;
}

// ---------------------------------------------------------------- account

let account = null;

function infoToast(message) {
  const toast = el("div", { class: "toast", style: "border-left-color: var(--ok)" }, el("div", {}, message));
  $("#toasts").append(toast);
  setTimeout(() => toast.remove(), 6000);
}

async function refreshAccount() {
  try { account = await getJson("/api/account"); } catch { return; }
  const btn = $("#account-btn");
  btn.textContent = account.signedIn ? (account.name || account.email || "Account") : account.signInConfigured ? "Sign in" : "Settings";
  btn.title = account.signedIn ? `Signed in as ${account.email ?? account.name}` : "";
}

function renderAccountPanel() {
  const panel = $("#account-panel");
  const settings = el("button", { class: "text-btn", onclick: openSettings }, "Settings");
  if (!account) { panel.replaceChildren(settings); return; }
  if (account.signedIn) {
    const q = account.quota;
    panel.replaceChildren(
      el("div", {}, el("div", { class: "check-title" }, account.name || "Signed in"), el("div", { class: "muted" }, account.email ?? "")),
      q ? el("div", {},
            el("div", { class: "row small" }, el("span", {}, "Assistant questions today"), el("b", {}, `${q.questionsToday} of ${q.dailyLimit}`)),
            el("div", { class: "quota-bar" }, el("span", { style: `width:${Math.min(100, (q.questionsToday / Math.max(1, q.dailyLimit)) * 100)}%` })))
        : null,
      el("div", { class: "row" }, settings, el("button", { class: "text-btn", onclick: signOut }, "Sign out")));
  } else if (account.signInConfigured) {
    panel.replaceChildren(
      el("div", {}, el("div", { class: "check-title" }, "Sign in to AIAnalyze"),
        el("div", { class: "muted" }, "Use the assistant for free during the beta. Sign-in opens in your browser.")),
      el("div", { class: "row" }, settings, el("a", { class: "primary-btn", href: "/auth/sign-in" }, "Sign in")));
  } else {
    panel.replaceChildren(el("div", { class: "muted" }, "Accounts aren't set up in this build."), settings);
  }
}

async function signOut() {
  await fetch("/api/account/sign-out", { method: "POST" }).catch(() => {});
  chat.configured = null;
  $("#account-panel").classList.add("hidden");
  await refreshAccount();
  infoToast("Signed out.");
}

$("#account-btn").addEventListener("click", async (ev) => {
  ev.stopPropagation();
  const panel = $("#account-panel");
  if (panel.classList.contains("hidden")) {
    await refreshAccount();
    renderAccountPanel();
  }
  panel.classList.toggle("hidden");
});
document.addEventListener("click", (ev) => {
  const panel = $("#account-panel");
  if (!panel.contains(ev.target) && ev.target !== $("#account-btn")) panel.classList.add("hidden");
});

// ---------------------------------------------------------------- settings and privacy

async function openSettings({ showPreview = false } = {}) {
  $("#account-panel").classList.add("hidden");
  const body = $("#settings-body");
  body.replaceChildren(el("div", { class: "muted" }, "Loading…"));
  $("#settings").classList.remove("hidden");

  let privacy, notifications;
  try {
    [privacy, notifications] = await Promise.all([getJson("/api/privacy"), getJson("/api/notifications"), refreshAccount()]);
  } catch {
    body.replaceChildren(el("div", { class: "muted" }, "Could not load settings."));
    return;
  }

  const assistantText = {
    Account: "On, through your AIAnalyze account.",
    ApiKey: "On, with your own Anthropic API key.",
    Off: account?.signInConfigured ? "Off. Sign in to turn it on." : "Off. Add your own API key with the installer's -SetApiKey option.",
  }[account?.assistant ?? "Off"];

  const preview = el("pre", { class: `preview ${showPreview ? "" : "hidden"}` }, JSON.stringify(privacy.preview, null, 2));
  const toggle = el("input", { type: "checkbox", id: "share-toggle" });
  toggle.checked = privacy.consent === true;
  toggle.addEventListener("change", async () => {
    await setConsent(toggle.checked);
  });

  const emailToggle = el("input", { type: "checkbox", id: "email-toggle" });
  emailToggle.checked = notifications.emailAlerts;
  emailToggle.disabled = !notifications.available && !notifications.emailAlerts;
  const emailNote = el("p", { class: "small", style: "margin-top:8px" }, notifications.reason ?? "");
  const testBtn = el("button", { class: "text-btn", onclick: async () => {
    testBtn.disabled = true;
    const res = await fetch("/api/notifications/test", { method: "POST" }).catch(() => null);
    const out = res ? await res.json().catch(() => ({})) : {};
    infoToast(res?.ok ? `Test email sent to ${notifications.email}.` : out.error || "The test email could not be sent.");
    testBtn.disabled = false;
  } }, "Send a test email");
  emailToggle.addEventListener("change", async () => {
    const res = await fetch("/api/notifications", { method: "POST", headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ emailAlerts: emailToggle.checked }) }).catch(() => null);
    if (!res?.ok) {
      const out = res ? await res.json().catch(() => ({})) : {};
      emailToggle.checked = !emailToggle.checked;
      infoToast(out.error || "Could not change email alerts.");
    }
  });

  body.replaceChildren(
    el("div", { class: "setting" },
      el("h3", {}, "Account"),
      el("p", {}, account?.signedIn ? `Signed in as ${account.email ?? account.name}.` : account?.signInConfigured ? "Not signed in." : "Accounts aren't set up in this build."),
      account?.signedIn
        ? el("button", { class: "text-btn", onclick: async () => { await signOut(); openSettings(); } }, "Sign out")
        : account?.signInConfigured ? el("a", { class: "primary-btn", href: "/auth/sign-in" }, "Sign in") : null),
    el("div", { class: "setting" }, el("h3", {}, "Assistant"), el("p", {}, assistantText)),
    el("div", { class: "setting" },
      el("h3", {}, "Notifications"),
      el("p", { class: "muted" }, "Windows notifications show problems as they happen, also while you play."),
      el("label", { class: "toggle", for: "email-toggle" }, emailToggle,
        el("span", {}, el("b", {}, "Email me about serious problems"),
          el("div", { class: "muted" },
            `For when you're away from the PC: a failing drive, blue screens, critical heat, a drive about to fill up, or a problem building up over weeks. At most one email per problem a day and ${notifications.dailyLimit} a day. Sent to ${notifications.email ?? "your account's email address"}. The email contains only the problem's title and description.`))),
      notifications.reason ? emailNote : null,
      notifications.available ? testBtn : null),
    el("div", { class: "setting" },
      el("h3", {}, "Privacy"),
      el("label", { class: "toggle", for: "share-toggle" }, toggle,
        el("span", {}, el("b", {}, "Share anonymous diagnostics to help improve AIAnalyze"),
          el("div", { class: "muted" }, "Once a day: hardware models, driver versions, Windows build, which health checks failed, which finding types appeared, and how games ran (game file name and FPS). Plus your Helpful / Not helpful votes. Never names of your files, folders or other programs, never Event Log text, never chat content. Tied to a random ID for this install, and to your account only if you're signed in."))),
      privacy.serverConfigured ? null : el("p", { class: "small", style: "margin-top:8px" }, "This build has no server set up yet, so nothing is sent even when sharing is on."),
      el("button", { class: "text-btn", onclick: () => preview.classList.toggle("hidden") }, "Show exactly what is shared"),
      preview),
  );
}

async function setConsent(share) {
  await fetch("/api/privacy", { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ share }) }).catch(() => {});
  $("#consent-banner").classList.add("hidden");
}

$("#settings-close").addEventListener("click", () => $("#settings").classList.add("hidden"));
$("#settings").addEventListener("click", (ev) => { if (ev.target === $("#settings")) $("#settings").classList.add("hidden"); });
$("#consent-yes").addEventListener("click", () => { setConsent(true); infoToast("Thanks! Sharing is on. You can turn it off in Settings."); });
$("#consent-no").addEventListener("click", () => setConsent(false));
$("#consent-details").addEventListener("click", () => openSettings({ showPreview: true }));
document.addEventListener("keydown", (ev) => { if (ev.key === "Escape") $("#settings").classList.add("hidden"); });

async function initAccountAndPrivacy() {
  await refreshAccount();
  if (location.hash === "#signed-in") {
    history.replaceState(null, "", location.pathname);
    chat.configured = null;
    infoToast(`Signed in${account?.email ? ` as ${account.email}` : ""}.`);
  }
  try {
    const privacy = await getJson("/api/privacy");
    if (privacy.consent === null) $("#consent-banner").classList.remove("hidden");
  } catch { /* ignore */ }
}

initAccountAndPrivacy();
loadChat();
refreshAlerts();
setInterval(() => { if (!document.hidden) refreshAlerts(); }, 10000);

refreshStatus();
showTab("assistant");
setInterval(refreshStatus, 5000);
setInterval(() => { if (activeTab === "health" && !document.hidden) refreshHealth(); }, 30000);
setInterval(() => { if (activeTab === "assistant" && !document.hidden) refreshAssistant(); }, 5000);
setInterval(() => { if (activeTab === "live" && !document.hidden) refreshLive(); }, 2000);
setInterval(() => { if (activeTab === "sessions" && !document.hidden) refreshSessions(); }, 30000);
