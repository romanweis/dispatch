import { useEffect, useState } from "react";
import { Box, Check, Loader2, Pencil, X } from "lucide-react";
import { api } from "@/lib/api";
import { STATUS_LABEL, type TicketDetail } from "@/lib/types";
import { relativeTime } from "@/lib/utils";
import { useBoardStore } from "@/store/board";
import { toastError } from "@/store/toasts";
import { Badge } from "@/components/ui/Badge";
import { containerTone, statusTone } from "@/components/ui/tones";
import { Input } from "@/components/ui/Field";

export function TicketHeader({ ticket }: { ticket: TicketDetail }) {
  const applyTicket = useBoardStore((s) => s.applyTicket);
  const [editing, setEditing] = useState(false);
  const [draft, setDraft] = useState(ticket.title);
  const [saving, setSaving] = useState(false);

  useEffect(() => {
    if (!editing) setDraft(ticket.title);
  }, [ticket.title, editing]);

  const save = async () => {
    const title = draft.trim();
    if (!title || title === ticket.title) {
      setEditing(false);
      return;
    }
    setSaving(true);
    try {
      const updated = await api.tickets.patch(ticket.id, { title });
      applyTicket(updated);
      setEditing(false);
    } catch (err) {
      toastError(err, "Rename");
    } finally {
      setSaving(false);
    }
  };

  return (
    <div className="flex flex-col gap-1.5">
      <div className="flex flex-wrap items-center gap-1.5 text-[11.5px] text-fg-muted">
        <span className="font-mono text-fg-faint">#{ticket.id}</span>
        <Badge>{ticket.projectName}</Badge>
        <Badge tone={statusTone[ticket.status]}>{STATUS_LABEL[ticket.status]}</Badge>
        <Badge tone={containerTone[ticket.containerState]} title={ticket.container ?? "no container"}>
          <Box size={10} /> {ticket.container ?? "container"} · {ticket.containerState}
        </Badge>
        {ticket.activeRunId !== null && (
          <Badge tone="info">
            <Loader2 size={10} className="animate-spin" /> run #{ticket.activeRunId}
          </Badge>
        )}
        {ticket.slug && <span className="font-mono text-fg-faint">{ticket.slug}</span>}
        <span className="ml-auto text-fg-faint" title={ticket.updatedAt}>
          updated {relativeTime(ticket.updatedAt)}
        </span>
      </div>
      {editing ? (
        <div className="flex items-center gap-1.5">
          <Input
            autoFocus
            value={draft}
            disabled={saving}
            onChange={(e) => setDraft(e.target.value)}
            onKeyDown={(e) => {
              if (e.key === "Enter") void save();
              if (e.key === "Escape") {
                e.stopPropagation();
                setEditing(false);
              }
            }}
            className="h-8 text-[15px] font-semibold"
          />
          <button type="button" onClick={() => void save()} className="rounded p-1 text-ok hover:bg-surface-2" aria-label="Save title">
            {saving ? <Loader2 size={15} className="animate-spin" /> : <Check size={15} />}
          </button>
          <button type="button" onClick={() => setEditing(false)} className="rounded p-1 text-fg-muted hover:bg-surface-2" aria-label="Cancel">
            <X size={15} />
          </button>
        </div>
      ) : (
        <h1 className="group flex items-start gap-2 text-[15px] font-semibold leading-snug text-fg">
          <span className="min-w-0 break-words">{ticket.title}</span>
          <button
            type="button"
            onClick={() => setEditing(true)}
            className="mt-0.5 shrink-0 rounded p-0.5 text-fg-faint opacity-0 transition-opacity hover:text-fg group-hover:opacity-100 focus:opacity-100"
            aria-label="Edit title"
          >
            <Pencil size={13} />
          </button>
        </h1>
      )}
    </div>
  );
}
