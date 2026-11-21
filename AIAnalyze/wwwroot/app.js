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
            el("div", {}, el("div", { class: "kind" }, humanKind(f.kind)), f.message))))
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

let activeTab = "live";

function showTab(name) {
  activeTab = name;
  document.querySelectorAll(".tab").forEach((t) => t.classList.toggle("active", t.dataset.tab === name));
  document.querySelectorAll(".tab-panel").forEach((p) => p.classList.toggle("active", p.id === `tab-${name}`));
  if (name === "live") refreshLive();
  if (name === "sessions") refreshSessions();
  if (name === "system") refreshSpecs();
}

document.querySelectorAll(".tab").forEach((t) => t.addEventListener("click", () => showTab(t.dataset.tab)));

// ---------------------------------------------------------------- chat

const chat = {
  messages: [],          // { role: "user" | "assistant", content }
  focusSessionId: null,  // session the question is about, if any
  configured: null,
  busy: false,
  controller: null,
};

const SUGGESTIONS = [
  "Is anything wrong with my PC right now?",
  "Why did my FPS drop in the last session?",
  "Are my temperatures OK?",
  "What limits my FPS, the CPU or the GPU?",
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
    box.replaceChildren(el("div", { class: "setup-note" },
      el("b", {}, "The assistant needs your own Anthropic API key."),
      el("div", { style: "margin-top:6px" }, "Rerun the installer as Administrator with ", el("code", {}, "-SetApiKey"),
        " and paste your key when asked. The key stays on this PC in a file only Windows administrators can read.")));
    $("#chat-suggestions").replaceChildren();
    $("#chat-send").disabled = true;
    return;
  }
  $("#chat-send").disabled = chat.busy;

  if (!chat.messages.length) {
    box.replaceChildren(el("div", { class: "chat-empty" },
      "Ask about temperatures, FPS, a session or an alert. Answers use what this PC has measured."));
  } else {
    box.replaceChildren(...chat.messages.map((m, i) => {
      const bubble = el("div", { class: `bubble ${m.role}${m.error ? " error" : ""}`, dir: "auto" });
      if (m.role === "assistant" && !m.error) bubble.innerHTML = renderAnswer(m.content);
      else bubble.textContent = m.content;
      if (chat.busy && i === chat.messages.length - 1 && m.role === "assistant") bubble.classList.add("pending");
      return bubble;
    }));
    box.scrollTop = box.scrollHeight;
  }

  $("#chat-suggestions").replaceChildren(...(chat.messages.length ? [] : SUGGESTIONS.map((q) =>
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
  if (chat.configured === null) {
    try { chat.configured = (await getJson("/api/chat/status")).configured; } catch { chat.configured = false; }
  }
  renderChat();
  if (question) ask(question);
  else $("#chat-input").focus();
}

function closeChat() {
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

  const history = chat.messages.slice(0, -1).filter((m) => !m.error).map(({ role, content }) => ({ role, content }));
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

$("#chat-btn").addEventListener("click", () => openChat());
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

loadChat();
refreshAlerts();
setInterval(() => { if (!document.hidden) refreshAlerts(); }, 10000);

refreshStatus();
refreshLive();
setInterval(refreshStatus, 5000);
setInterval(() => { if (activeTab === "live" && !document.hidden) refreshLive(); }, 2000);
setInterval(() => { if (activeTab === "sessions" && !document.hidden) refreshSessions(); }, 30000);
