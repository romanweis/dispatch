import { create } from "zustand";
import { api } from "@/lib/api";
import { connectSse, type SseHandle } from "@/lib/sse";
import type { Project, Run, Ticket, TicketStatus } from "@/lib/types";
import { toastError } from "./toasts";
import { useTicketStore } from "./ticket";

export type ConnectionState = "connecting" | "live" | "polling" | "offline";

const POLL_MS = 10_000;

interface BoardState {
  tickets: Record<number, Ticket>;
  projects: Project[];
  projectFilter: number | null;
  loaded: boolean;
  loadError: string | null;
  connection: ConnectionState;
  /** ticket ids with an in-flight optimistic move */
  moving: Record<number, true | undefined>;

  loadAll: () => Promise<void>;
  setProjectFilter: (id: number | null) => void;
  applyTicket: (t: Ticket) => void;
  applyRun: (r: Run) => void;
  removeTicket: (id: number) => void;
  moveTicket: (id: number, status: TicketStatus) => Promise<boolean>;
  /** Start the live feed (idempotent). Returns a disposer. */
  connect: () => () => void;
}

let sse: SseHandle | null = null;
let poll: ReturnType<typeof setInterval> | null = null;
let connectRefs = 0;

function indexById(list: Ticket[]): Record<number, Ticket> {
  const out: Record<number, Ticket> = {};
  for (const t of list) out[t.id] = t;
  return out;
}

export const useBoardStore = create<BoardState>((set, get) => ({
  tickets: {},
  projects: [],
  projectFilter: null,
  loaded: false,
  loadError: null,
  connection: "connecting",
  moving: {},

  loadAll: async () => {
    try {
      const [projects, tickets] = await Promise.all([api.projects.list(), api.tickets.list()]);
      set({ projects, tickets: indexById(tickets), loaded: true, loadError: null });
    } catch (err) {
      set({ loaded: true, loadError: err instanceof Error ? err.message : "Failed to load board" });
      if (!get().loaded) toastError(err, "Load board");
    }
  },

  setProjectFilter: (id) => set({ projectFilter: id }),

  applyTicket: (t) => {
    set((s) => {
      // Ignore stale updates.
      const cur = s.tickets[t.id];
      if (cur && cur.updatedAt > t.updatedAt) return s;
      return { tickets: { ...s.tickets, [t.id]: t } };
    });
    useTicketStore.getState().mergeTicket(t);
  },

  applyRun: (r) => {
    useTicketStore.getState().upsertRun(r);
    // The board card spinner depends on activeRunId; the `ticket` event carries that,
    // but make sure a running run shows up even if the ticket event is delayed.
    const cur = get().tickets[r.ticketId];
    if (cur && r.status === "running" && cur.activeRunId !== r.id) {
      set((s) => ({ tickets: { ...s.tickets, [r.ticketId]: { ...cur, activeRunId: r.id } } }));
    }
  },

  removeTicket: (id) => {
    set((s) => {
      const tickets = { ...s.tickets };
      delete tickets[id];
      return { tickets };
    });
    useTicketStore.getState().remove(id);
  },

  moveTicket: async (id, status) => {
    const prev = get().tickets[id];
    if (!prev || prev.status === status) return true;
    set((s) => ({
      tickets: { ...s.tickets, [id]: { ...prev, status } },
      moving: { ...s.moving, [id]: true },
    }));
    try {
      const updated = await api.tickets.move(id, status);
      get().applyTicket(updated);
      return true;
    } catch (err) {
      toastError(err, `Move #${id} to ${status}`);
      set((s) => {
        const cur = s.tickets[id];
        // Revert only if nothing newer arrived over SSE in the meantime.
        if (!cur || cur.updatedAt !== prev.updatedAt) return s;
        return { tickets: { ...s.tickets, [id]: prev } };
      });
      return false;
    } finally {
      set((s) => {
        const moving = { ...s.moving };
        delete moving[id];
        return { moving };
      });
    }
  },

  connect: () => {
    connectRefs += 1;
    if (connectRefs === 1) startFeed(set, get);
    return () => {
      connectRefs -= 1;
      if (connectRefs === 0) stopFeed();
    };
  },
}));

type Set = (partial: Partial<BoardState> | ((s: BoardState) => Partial<BoardState>)) => void;
type Get = () => BoardState;

function startPolling(get: Get) {
  if (poll) return;
  poll = setInterval(() => void get().loadAll(), POLL_MS);
}

function stopPolling() {
  if (poll) clearInterval(poll);
  poll = null;
}

function startFeed(set: Set, get: Get) {
  void get().loadAll();
  sse = connectSse<{ ticket: Ticket; run: Run }>({
    url: api.eventsUrl,
    events: {
      ticket: (t) => get().applyTicket(t),
      run: (r) => get().applyRun(r),
    },
    onOpen: () => {
      stopPolling();
      set({ connection: "live" });
      // Catch up on anything missed while disconnected.
      void get().loadAll();
    },
    onError: (info) => {
      if (info.kind === "parse") return;
      set({ connection: info.nextDelayMs === null ? "offline" : "polling" });
      startPolling(get);
    },
    minDelayMs: 1000,
    maxDelayMs: 20_000,
  });
}

function stopFeed() {
  sse?.close();
  sse = null;
  stopPolling();
}

// ---- selectors ------------------------------------------------------------

export const selectVisibleTickets = (s: Pick<BoardState, "tickets" | "projectFilter">): Ticket[] => {
  const all = Object.values(s.tickets);
  const filtered = s.projectFilter === null ? all : all.filter((t) => t.projectId === s.projectFilter);
  return filtered.sort((a, b) => (a.updatedAt < b.updatedAt ? 1 : a.updatedAt > b.updatedAt ? -1 : b.id - a.id));
};
