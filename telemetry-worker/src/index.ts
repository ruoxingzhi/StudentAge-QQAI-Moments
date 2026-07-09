export interface Env {
  qqai_telemetry: D1Database;
  INGEST_TOKEN?: string;
  ADMIN_TOKEN?: string;
  IP_SALT?: string;
}

type JsonObject = Record<string, unknown>;

const MAX_BODY_BYTES = 64 * 1024;
const MAX_EXPORT_LIMIT = 500;

export default {
  async fetch(request: Request, env: Env): Promise<Response> {
    const url = new URL(request.url);

    if (request.method === "OPTIONS") {
      return withCors(new Response(null, { status: 204 }));
    }

    if (request.method === "GET" && url.pathname === "/health") {
      return json({ ok: true, service: "studentage-qqai-telemetry" });
    }

    if (request.method === "POST" && url.pathname === "/ingest") {
      return withCors(await ingest(request, env, url));
    }

    if (request.method === "GET" && url.pathname === "/stats") {
      return withCors(await stats(env, url));
    }

    if (request.method === "GET" && url.pathname === "/export") {
      return withCors(await exportRows(env, url));
    }

    return json({ ok: false, error: "not_found" }, 404);
  }
};

async function ingest(request: Request, env: Env, url: URL): Promise<Response> {
  if (!hasValidIngestToken(env, url)) {
    return json({ ok: false, error: "unauthorized" }, 401);
  }

  const contentLength = Number(request.headers.get("content-length") || "0");
  if (contentLength > MAX_BODY_BYTES) {
    return json({ ok: false, error: "payload_too_large" }, 413);
  }

  const body = await request.text();
  if (new TextEncoder().encode(body).byteLength > MAX_BODY_BYTES) {
    return json({ ok: false, error: "payload_too_large" }, 413);
  }

  let payload: JsonObject;
  try {
    payload = JSON.parse(body) as JsonObject;
  } catch {
    return json({ ok: false, error: "invalid_json" }, 400);
  }

  const validationError = validatePayload(payload);
  if (validationError) {
    return json({ ok: false, error: validationError }, 400);
  }

  const job = objectField(payload, "job");
  const generation = objectField(payload, "generation");
  const metrics = objectField(payload, "text_metrics");

  const ipHash = await hashText(env, request.headers.get("cf-connecting-ip") || "");
  const uaHash = await hashText(env, request.headers.get("user-agent") || "");

  await env.qqai_telemetry.prepare(
    `INSERT INTO telemetry_events (
      plugin_version, install_id, event_name, job_type,
      author_role_id, post_author_role_id, target_role_id,
      raw_text_included, final_state, quality_issue, model,
      endpoint_kind, request_attempts, http_status,
      source_len_bucket, parent_len_bucket, ai_len_bucket,
      source_hash, parent_hash, ai_hash,
      ip_hash, user_agent_hash, payload_json
    ) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)`
  )
    .bind(
      stringField(payload, "plugin_version"),
      stringField(payload, "install_id"),
      stringField(payload, "event"),
      stringField(job, "type"),
      numberOrNull(job, "author_role_id"),
      numberOrNull(job, "post_author_role_id"),
      numberOrNull(job, "target_role_id"),
      boolField(payload, "raw_text_included") ? 1 : 0,
      stringField(generation, "final_state"),
      stringField(generation, "quality_issue"),
      stringField(payload, "model"),
      stringField(generation, "endpoint_kind"),
      numberOrZero(generation, "request_attempts"),
      numberOrZero(generation, "http_status"),
      stringField(metrics, "source_len_bucket"),
      stringField(metrics, "parent_len_bucket"),
      stringField(metrics, "ai_len_bucket"),
      stringField(metrics, "source_hash"),
      stringField(metrics, "parent_hash"),
      stringField(metrics, "ai_hash"),
      ipHash,
      uaHash,
      JSON.stringify(payload)
    )
    .run();

  return json({ ok: true });
}

async function stats(env: Env, url: URL): Promise<Response> {
  if (!hasValidAdminToken(env, url)) {
    return json({ ok: false, error: "unauthorized" }, 401);
  }

  const total = await env.qqai_telemetry.prepare("SELECT COUNT(*) AS count FROM telemetry_events").first<{ count: number }>();
  const raw = await env.qqai_telemetry.prepare("SELECT COUNT(*) AS count FROM telemetry_events WHERE raw_text_included=1").first<{ count: number }>();
  const byJob = await env.qqai_telemetry.prepare(
    "SELECT job_type, COUNT(*) AS count FROM telemetry_events GROUP BY job_type ORDER BY count DESC"
  ).all();
  const byState = await env.qqai_telemetry.prepare(
    "SELECT final_state, COUNT(*) AS count FROM telemetry_events GROUP BY final_state ORDER BY count DESC"
  ).all();

  return json({
    ok: true,
    total: total?.count || 0,
    raw_text_rows: raw?.count || 0,
    by_job: byJob.results,
    by_state: byState.results
  });
}

async function exportRows(env: Env, url: URL): Promise<Response> {
  if (!hasValidAdminToken(env, url)) {
    return json({ ok: false, error: "unauthorized" }, 401);
  }

  const limit = clamp(Number(url.searchParams.get("limit") || "100"), 1, MAX_EXPORT_LIMIT);
  const rows = await env.qqai_telemetry.prepare(
    `SELECT id, received_at, plugin_version, install_id, event_name, job_type,
            author_role_id, post_author_role_id, target_role_id,
            raw_text_included, final_state, quality_issue, model,
            endpoint_kind, request_attempts, http_status,
            source_len_bucket, parent_len_bucket, ai_len_bucket,
            source_hash, parent_hash, ai_hash, payload_json
       FROM telemetry_events
      ORDER BY id DESC
      LIMIT ?`
  ).bind(limit).all();

  return json({ ok: true, rows: rows.results });
}

function validatePayload(payload: JsonObject): string | null {
  if (numberOrZero(payload, "schema") <= 0) return "missing_schema";
  if (!stringField(payload, "install_id")) return "missing_install_id";
  if (!objectField(payload, "job")) return "missing_job";
  if (!objectField(payload, "generation")) return "missing_generation";
  if (!objectField(payload, "text_metrics")) return "missing_text_metrics";
  return null;
}

function hasValidIngestToken(env: Env, url: URL): boolean {
  const expected = env.INGEST_TOKEN || "";
  if (!expected) return false;
  return safeEqual(url.searchParams.get("token") || "", expected);
}

function hasValidAdminToken(env: Env, url: URL): boolean {
  const expected = env.ADMIN_TOKEN || "";
  if (!expected) return false;
  return safeEqual(url.searchParams.get("admin_token") || "", expected);
}

function safeEqual(a: string, b: string): boolean {
  if (a.length !== b.length) return false;
  let diff = 0;
  for (let i = 0; i < a.length; i++) {
    diff |= a.charCodeAt(i) ^ b.charCodeAt(i);
  }
  return diff === 0;
}

async function hashText(env: Env, value: string): Promise<string> {
  if (!value) return "";
  const salt = env.IP_SALT || env.INGEST_TOKEN || "studentage-qqai";
  const data = new TextEncoder().encode(`${salt}\n${value}`);
  const digest = await crypto.subtle.digest("SHA-256", data);
  return [...new Uint8Array(digest)]
    .slice(0, 12)
    .map((b) => b.toString(16).padStart(2, "0"))
    .join("");
}

function objectField(obj: JsonObject | null, key: string): JsonObject {
  const value = obj?.[key];
  return value && typeof value === "object" && !Array.isArray(value) ? (value as JsonObject) : {};
}

function stringField(obj: JsonObject | null, key: string): string {
  const value = obj?.[key];
  if (typeof value === "string") return value.slice(0, 4000);
  if (typeof value === "number" || typeof value === "boolean") return String(value);
  return "";
}

function numberOrNull(obj: JsonObject | null, key: string): number | null {
  const value = obj?.[key];
  return typeof value === "number" && Number.isFinite(value) ? value : null;
}

function numberOrZero(obj: JsonObject | null, key: string): number {
  return numberOrNull(obj, key) || 0;
}

function boolField(obj: JsonObject | null, key: string): boolean {
  return obj?.[key] === true;
}

function clamp(value: number, min: number, max: number): number {
  if (!Number.isFinite(value)) return min;
  return Math.max(min, Math.min(max, Math.floor(value)));
}

function json(data: unknown, status = 200): Response {
  return withCors(new Response(JSON.stringify(data), {
    status,
    headers: {
      "content-type": "application/json; charset=utf-8"
    }
  }));
}

function withCors(response: Response): Response {
  const headers = new Headers(response.headers);
  headers.set("access-control-allow-origin", "*");
  headers.set("access-control-allow-methods", "GET,POST,OPTIONS");
  headers.set("access-control-allow-headers", "content-type");
  return new Response(response.body, {
    status: response.status,
    statusText: response.statusText,
    headers
  });
}
