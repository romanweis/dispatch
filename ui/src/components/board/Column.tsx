import { useDroppable } from "@dnd-kit/core";
import { SortableContext, verticalListSortingStrategy } from "@dnd-kit/sortable";
import { STATUS_LABEL, type Ticket, type TicketStatus } from "@/lib/types";
import { cn } from "@/lib/utils";
import { Dot } from "@/components/ui/Badge";
import { statusTone } from "@/components/ui/tones";
import { TicketCard } from "./TicketCard";
import { cardDragId, columnDragId } from "./dnd";

interface Props {
  status: TicketStatus;
  tickets: Ticket[];
  activeTicketId: number | null;
}

export function Column({ status, tickets, activeTicketId }: Props) {
  const { setNodeRef, isOver } = useDroppable({ id: columnDragId(status), data: { status } });
  const ids = tickets.map((t) => cardDragId(t.id));

  return (
    <section
      className={cn(
        "flex h-full w-[264px] shrink-0 flex-col rounded-lg border bg-surface/40 transition-colors",
        isOver ? "border-accent/50 bg-accent-soft/40" : "border-line/70",
      )}
    >
      <header className="flex h-9 items-center justify-between px-3">
        <div className="flex items-center gap-2 text-[12px] font-semibold text-fg">
          <Dot tone={statusTone[status]} />
          {STATUS_LABEL[status]}
        </div>
        <span className="font-mono text-[11px] text-fg-faint">{tickets.length}</span>
      </header>
      <div ref={setNodeRef} className="flex min-h-[80px] flex-1 flex-col gap-2 overflow-y-auto px-2 pb-2">
        <SortableContext items={ids} strategy={verticalListSortingStrategy}>
          {tickets.map((t) => (
            <TicketCard key={t.id} ticket={t} active={t.id === activeTicketId} />
          ))}
        </SortableContext>
        {tickets.length === 0 && (
          <div className="flex flex-1 items-center justify-center rounded-md border border-dashed border-line text-[11.5px] text-fg-faint">
            Empty
          </div>
        )}
      </div>
    </section>
  );
}
