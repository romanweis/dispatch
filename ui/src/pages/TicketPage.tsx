import { Navigate, useParams } from "react-router";
import { useMediaQuery } from "@/lib/useMediaQuery";
import { TicketDrawer } from "@/components/ticket/TicketDrawer";

/** Route element for /tickets/:id. Panel over the board on wide screens, full page on narrow. */
export function TicketPage() {
  const { id } = useParams<{ id: string }>();
  const wide = useMediaQuery("(min-width: 1024px)");
  const ticketId = Number(id);
  if (!id || !Number.isInteger(ticketId) || ticketId <= 0) return <Navigate to="/" replace />;
  return <TicketDrawer ticketId={ticketId} mode={wide ? "panel" : "page"} />;
}
