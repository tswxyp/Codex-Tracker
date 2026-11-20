export default {
  fetch(request) {
    if (request.method !== "GET" && request.method !== "HEAD") {
      return new Response(null, { status: 405, headers: { Allow: "GET, HEAD" } });
    }
    const path = new URL(request.url).pathname;
    if (path !== "/" && path !== "/health") {
      return new Response(null, { status: 404 });
    }
    const status = { service: "Codex Tracker", uploadsEnabled: false };
    return new Response(request.method === "HEAD" ? null : JSON.stringify(status), {
      headers: { "Content-Type": "application/json; charset=utf-8", "Cache-Control": "no-store" },
    });
  },
};
