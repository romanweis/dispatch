/**
 * Small EventSource wrapper with exponential backoff and named-event routing.
 * Payloads are parsed as JSON; parse failures are reported via `onError` with `kind: "parse"`.
 */
export interface SseErrorInfo {
  kind: "transport" | "parse";
  /** consecutive transport failures so far */
  attempt: number;
  /** null when no reconnect is scheduled */
  nextDelayMs: number | null;
}

export interface SseOptions<E extends Record<string, unknown>> {
  url: string;
  events: { [K in keyof E]: (data: E[K]) => void };
  onOpen?: () => void;
  onError?: (info: SseErrorInfo) => void;
  /** Stop reconnecting after this many consecutive failures (default: unlimited). */
  maxAttempts?: number;
  minDelayMs?: number;
  maxDelayMs?: number;
}

export interface SseHandle {
  close: () => void;
  readonly connected: boolean;
}

export function connectSse<E extends Record<string, unknown>>(opts: SseOptions<E>): SseHandle {
  const minDelay = opts.minDelayMs ?? 1000;
  const maxDelay = opts.maxDelayMs ?? 30_000;
  let es: EventSource | null = null;
  let closed = false;
  let attempt = 0;
  let timer: ReturnType<typeof setTimeout> | null = null;
  let connected = false;

  const open = () => {
    if (closed) return;
    timer = null;
    const source = new EventSource(opts.url);
    es = source;
    for (const name of Object.keys(opts.events) as Array<keyof E & string>) {
      source.addEventListener(name, (ev) => {
        const msg = ev as MessageEvent<string>;
        let data: E[typeof name];
        try {
          data = JSON.parse(msg.data) as E[typeof name];
        } catch {
          opts.onError?.({ kind: "parse", attempt, nextDelayMs: null });
          return;
        }
        opts.events[name](data);
      });
    }
    source.onopen = () => {
      attempt = 0;
      connected = true;
      opts.onOpen?.();
    };
    source.onerror = () => {
      connected = false;
      source.close();
      if (es === source) es = null;
      if (closed) return;
      attempt += 1;
      if (opts.maxAttempts !== undefined && attempt >= opts.maxAttempts) {
        opts.onError?.({ kind: "transport", attempt, nextDelayMs: null });
        return;
      }
      const base = Math.min(maxDelay, minDelay * 2 ** (attempt - 1));
      const delay = Math.round(base * (0.8 + Math.random() * 0.4));
      opts.onError?.({ kind: "transport", attempt, nextDelayMs: delay });
      timer = setTimeout(open, delay);
    };
  };

  open();

  return {
    close: () => {
      closed = true;
      if (timer) clearTimeout(timer);
      timer = null;
      es?.close();
      es = null;
      connected = false;
    },
    get connected() {
      return connected;
    },
  };
}
