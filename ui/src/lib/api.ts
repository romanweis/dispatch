import type {
  ApiErrorBody,
  Comment,
  Project,
  Run,
  RunEvent,
  Ticket,
  TicketDetail,
  TicketStatus,
  TicketType,
} from "./types";

export class ApiError extends Error {
  readonly status: number;
  readonly code: string;

  constructor(status: number, code: string, message: string) {
    super(message);
    this.name = "ApiError";
    this.status = status;
    this.code = code;
  }

  get isInvalidTransition() {
    return this.code === "invalid_transition";
  }
  get isRunActive() {
    return this.code === "run_active";
  }
  get isNotFound() {
    return this.status === 404;
  }
}

type Json = Record<string, unknown> | unknown[] | null;

async function parseError(res: Response): Promise<ApiError> {
  let body: Partial<ApiErrorBody> | null = null;
  const text = await res.text().catch(() => "");
  if (text) {
    try {
      body = JSON.parse(text) as Partial<ApiErrorBody>;
    } catch {
      body = null;
    }
  }
  const code = typeof body?.code === "string" && body.code ? body.code : `http_${res.status}`;
  const message =
    typeof body?.error === "string" && body.error
      ? body.error
      : text || res.statusText || `Request failed with status ${res.status}`;
  return new ApiError(res.status, code, message);
}

async function request<T>(method: string, path: string, body?: Json, signal?: AbortSignal): Promise<T> {
  const headers: Record<string, string> = { Accept: "application/json" };
  const init: RequestInit = { method, headers, signal };
  if (body !== undefined) {
    headers["Content-Type"] = "application/json";
    init.body = JSON.stringify(body);
  }
  let res: Response;
  try {
    res = await fetch(path, init);
  } catch (err) {
    if (err instanceof Error && err.name === "AbortError") throw err;
    throw new ApiError(0, "network_error", err instanceof Error ? err.message : "Network error");
  }
  if (!res.ok) throw await parseError(res);
  if (res.status === 204) return undefined as T;
  const text = await res.text();
  if (!text) return undefined as T;
  try {
    return JSON.parse(text) as T;
  } catch {
    throw new ApiError(res.status, "invalid_json", "Server returned invalid JSON");
  }
}

function qs(params: Record<string, string | number | undefined | null>): string {
  const sp = new URLSearchParams();
  for (const [k, v] of Object.entries(params)) {
    if (v !== undefined && v !== null && v !== "") sp.set(k, String(v));
  }
  const s = sp.toString();
  return s ? `?${s}` : "";
}

export const api = {
  health: (signal?: AbortSignal) => request<{ status: string }>("GET", "/healthz", undefined, signal),

  projects: {
    list: (signal?: AbortSignal) => request<Project[]>("GET", "/api/projects", undefined, signal),
  },

  tickets: {
    list: (filter: { projectId?: number; status?: TicketStatus } = {}, signal?: AbortSignal) =>
      request<Ticket[]>("GET", `/api/tickets${qs(filter)}`, undefined, signal),
    create: (input: { projectId: number; title: string; body: string; autoMerge?: boolean; type?: TicketType; start?: boolean }) =>
      request<Ticket>("POST", "/api/tickets", input),
    get: (id: number, signal?: AbortSignal) =>
      request<TicketDetail>("GET", `/api/tickets/${id}`, undefined, signal),
    patch: (id: number, patch: { title?: string; body?: string; spec?: string; autoMerge?: boolean }) =>
      request<Ticket>("PATCH", `/api/tickets/${id}`, patch),
    delete: (id: number) => request<void>("DELETE", `/api/tickets/${id}`),
    refine: (id: number) => request<Run>("POST", `/api/tickets/${id}/refine`),
    answer: (id: number, answers: { questionId: number; answer: string }[]) =>
      request<Run>("POST", `/api/tickets/${id}/answer`, { answers }),
    start: (id: number) => request<Run>("POST", `/api/tickets/${id}/start`),
    ship: (id: number) => request<Run>("POST", `/api/tickets/${id}/ship`),
    resume: (id: number, message: string) => request<Run>("POST", `/api/tickets/${id}/resume`, { message }),
    cancel: (id: number) => request<Run>("POST", `/api/tickets/${id}/cancel`),
    done: (id: number, snapshot?: boolean) =>
      request<Ticket>("POST", `/api/tickets/${id}/done`, snapshot === undefined ? {} : { snapshot }),
    move: (id: number, status: TicketStatus) =>
      request<Ticket>("POST", `/api/tickets/${id}/move`, { status }),
    addComment: (id: number, text: string) =>
      request<Comment>("POST", `/api/tickets/${id}/comments`, { text }),
    refreshContainer: (id: number) => request<Ticket>("POST", `/api/tickets/${id}/container/refresh`),
    runs: (id: number, signal?: AbortSignal) =>
      request<Run[]>("GET", `/api/tickets/${id}/runs`, undefined, signal),
  },

  runs: {
    get: (id: number, signal?: AbortSignal) => request<Run>("GET", `/api/runs/${id}`, undefined, signal),
    events: (id: number, opts: { since?: number; limit?: number } = {}, signal?: AbortSignal) =>
      request<RunEvent[]>("GET", `/api/runs/${id}/events${qs(opts)}`, undefined, signal),
    streamUrl: (id: number) => `/api/runs/${id}/stream`,
  },

  eventsUrl: "/api/events",
};

export type Api = typeof api;
