import { useMemo, useState } from "react";
import {
  DndContext,
  DragOverlay,
  PointerSensor,
  KeyboardSensor,
  closestCorners,
  useSensor,
  useSensors,
  type DragEndEvent,
  type DragStartEvent,
} from "@dnd-kit/core";
import { sortableKeyboardCoordinates } from "@dnd-kit/sortable";
import { TICKET_STATUSES, type Ticket, type TicketStatus } from "@/lib/types";
import { selectVisibleTickets, useBoardStore } from "@/store/board";
import { Column } from "./Column";
import { TicketCard } from "./TicketCard";
import { statusFromDragId, ticketIdFromDragId } from "./dnd";

export function Board({ activeTicketId }: { activeTicketId: number | null }) {
  const allTickets = useBoardStore((s) => s.tickets);
  const projectFilter = useBoardStore((s) => s.projectFilter);
  // Derive in the component: a selector returning a fresh array would re-render forever under useSyncExternalStore.
  const tickets = useMemo(
    () => selectVisibleTickets({ tickets: allTickets, projectFilter }),
    [allTickets, projectFilter],
  );
  const moveTicket = useBoardStore((s) => s.moveTicket);
  const [dragging, setDragging] = useState<Ticket | null>(null);

  const sensors = useSensors(
    useSensor(PointerSensor, { activationConstraint: { distance: 5 } }),
    useSensor(KeyboardSensor, { coordinateGetter: sortableKeyboardCoordinates }),
  );

  const byStatus = useMemo(() => {
    const map = Object.fromEntries(TICKET_STATUSES.map((s) => [s, [] as Ticket[]])) as Record<TicketStatus, Ticket[]>;
    for (const t of tickets) (map[t.status] ?? map.backlog).push(t);
    return map;
  }, [tickets]);

  const onDragStart = (e: DragStartEvent) => {
    setDragging(allTickets[ticketIdFromDragId(e.active.id)] ?? null);
  };

  const onDragEnd = (e: DragEndEvent) => {
    setDragging(null);
    const { active, over } = e;
    if (!over) return;
    const ticketId = ticketIdFromDragId(active.id);
    const target = statusFromDragId(over.id, allTickets);
    const current = allTickets[ticketId];
    if (!target || !current || current.status === target) return;
    void moveTicket(ticketId, target);
  };

  return (
    <DndContext sensors={sensors} collisionDetection={closestCorners} onDragStart={onDragStart} onDragEnd={onDragEnd} onDragCancel={() => setDragging(null)}>
      <div className="flex h-full gap-3 overflow-x-auto px-4 pb-4">
        {TICKET_STATUSES.map((status) => (
          <Column key={status} status={status} tickets={byStatus[status]} activeTicketId={activeTicketId} />
        ))}
      </div>
      <DragOverlay dropAnimation={null}>{dragging ? <TicketCard ticket={dragging} overlay /> : null}</DragOverlay>
    </DndContext>
  );
}
