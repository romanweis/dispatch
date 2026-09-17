import { memo, type CSSProperties } from "react";
import { useSortable } from "@dnd-kit/sortable";
import { CSS } from "@dnd-kit/utilities";
import { CircleHelp, GitMerge, GitPullRequest, Loader2 } from "lucide-react";
import { Link } from "react-router";
import type { Ticket } from "@/lib/types";
import { cn } from "@/lib/utils";
import { Badge } from "@/components/ui/Badge";
import { cardDragId, ticketHint } from "./dnd";

interface Props {
  ticket: Ticket;
  active?: boolean;
  overlay?: boolean;
}

export const TicketCard = memo(function TicketCard({ ticket, active, overlay }: Props) {
  const { attributes, listeners, setNodeRef, transform, transition, isDragging } = useSortable({
    id: cardDragId(ticket.id),
    data: { ticketId: ticket.id, status: ticket.status },
    disabled: overlay,
  });
  const style: CSSProperties = { transform: CSS.Translate.toString(transform), transition };
  const hint = ticketHint(ticket);
  const running = ticket.activeRunId !== null;

  return (
    <div
      ref={setNodeRef}
      style={style}
      {...attributes}
      {...listeners}
      className={cn(
        "group rounded-md border bg-surface p-2.5 text-left shadow-sm outline-none transition-colors",
        active ? "border-accent/70 ring-1 ring-accent/40" : "border-line hover:border-line-strong",
        isDragging && !overlay && "opacity-30",
        overlay && "shadow-xl shadow-black/40 border-accent/60 cursor-grabbing",
      )}
    >
      <div className="flex items-center justify-between gap-2">
        <span className="font-mono text-[11px] text-fg-faint">#{ticket.id}</span>
        <div className="flex items-center gap-1.5">
          {ticket.type === "task" && (
            <Badge tone="info" title="Task: no refinement round, the agent plans and implements in one go">
              task
            </Badge>
          )}
          {ticket.autoMerge && <GitMerge size={12} className="text-ok" aria-label="Auto-merge on" />}
          {running && <Loader2 size={12} className="animate-spin text-info" aria-label="Run active" />}
          <Badge tone="neutral" className="max-w-[120px] truncate" title={ticket.projectName}>
            {ticket.projectName}
          </Badge>
        </div>
      </div>
      <Link
        to={`/tickets/${ticket.id}`}
        draggable={false}
        onPointerDown={(e) => e.stopPropagation()}
        className="mt-1 block text-[13px] font-medium leading-snug text-fg hover:text-accent line-clamp-3"
      >
        {ticket.title}
      </Link>
      {(hint || ticket.lastProgress) && (
        <div className="mt-2 flex flex-col gap-1 text-[11.5px] text-fg-muted">
          {hint && (
            <div className="flex items-center gap-1.5">
              {hint.icon === "help" && <CircleHelp size={12} className="text-warn" />}
              {hint.icon === "pr" && <GitPullRequest size={12} className="text-accent" />}
              <span className="truncate">{hint.text}</span>
            </div>
          )}
          {ticket.lastProgress && (
            <div className="flex items-center gap-1.5 truncate" title={ticket.lastProgress.note ?? undefined}>
              <span className="inline-block size-1.5 rounded-full bg-info" />
              <span className="truncate">
                <span className="text-fg">{ticket.lastProgress.phase}</span>
                {ticket.lastProgress.note && <span className="text-fg-faint"> — {ticket.lastProgress.note}</span>}
              </span>
            </div>
          )}
        </div>
      )}
    </div>
  );
});
