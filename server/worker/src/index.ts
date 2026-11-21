import Anthropic from "@anthropic-ai/sdk";
import { createRemoteJWKSet, jwtVerify } from "jose";
import INSTRUCTIONS from "../../../AIAnalyze/Chat/Assistant/instructions.txt";
import TOOLS from "../../../AIAnalyze/Chat/Assistant/tools.json";

/**
 * AIAnalyze API.
 *   GET  /v1/me         signed-in user's quota
 *   POST /v1/chat       one model call for a question, with the app's tools (signed-in, daily quota);
 *                       the app runs the tools on the PC and sends their results in the next call
 *   POST /v1/telemetry  opt-in diagnostics snapshot and feedback votes (anonymous or signed-in)
 */
export interface Env {
  DB: D1Database;
  ANTHROPIC_API_KEY: string;
  OIDC_ISSUER: string;
  OIDC_CLIENT_ID?: string;
  MODEL: string;
  DAILY_FREE_QUESTIONS: string;
  /** Website origin allowed to read /v1/me from the browser, for example https://aianalyze.app. */
  SITE_ORIGIN?: string;
}

const MAX_MESSAGES = 60; // 20 conversation turns plus the tool steps of the current question
const MAX_MESSAGE_CHARS = 4000;
const MAX_BLOCK_CHARS = 20_000; // one tool result or assistant text block
const MAX_THINKING_CHARS = 200_000;
const MAX_BRIEFING_CHARS = 60_000;
const MAX_CHAT_BYTES = 768 * 1024;
const MAX_TELEMETRY_BYTES = 64 * 1024;
const MAX_TELEMETRY_PER_DEVICE_PER_DAY = 50;
/** Model calls per question; must match ClaudeChat.MaxSteps in the app. */
const MAX_STEPS = 8;
/** Tool steps a user may spend per day, per free question. */
const STEPS_PER_QUESTION = 6;

// The instructions and tool definitions are the same files the app embeds (AIAnalyze/Chat/Assistant).
// The server owns them, so a modified client cannot turn the relay into a general-purpose assistant
// on the operator's key or add tools of its own.
const TOOL_NAMES = new Set((TOOLS as { name: string }[]).map((t) => t.name));
const ASSISTANT_BLOCKS = new Set(["text", "tool_use", "thinking", "redacted_thinking"]);

type Block = Record<string, unknown> & { type: string };
type ChatMessage = { role: "user" | "assistant"; content: string | Block[] };

export default {
  async fetch(request: Request, env: Env, ctx: ExecutionContext): Promise<Response> {
    const url = new URL(request.url);
    try {
      // The website reads the signed-in user's quota from the browser; only its origin is allowed, only for /v1/me.
      if (url.pathname === "/v1/me" && request.method === "OPTIONS") return withCors(new Response(null, { status: 204 }), request, env);
      if (request.method === "GET" && url.pathname === "/v1/me") return withCors(await me(request, env), request, env);
      if (request.method === "POST" && url.pathname === "/v1/chat") return await chat(request, env);
      if (request.method === "POST" && url.pathname === "/v1/telemetry") return await telemetry(request, env);
      return json({ error: "Not found." }, 404);
    } catch (err) {
      // Errors on /v1/me carry CORS headers too, so the website can show "sign in again" instead of a blocked request.
      const cors = (r: Response) => (url.pathname === "/v1/me" ? withCors(r, request, env) : r);
      if (err instanceof HttpError) return cors(json({ error: err.message }, err.status));
      console.error(err);
      return cors(json({ error: "Something went wrong on the server." }, 500));
    }
  },

  // Daily cleanup: telemetry and feedback after 90 days, usage counters after 60 days.
  async scheduled(_event: ScheduledEvent, env: Env): Promise<void> {
    await env.DB.batch([
      env.DB.prepare("DELETE FROM telemetry WHERE received_utc < datetime('now', '-90 days')"),
      env.DB.prepare("DELETE FROM feedback WHERE created_utc < datetime('now', '-90 days')"),
      env.DB.prepare("DELETE FROM usage WHERE day < date('now', '-60 days')"),
    ]);
  },
};

// ------------------------------------------------------------------ routes

async function me(request: Request, env: Env): Promise<Response> {
  const userId = await requireUser(request, env);
  const row = await env.DB.prepare("SELECT questions FROM usage WHERE user_id = ? AND day = ?")
    .bind(userId, today())
    .first<{ questions: number }>();
  return json({ userId, questionsToday: row?.questions ?? 0, dailyLimit: dailyLimit(env) });
}

async function chat(request: Request, env: Env): Promise<Response> {
  const userId = await requireUser(request, env);
  const body = await readJson<{ messages?: ChatMessage[]; briefing?: string }>(request, MAX_CHAT_BYTES);
  const { messages, steps } = validateMessages(body.messages);
  const briefing = typeof body.briefing === "string" ? body.briefing.slice(0, MAX_BRIEFING_CHARS) : "";

  // A new question counts against the daily question quota; the tool steps that follow it count
  // against a larger step quota, so a question that needs several tool rounds costs one question.
  const column = steps === 0 ? "questions" : "steps";
  const limit = steps === 0 ? dailyLimit(env) : dailyLimit(env) * STEPS_PER_QUESTION;
  const counted = await env.DB.prepare(
    `INSERT INTO usage (user_id, day, ${column}) VALUES (?1, ?2, 1)
     ON CONFLICT (user_id, day) DO UPDATE SET ${column} = ${column} + 1 WHERE ${column} < ?3
     RETURNING ${column} AS n`,
  )
    .bind(userId, today(), limit)
    .first<{ n: number }>();
  if (!counted) {
    return json({ error: `You've used today's ${dailyLimit(env)} free questions. They reset at midnight UTC.` }, 429);
  }

  const client = new Anthropic({ apiKey: env.ANTHROPIC_API_KEY });
  const encoder = new TextEncoder();
  const send = (controller: ReadableStreamDefaultController<Uint8Array>, payload: unknown) =>
    controller.enqueue(encoder.encode(`data: ${JSON.stringify(payload)}\n\n`));

  const stream = new ReadableStream<Uint8Array>({
    async start(controller) {
      try {
        // Tools and instructions never change and are cached across questions; the briefing is cached
        // across the tool steps of one question. Server-side fallback re-serves a declined request on
        // another model inside the same call. On the last allowed step the model must answer.
        const params = {
          model: env.MODEL || "claude-opus-5-5",
          max_tokens: 8000,
          betas: ["server-side-fallback-2026-07-01"],
          fallbacks: "default",
          output_config: { effort: "medium" },
          tools: TOOLS,
          ...(steps >= MAX_STEPS - 1 ? { tool_choice: { type: "none" } } : {}),
          system: [
            { type: "text", text: INSTRUCTIONS.trim(), cache_control: { type: "ephemeral" } },
            { type: "text", text: `Briefing of what the app has measured:\n\n${briefing}`, cache_control: { type: "ephemeral" } },
          ],
          messages,
        } as unknown as Anthropic.Beta.Messages.MessageCreateParamsStreaming;

        const claude = client.beta.messages.stream(params);
        for await (const event of claude) {
          if (event.type === "content_block_delta" && event.delta.type === "text_delta") {
            send(controller, { text: event.delta.text });
          }
        }
        const final = await claude.finalMessage();
        if (final.stop_reason === "refusal") {
          send(controller, { text: "\n\n(The assistant declined to answer this question.)" });
        }
        // The app needs the whole assistant turn (tool calls, and thinking with its signature) to run the
        // tools and send it back unchanged with their results.
        const content = (final.content as unknown as Block[]).filter((b) => ASSISTANT_BLOCKS.has(b.type));
        send(controller, { assistant: content, stop_reason: final.stop_reason });
        send(controller, { done: true });
      } catch (err) {
        console.error(err);
        send(controller, { error: friendlyError(err) });
      } finally {
        controller.close();
      }
    },
  });

  return new Response(stream, {
    headers: { "Content-Type": "text/event-stream", "Cache-Control": "no-cache" },
  });
}

async function telemetry(request: Request, env: Env): Promise<Response> {
  const userId = await optionalUser(request, env);
  const body = await readJson<{
    deviceId?: string;
    snapshot?: unknown;
    feedback?: { target?: string; kind?: string; helpful?: boolean; atUtc?: string }[];
  }>(request, MAX_TELEMETRY_BYTES);

  const deviceId = body.deviceId ?? "";
  if (!/^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(deviceId)) {
    throw new HttpError(400, "deviceId must be a UUID.");
  }

  const recent = await env.DB.prepare(
    "SELECT COUNT(*) AS n FROM telemetry WHERE device_id = ? AND received_utc > datetime('now', '-1 day')",
  )
    .bind(deviceId)
    .first<{ n: number }>();
  if ((recent?.n ?? 0) >= MAX_TELEMETRY_PER_DEVICE_PER_DAY) {
    throw new HttpError(429, "Too many uploads from this device today.");
  }

  const now = new Date().toISOString();
  const statements: D1PreparedStatement[] = [];
  if (body.snapshot !== undefined) {
    statements.push(
      env.DB.prepare("INSERT INTO telemetry (device_id, user_id, received_utc, payload) VALUES (?, ?, ?, ?)")
        .bind(deviceId, userId, now, JSON.stringify(body.snapshot)),
    );
  }
  for (const vote of (body.feedback ?? []).slice(0, 200)) {
    const target = vote.target ?? "";
    if (!["finding", "health", "answer"].includes(target) || typeof vote.helpful !== "boolean") continue;
    statements.push(
      env.DB.prepare(
        "INSERT INTO feedback (device_id, user_id, created_utc, target, kind, helpful) VALUES (?, ?, ?, ?, ?, ?)",
      ).bind(deviceId, userId, vote.atUtc ?? now, target, String(vote.kind ?? "").slice(0, 64), vote.helpful ? 1 : 0),
    );
  }
  if (statements.length > 0) await env.DB.batch(statements);

  return json({ accepted: statements.length });
}

// ------------------------------------------------------------------ auth

class HttpError extends Error {
  constructor(public status: number, message: string) {
    super(message);
  }
}

type Discovery = { jwks_uri: string; userinfo_endpoint?: string; issuer: string };
let discoveryCache: { issuer: string; doc: Discovery; at: number } | null = null;
let jwks: ReturnType<typeof createRemoteJWKSet> | null = null;
const userinfoCache = new Map<string, { sub: string; until: number }>();

async function discovery(env: Env): Promise<Discovery> {
  if (!env.OIDC_ISSUER) throw new HttpError(503, "Sign-in is not configured on the server.");
  if (discoveryCache && discoveryCache.issuer === env.OIDC_ISSUER && Date.now() - discoveryCache.at < 3_600_000) {
    return discoveryCache.doc;
  }
  const res = await fetch(`${env.OIDC_ISSUER.replace(/\/$/, "")}/.well-known/openid-configuration`);
  if (!res.ok) throw new HttpError(503, "Could not reach the sign-in provider.");
  const doc = (await res.json()) as Discovery;
  discoveryCache = { issuer: env.OIDC_ISSUER, doc, at: Date.now() };
  jwks = createRemoteJWKSet(new URL(doc.jwks_uri));
  return doc;
}

/**
 * Returns the user id for a bearer token. Signed tokens (JWT) are verified against the provider's
 * published keys; opaque tokens are checked with the provider's userinfo endpoint and cached briefly.
 */
async function verifyToken(token: string, env: Env): Promise<string> {
  const doc = await discovery(env);

  if (token.split(".").length === 3) {
    const { payload } = await jwtVerify(token, jwks!, { issuer: doc.issuer }).catch(() => {
      throw new HttpError(401, "Your sign-in has expired. Sign in again.");
    });
    // Desktop tokens name the OAuth client; website session tokens name the site's origin in azp.
    const client = (payload.azp ?? payload.client_id) as string | undefined;
    if (env.OIDC_CLIENT_ID && client && client !== env.OIDC_CLIENT_ID && client !== env.SITE_ORIGIN) {
      throw new HttpError(401, "This token was issued to a different app.");
    }
    if (!payload.sub) throw new HttpError(401, "The token has no user.");
    return payload.sub;
  }

  const cached = userinfoCache.get(token);
  if (cached && cached.until > Date.now()) return cached.sub;
  if (!doc.userinfo_endpoint) throw new HttpError(401, "Unsupported token.");
  const res = await fetch(doc.userinfo_endpoint, { headers: { Authorization: `Bearer ${token}` } });
  if (!res.ok) throw new HttpError(401, "Your sign-in has expired. Sign in again.");
  const info = (await res.json()) as { sub?: string };
  if (!info.sub) throw new HttpError(401, "The token has no user.");
  userinfoCache.set(token, { sub: info.sub, until: Date.now() + 5 * 60_000 });
  return info.sub;
}

function bearer(request: Request): string | null {
  const header = request.headers.get("Authorization") ?? "";
  return header.startsWith("Bearer ") ? header.slice(7).trim() : null;
}

async function requireUser(request: Request, env: Env): Promise<string> {
  const token = bearer(request);
  if (!token) throw new HttpError(401, "Sign in to use the assistant.");
  return verifyToken(token, env);
}

async function optionalUser(request: Request, env: Env): Promise<string | null> {
  const token = bearer(request);
  if (!token) return null;
  try {
    return await verifyToken(token, env);
  } catch {
    return null; // telemetry still counts, just without an account
  }
}

// ------------------------------------------------------------------ chat validation

/**
 * Checks the conversation the app sends. Earlier turns are plain text. The current question may be
 * followed by tool steps: an assistant turn with tool calls (as this server returned it), then a user
 * turn with exactly one result per call. Returns the messages and how many tool steps follow the question.
 */
function validateMessages(raw: ChatMessage[] | undefined): { messages: ChatMessage[]; steps: number } {
  const bad = (message: string) => new HttpError(400, message);
  if (!Array.isArray(raw) || raw.length === 0) throw bad("Ask a question first.");
  if (raw.length > MAX_MESSAGES) throw bad("The conversation is too long.");

  const messages: ChatMessage[] = [];
  let pendingToolIds: string[] | null = null;
  for (const m of raw) {
    if (m?.role !== "user" && m?.role !== "assistant") throw bad("Messages must come from the user or the assistant.");
    const expected = messages.length % 2 === 0 ? "user" : "assistant";
    if (m.role !== expected) throw bad("The conversation must start with the user and alternate.");

    if (typeof m.content === "string") {
      if (m.content.length === 0 || m.content.length > MAX_MESSAGE_CHARS) {
        throw bad(`Messages must be between 1 and ${MAX_MESSAGE_CHARS} characters.`);
      }
      if (pendingToolIds) throw bad("Tool calls must be answered with their results.");
      messages.push({ role: m.role, content: m.content });
      continue;
    }
    if (!Array.isArray(m.content) || m.content.length === 0 || m.content.length > 20) throw bad("Invalid message content.");

    if (m.role === "assistant") {
      const blocks = m.content.map(checkAssistantBlock);
      const ids = blocks.filter((b) => b.type === "tool_use").map((b) => b.id as string);
      pendingToolIds = ids.length > 0 ? ids : null;
      messages.push({ role: "assistant", content: blocks });
    } else {
      if (!pendingToolIds) throw bad("Tool results must follow the assistant's tool calls.");
      const results = m.content.map(checkToolResult);
      const got = results.map((r) => r.tool_use_id as string).sort().join(",");
      if (got !== [...pendingToolIds].sort().join(",")) throw bad("Every tool call needs exactly one result.");
      pendingToolIds = null;
      messages.push({ role: "user", content: results });
    }
  }

  if (messages[messages.length - 1].role !== "user" || pendingToolIds) {
    throw bad("The conversation must end with the user's message or tool results.");
  }

  // Tool steps since the question: assistant turns after the last plain user message.
  let lastQuestion = -1;
  messages.forEach((m, i) => {
    if (m.role === "user" && typeof m.content === "string") lastQuestion = i;
  });
  if (lastQuestion < 0) throw bad("Ask a question first.");
  if (messages.slice(0, lastQuestion).some((m) => typeof m.content !== "string")) {
    throw bad("Only the current question may include tool steps.");
  }
  const steps = messages.slice(lastQuestion).filter((m) => m.role === "assistant").length;
  if (steps >= MAX_STEPS) throw bad("This question needed too many steps. Ask it again more specifically.");
  return { messages, steps };
}

function checkAssistantBlock(b: Block): Block {
  const bad = new HttpError(400, "Invalid assistant content.");
  if (!b || typeof b !== "object") throw bad;
  switch (b.type) {
    case "text":
      if (typeof b.text !== "string" || b.text.length > MAX_BLOCK_CHARS) throw bad;
      return { type: "text", text: b.text };
    case "tool_use":
      if (typeof b.id !== "string" || typeof b.name !== "string" || !TOOL_NAMES.has(b.name)) throw bad;
      if (typeof b.input !== "object" || b.input === null || JSON.stringify(b.input).length > 2000) throw bad;
      return { type: "tool_use", id: b.id, name: b.name, input: b.input };
    case "thinking":
      if (typeof b.thinking !== "string" || typeof b.signature !== "string" || b.thinking.length > MAX_THINKING_CHARS) throw bad;
      return { type: "thinking", thinking: b.thinking, signature: b.signature };
    case "redacted_thinking":
      if (typeof b.data !== "string" || b.data.length > MAX_THINKING_CHARS) throw bad;
      return { type: "redacted_thinking", data: b.data };
    default:
      throw bad;
  }
}

function checkToolResult(b: Block): Block {
  if (!b || b.type !== "tool_result" || typeof b.tool_use_id !== "string" || typeof b.content !== "string" || b.content.length > MAX_BLOCK_CHARS) {
    throw new HttpError(400, "Invalid tool result.");
  }
  return { type: "tool_result", tool_use_id: b.tool_use_id, content: b.content };
}

// ------------------------------------------------------------------ helpers

async function readJson<T>(request: Request, maxBytes: number): Promise<T> {
  const length = Number(request.headers.get("Content-Length") ?? "0");
  if (length > maxBytes) throw new HttpError(413, "Request too large.");
  const text = await request.text();
  if (text.length > maxBytes) throw new HttpError(413, "Request too large.");
  try {
    return JSON.parse(text) as T;
  } catch {
    throw new HttpError(400, "Invalid JSON.");
  }
}

function friendlyError(err: unknown): string {
  if (err instanceof Anthropic.RateLimitError) return "The assistant is busy. Try again in a minute.";
  if (err instanceof Anthropic.APIConnectionError) return "The assistant could not be reached. Try again.";
  return "The assistant could not answer right now. Try again.";
}

function dailyLimit(env: Env): number {
  const n = Number(env.DAILY_FREE_QUESTIONS);
  return Number.isFinite(n) && n > 0 ? Math.floor(n) : 30;
}

function today(): string {
  return new Date().toISOString().slice(0, 10);
}

/** Adds CORS headers when the request comes from the configured website origin; other origins get none. */
function withCors(response: Response, request: Request, env: Env): Response {
  const origin = request.headers.get("Origin");
  if (!env.SITE_ORIGIN || origin !== env.SITE_ORIGIN) return response;
  const headers = new Headers(response.headers);
  headers.set("Access-Control-Allow-Origin", origin);
  headers.set("Access-Control-Allow-Methods", "GET, OPTIONS");
  headers.set("Access-Control-Allow-Headers", "Authorization");
  headers.set("Access-Control-Max-Age", "600");
  headers.set("Vary", "Origin");
  return new Response(response.body, { status: response.status, headers });
}

function json(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json" } });
}
