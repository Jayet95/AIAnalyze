import Anthropic from "@anthropic-ai/sdk";
import { createRemoteJWKSet, jwtVerify } from "jose";

/**
 * AIAnalyze API.
 *   GET  /v1/me         signed-in user's quota
 *   POST /v1/chat       relay a question to Claude with the operator's API key (signed-in, daily quota)
 *   POST /v1/telemetry  opt-in diagnostics snapshot and feedback votes (anonymous or signed-in)
 */
export interface Env {
  DB: D1Database;
  ANTHROPIC_API_KEY: string;
  OIDC_ISSUER: string;
  OIDC_CLIENT_ID?: string;
  MODEL: string;
  DAILY_FREE_QUESTIONS: string;
}

const MAX_MESSAGES = 20;
const MAX_MESSAGE_CHARS = 4000;
const MAX_BRIEFING_CHARS = 60_000;
const MAX_TELEMETRY_BYTES = 64 * 1024;
const MAX_TELEMETRY_PER_DEVICE_PER_DAY = 50;

// Kept in sync with AIAnalyze/Chat/ClaudeChat.cs. The server owns the instructions, so a modified client
// cannot turn the relay into a general-purpose assistant on the operator's key.
const INSTRUCTIONS = `You are the assistant inside AIAnalyze, a diagnostics app running locally on the user's Windows PC.
With each question you receive a briefing of what the app has measured on this PC: hardware and drivers,
system health checks (crashes and blue screens, hardware errors, graphics driver resets, crashing
programs, failing services, failed updates, disk space and drive health, memory, busy programs, uptime,
battery), current sensor readings, recent alerts, recent game sessions with their findings, and, when
the user has one open, a summary of a single session. Use it to help with any hardware or software
problem on this PC: slowness, freezes, crashes, errors, noise and heat, storage, updates, drivers, and
performance in games and other programs.

- Reply in the language the user writes in.
- Base every claim on the briefing and quote the numbers you rely on. When the briefing does not show
  something, say so and say what would show it, rather than guessing.
- Write for a gamer, not an engineer: lead with the answer, then the evidence, then what to do.
- Suggest concrete steps the user can take themselves, such as in-game settings, cooling, power plan,
  closing background programs, or updating a driver from the vendor's official site. Mention a restore
  point before driver changes.
- The app only reads data. It cannot change settings or install anything, so never say you changed something.
- Keep it short: a few sentences or a short list, unless the user asks for more detail.
- Only help with this PC and its software. Politely decline unrelated requests.`;

type ChatTurn = { role: "user" | "assistant"; content: string };

export default {
  async fetch(request: Request, env: Env, ctx: ExecutionContext): Promise<Response> {
    const url = new URL(request.url);
    try {
      if (request.method === "GET" && url.pathname === "/v1/me") return await me(request, env);
      if (request.method === "POST" && url.pathname === "/v1/chat") return await chat(request, env);
      if (request.method === "POST" && url.pathname === "/v1/telemetry") return await telemetry(request, env);
      return json({ error: "Not found." }, 404);
    } catch (err) {
      if (err instanceof HttpError) return json({ error: err.message }, err.status);
      console.error(err);
      return json({ error: "Something went wrong on the server." }, 500);
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
  const body = await readJson<{ messages?: ChatTurn[]; briefing?: string }>(request, 256 * 1024);
  const messages = validateMessages(body.messages);
  const briefing = typeof body.briefing === "string" ? body.briefing.slice(0, MAX_BRIEFING_CHARS) : "";

  // Count the question atomically; no row back means today's quota is used up.
  const counted = await env.DB.prepare(
    `INSERT INTO usage (user_id, day, questions) VALUES (?1, ?2, 1)
     ON CONFLICT (user_id, day) DO UPDATE SET questions = questions + 1 WHERE questions < ?3
     RETURNING questions`,
  )
    .bind(userId, today(), dailyLimit(env))
    .first<{ questions: number }>();
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
        // Server-side fallback re-serves a declined request on another model inside the same call.
        const params = {
          model: env.MODEL || "claude-opus-5-5",
          max_tokens: 8000,
          betas: ["server-side-fallback-2026-07-01"],
          fallbacks: "default",
          output_config: { effort: "medium" },
          system: [
            { type: "text", text: INSTRUCTIONS, cache_control: { type: "ephemeral" } },
            { type: "text", text: `Briefing of what the app has measured:\n\n${briefing}` },
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
    const client = (payload.azp ?? payload.client_id) as string | undefined;
    if (env.OIDC_CLIENT_ID && client && client !== env.OIDC_CLIENT_ID) {
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

// ------------------------------------------------------------------ helpers

function validateMessages(raw: ChatTurn[] | undefined): ChatTurn[] {
  if (!Array.isArray(raw) || raw.length === 0) throw new HttpError(400, "Ask a question first.");
  const messages = raw.slice(-MAX_MESSAGES);
  for (const m of messages) {
    if ((m.role !== "user" && m.role !== "assistant") || typeof m.content !== "string") {
      throw new HttpError(400, "Messages must come from the user or the assistant.");
    }
    if (m.content.length === 0 || m.content.length > MAX_MESSAGE_CHARS) {
      throw new HttpError(400, `Messages must be between 1 and ${MAX_MESSAGE_CHARS} characters.`);
    }
  }
  if (messages[0].role !== "user" || messages[messages.length - 1].role !== "user") {
    throw new HttpError(400, "The conversation must start and end with the user's message.");
  }
  return messages.map((m) => ({ role: m.role, content: m.content }));
}

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

function json(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json" } });
}
