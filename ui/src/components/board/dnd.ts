import type { Ticket, TicketStatus } from "@/lib/types";

export const cardDragId = (id: number) => `card:${id}`;
export const columnDragId = (status: TicketStatus) => `col:${status}`;

/** Resolve a dnd-kit droppable/draggable id to the column status it belongs to. */
export function statusFromDragId(id: string | number, tickets: Record<number, Ticket>): TicketStatus | null {
  const s = String(id);
  if (s.startsWith("col:")) return s.slice(4) as TicketStatus;
  if (s.startsWith("card:")) return tickets[Number(s.slice(5))]?.status ?? null;
  return null;
}

export function ticketIdFromDragId(id: string | number): number {
  return Number(String(id).slice(5));
}

/** Status-specific one-line hint shown on a board card. */
export function ticketHint(t: Ticket): { icon?: "help" | "pr"; text: string } | null {
  if (t.status === "needs_input" && t.openQuestions > 0) {
    return { icon: "help", text: `${t.openQuestions} open question${t.openQuestions === 1 ? "" : "s"}` };
  }
  if (t.status === "in_progress" || t.status === "review") {
    const parts: string[] = [];
    const ws = t.workflowState;
    if (ws?.phase) parts.push(String(ws.phase));
    if (ws?.gate) parts.push(`gate ${ws.gate}`);
    const prs = t.result?.prs?.length ?? 0;
    if (prs > 0) parts.push(`${prs} PR${prs === 1 ? "" : "s"}`);
    if (parts.length) return { icon: prs > 0 ? "pr" : undefined, text: parts.join(" · ") };
  }
  return null;
}
