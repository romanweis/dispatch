import { create } from "zustand";
import { api } from "@/lib/api";
import type { Comment, Run, Ticket, TicketDetail } from "@/lib/types";

/**
 * Cache of full ticket details (questions, comments, runs, attachCommand),
 * kept fresh by the board SSE feed: `ticket` events merge base fields,
 * `run` events upsert the run list. Anything the feed does not carry
 * (questions, comments) is refetched when the ticket's updatedAt changes.
 */
interface TicketState {
  details: Record<number, TicketDetail | undefined>;
  loading: Record<number, boolean | undefined>;
  errors: Record<number, string | undefined>;
  load: (id: number) => Promise<TicketDetail | null>;
  set: (detail: TicketDetail) => void;
  mergeTicket: (ticket: Ticket) => void;
  upsertRun: (run: Run) => void;
  addComment: (comment: Comment) => void;
  remove: (id: number) => void;
}

export const useTicketStore = create<TicketState>((set, get) => ({
  details: {},
  loading: {},
  errors: {},

  load: async (id) => {
    set((s) => ({ loading: { ...s.loading, [id]: true }, errors: { ...s.errors, [id]: undefined } }));
    try {
      const detail = await api.tickets.get(id);
      set((s) => ({ details: { ...s.details, [id]: detail }, loading: { ...s.loading, [id]: false } }));
      return detail;
    } catch (err) {
      set((s) => ({
        loading: { ...s.loading, [id]: false },
        errors: { ...s.errors, [id]: err instanceof Error ? err.message : "Failed to load ticket" },
      }));
      return null;
    }
  },

  set: (detail) => set((s) => ({ details: { ...s.details, [detail.id]: detail } })),

  mergeTicket: (ticket) => {
    const cur = get().details[ticket.id];
    if (!cur) return;
    const changed = cur.updatedAt !== ticket.updatedAt || cur.status !== ticket.status || cur.activeRunId !== ticket.activeRunId;
    set((s) => ({ details: { ...s.details, [ticket.id]: { ...cur, ...ticket } } }));
    // Questions/comments are not on the feed; refresh them when the ticket changed.
    if (changed) void get().load(ticket.id);
  },

  upsertRun: (run) => {
    const cur = get().details[run.ticketId];
    if (!cur) return;
    const idx = cur.runs.findIndex((r) => r.id === run.id);
    const runs = idx >= 0 ? cur.runs.map((r) => (r.id === run.id ? run : r)) : [run, ...cur.runs];
    set((s) => ({ details: { ...s.details, [run.ticketId]: { ...cur, runs } } }));
  },

  addComment: (comment) => {
    const cur = get().details[comment.ticketId];
    if (!cur || cur.comments.some((c) => c.id === comment.id)) return;
    set((s) => ({ details: { ...s.details, [comment.ticketId]: { ...cur, comments: [...cur.comments, comment] } } }));
  },

  remove: (id) =>
    set((s) => {
      const details = { ...s.details };
      delete details[id];
      return { details };
    }),
}));
