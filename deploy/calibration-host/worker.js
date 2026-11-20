const samplePath = "/v1/samples";
const automaticPath = "/v2/samples";
const maxBytes = 1024;
const models = new Set([
  "gpt-6.2-astra", "gpt-6.2-sol", "gpt-6-astra", "gpt-6.1-sol", "gpt-6.1-terra",
  "gpt-6-sol", "gpt-6-luna", "gpt-5.6-sol", "gpt-5.6-terra", "gpt-5.6-luna",
]);
const efforts = new Set(["none", "minimal", "low", "medium", "high", "xhigh", "max", "ultra", "unknown"]);
const speeds = new Set(["standard", "fast", "ultrafast", "unknown"]);
const counts = ["input", "output", "cacheRead", "cacheWrite", "reasoning"];
const fields = ["receipt", "model", "effort", "speed", ...counts];
const automaticFields = [...fields, "points", "snapshotTokens"];
const uuidV4 = /^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/;

function valid(s, automatic) {
  const allowed = automatic ? automaticFields : fields;
  return s !== null && typeof s === "object" && !Array.isArray(s) &&
    Object.keys(s).length === allowed.length && allowed.every(key => Object.hasOwn(s, key)) &&
    (!automatic || (Number.isInteger(s.points) && s.points >= 1 && s.points <= 3 &&
      (s.snapshotTokens === null || Number.isSafeInteger(s.snapshotTokens)))) &&
    typeof s.receipt === "string" && s.receipt.length === 36 && uuidV4.test(s.receipt) &&
    models.has(s.model) && efforts.has(s.effort) && speeds.has(s.speed) &&
    counts.every(key => Number.isSafeInteger(s[key]) && s[key] >= 0) &&
    Number.isSafeInteger(s.input + s.output) && s.input + s.output > 0 &&
    s.cacheRead <= s.input && s.reasoning <= s.output;
}

function reply(body, status) {
  return Response.json(body, { status, headers: { "Cache-Control": "no-store" } });
}

async function receive(request, db, exclusion, automatic) {
  if (request.headers.get("Content-Type")?.split(";")[0].trim().toLowerCase() !== "application/json")
    return reply({ error: "expected_json" }, 415);
  if (Number(request.headers.get("Content-Length")) > maxBytes)
    return reply({ error: "too_large" }, 413);
  if (!request.body) return reply({ error: "invalid_sample" }, 400);

  const reader = request.body.getReader();
  const buffer = new Uint8Array(maxBytes);
  let used = 0;
  let sample;
  try {
    while (true) {
      const { done, value } = await reader.read();
      if (done) break;
      if (value.length > maxBytes - used) {
        await reader.cancel();
        return reply({ error: "too_large" }, 413);
      }
      buffer.set(value, used);
      used += value.length;
    }
    sample = JSON.parse(new TextDecoder("utf-8", { fatal: true }).decode(buffer.subarray(0, used)));
  } catch {
    return reply({ error: "invalid_sample" }, 400);
  } finally {
    reader.releaseLock();
  }
  if (!valid(sample, automatic)) return reply({ error: "invalid_sample" }, 422);

  const values = fields.map(key => sample[key]);
  values.push(automatic ? 2 : 1, automatic ? sample.points : 1, automatic ? sample.snapshotTokens : null);
  try {
    const matchFields = `receipt=? AND model=? AND effort=? AND speed=? AND input=? AND output=?
      AND cache_read=? AND cache_write=? AND reasoning=? AND protocol=? AND quota_points=? AND snapshot_tokens IS ?`;
    const insert = db.prepare(`INSERT INTO calibration_samples_v1
      (receipt,model,effort,speed,input,output,cache_read,cache_write,reasoning,protocol,quota_points,snapshot_tokens,review_status)
      VALUES (?,?,?,?,?,?,?,?,?,?,?,?,?) ON CONFLICT(receipt) DO NOTHING`);
    if (exclusion) {
      // Match the original sample as well as its random receipt. A report can only exclude.
      const update = db.prepare(`UPDATE calibration_samples_v1
        SET review_status=CASE WHEN review_status='excluded_multitask' THEN review_status ELSE ? END
        WHERE ${matchFields} RETURNING receipt, review_status`).bind(exclusion, ...values);
      // A lost upload reply can race with its invalidation. Reserve the receipt as excluded in either order.
      const row = exclusion === "excluded_uncertain"
        ? (await db.batch([insert.bind(...values, exclusion), update]))[1].results[0]
        : await update.first();
      return row ? reply({ status: "stored", receipt: row.receipt, review: row.review_status }, 200)
        : reply({ error: "sample_not_found" }, 404);
    }
    let review = "pending";
    if (automatic) {
      const countersAgree = sample.snapshotTokens === sample.input + sample.output;
      review = countersAgree && sample.cacheWrite <= sample.input && sample.effort !== "unknown" && sample.speed !== "unknown"
        ? "approved" : "excluded_uncertain";
    }
    const match = db.prepare(`SELECT receipt, review_status FROM calibration_samples_v1 WHERE ${matchFields}`).bind(...values);
    const [write, read] = await db.batch([insert.bind(...values, review), match]);
    if (!write.success || !read.success) return reply({ error: "storage_unavailable" }, 503);
    if (read.results.length !== 1) return reply({ error: "receipt_conflict" }, 409);
    const row = read.results[0];
    return reply({ status: "stored", receipt: row.receipt, review: row.review_status }, write.meta.changes ? 201 : 200);
  } catch {
    return reply({ error: "storage_unavailable" }, 503);
  }
}

export default {
  async fetch(request, env) {
    const path = new URL(request.url).pathname;
    const invalidated = path === automaticPath + "/invalidate";
    const automatic = path === automaticPath || path === automaticPath + "/exclude-multitask" || invalidated;
    const exclusion = invalidated ? "excluded_uncertain" :
      path.endsWith("/exclude-multitask") ? "excluded_multitask" : null;
    if (path === samplePath || path === samplePath + "/exclude-multitask" || automatic) {
      if (request.method === "POST") return receive(request, env.DB, exclusion, automatic);
      return new Response(null, { status: 405, headers: { Allow: "POST" } });
    }
    if (path !== "/" && path !== "/health") return new Response(null, { status: 404 });
    if (request.method === "HEAD") return new Response(null);
    if (request.method !== "GET") return new Response(null, { status: 405, headers: { Allow: "GET, HEAD" } });
    return reply({ service: "Codex Tracker", uploadsEnabled: true }, 200);
  },
};
