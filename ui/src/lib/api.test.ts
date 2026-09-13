import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { api, ApiError } from "./api";

const fetchMock = vi.fn<typeof fetch>();

function jsonResponse(status: number, body: unknown, statusText = ""): Response {
  return new Response(body === undefined ? null : JSON.stringify(body), {
    status,
    statusText,
    headers: { "Content-Type": "application/json" },
  });
}

beforeEach(() => {
  fetchMock.mockReset();
  vi.stubGlobal("fetch", fetchMock);
});

afterEach(() => {
  vi.unstubAllGlobals();
});

describe("api client", () => {
  it("parses successful JSON responses and builds query strings", async () => {
    fetchMock.mockResolvedValueOnce(jsonResponse(200, [{ id: 1 }]));
    const list = await api.tickets.list({ projectId: 3, status: "ready" });
    expect(list).toEqual([{ id: 1 }]);
    const [url, init] = fetchMock.mock.calls[0];
    expect(url).toBe("/api/tickets?projectId=3&status=ready");
    expect(init?.method).toBe("GET");
  });

  it("omits undefined query params", async () => {
    fetchMock.mockResolvedValueOnce(jsonResponse(200, []));
    await api.tickets.list({});
    expect(fetchMock.mock.calls[0][0]).toBe("/api/tickets");
  });

  it("sends JSON bodies with content-type", async () => {
    fetchMock.mockResolvedValueOnce(jsonResponse(200, { id: 7, status: "ready" }));
    await api.tickets.move(7, "ready");
    const [url, init] = fetchMock.mock.calls[0];
    expect(url).toBe("/api/tickets/7/move");
    expect(init?.method).toBe("POST");
    expect((init?.headers as Record<string, string>)["Content-Type"]).toBe("application/json");
    expect(init?.body).toBe(JSON.stringify({ status: "ready" }));
  });

  it("maps {error, code} bodies to ApiError with status and code", async () => {
    fetchMock.mockResolvedValueOnce(jsonResponse(400, { error: "Cannot move from in_progress to done", code: "invalid_transition" }));
    const err = await api.tickets.move(1, "done").catch((e: unknown) => e);
    expect(err).toBeInstanceOf(ApiError);
    const apiErr = err as ApiError;
    expect(apiErr.status).toBe(400);
    expect(apiErr.code).toBe("invalid_transition");
    expect(apiErr.message).toBe("Cannot move from in_progress to done");
    expect(apiErr.isInvalidTransition).toBe(true);
    expect(apiErr.isRunActive).toBe(false);
  });

  it("recognises 409 run_active", async () => {
    fetchMock.mockResolvedValueOnce(jsonResponse(409, { error: "A run is already active", code: "run_active" }));
    const err = (await api.tickets.refine(1).catch((e: unknown) => e)) as ApiError;
    expect(err.status).toBe(409);
    expect(err.isRunActive).toBe(true);
  });

  it("falls back to a synthetic code for non-JSON error bodies", async () => {
    fetchMock.mockResolvedValueOnce(new Response("<html>Bad Gateway</html>", { status: 502, statusText: "Bad Gateway" }));
    const err = (await api.projects.list().catch((e: unknown) => e)) as ApiError;
    expect(err).toBeInstanceOf(ApiError);
    expect(err.status).toBe(502);
    expect(err.code).toBe("http_502");
    expect(err.message).toBe("<html>Bad Gateway</html>");
  });

  it("handles 404 with an empty body", async () => {
    fetchMock.mockResolvedValueOnce(new Response(null, { status: 404, statusText: "Not Found" }));
    const err = (await api.tickets.get(999).catch((e: unknown) => e)) as ApiError;
    expect(err.isNotFound).toBe(true);
    expect(err.code).toBe("http_404");
    expect(err.message).toBe("Not Found");
  });

  it("returns undefined for 204 responses", async () => {
    fetchMock.mockResolvedValueOnce(new Response(null, { status: 204 }));
    await expect(api.tickets.delete(5)).resolves.toBeUndefined();
    expect(fetchMock.mock.calls[0][1]?.method).toBe("DELETE");
  });

  it("wraps network failures as ApiError network_error", async () => {
    fetchMock.mockRejectedValueOnce(new TypeError("Failed to fetch"));
    const err = (await api.health().catch((e: unknown) => e)) as ApiError;
    expect(err).toBeInstanceOf(ApiError);
    expect(err.status).toBe(0);
    expect(err.code).toBe("network_error");
    expect(err.message).toBe("Failed to fetch");
  });

  it("rethrows AbortError untouched", async () => {
    const abort = new DOMException("The operation was aborted.", "AbortError");
    fetchMock.mockRejectedValueOnce(abort);
    const err = await api.tickets.list({}, new AbortController().signal).catch((e: unknown) => e);
    expect(err).toBe(abort);
  });

  it("rejects invalid JSON on success responses", async () => {
    fetchMock.mockResolvedValueOnce(new Response("not json", { status: 200 }));
    const err = (await api.projects.list().catch((e: unknown) => e)) as ApiError;
    expect(err.code).toBe("invalid_json");
  });

  it("sends the done snapshot flag only when given", async () => {
    fetchMock.mockResolvedValueOnce(jsonResponse(200, { id: 1 }));
    fetchMock.mockResolvedValueOnce(jsonResponse(200, { id: 1 }));
    await api.tickets.done(1);
    expect(fetchMock.mock.calls[0][1]?.body).toBe("{}");
    await api.tickets.done(1, true);
    expect(fetchMock.mock.calls[1][1]?.body).toBe(JSON.stringify({ snapshot: true }));
  });

  it("builds the run stream url and event query", async () => {
    expect(api.runs.streamUrl(12)).toBe("/api/runs/12/stream");
    fetchMock.mockResolvedValueOnce(jsonResponse(200, []));
    await api.runs.events(12, { since: 40, limit: 100 });
    expect(fetchMock.mock.calls[0][0]).toBe("/api/runs/12/events?since=40&limit=100");
  });
});
