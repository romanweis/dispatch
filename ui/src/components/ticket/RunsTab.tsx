import { useEffect, useMemo, useState } from "react";
import type { Run, TicketDetail } from "@/lib/types";
import { cn, formatDateTime, formatMs } from "@/lib/utils";
import { Badge } from "@/components/ui/Badge";
import { runStatusTone } from "@/components/ui/tones";
import { EventStream } from "@/components/events/EventStream";

function runDuration(r: Run): string {
  if (!r.startedAt) return "";
  const end = r.endedAt ? Date.parse(r.endedAt) : Date.now();
  return formatMs(end - Date.parse(r.startedAt));
}

export function RunsTab({ ticket }: { ticket: TicketDetail }) {
  const runs = useMemo(() => [...ticket.runs].sort((a, b) => b.id - a.id), [ticket.runs]);
  const [selectedId, setSelectedId] = useState<number | null>(null);

  // Default to the active run, else newest; follow new active runs as they appear.
  useEffect(() => {
    if (ticket.activeRunId !== null && runs.some((r) => r.id === ticket.activeRunId)) {
      setSelectedId((cur) => (cur !== null && runs.some((r) => r.id === cur && r.status === "running") ? cur : ticket.activeRunId));
    } else if (selectedId === null && runs.length > 0) {
      setSelectedId(runs[0].id);
    }
  }, [ticket.activeRunId, runs, selectedId]);

  const selected = runs.find((r) => r.id === selectedId) ?? null;

  if (runs.length === 0) {
    return <div className="py-10 text-center text-[12.5px] text-fg-faint">No runs yet.</div>;
  }

  return (
    <div className="flex h-full min-h-0 flex-col gap-3 md:flex-row">
      <ul className="flex shrink-0 flex-col gap-1 overflow-y-auto md:w-[220px]">
        {runs.map((r) => (
          <li key={r.id}>
            <button
              type="button"
              onClick={() => setSelectedId(r.id)}
              className={cn(
                "flex w-full flex-col gap-1 rounded-md border px-2.5 py-1.5 text-left transition-colors",
                r.id === selectedId ? "border-accent/60 bg-accent-soft/50" : "border-line hover:border-line-strong",
              )}
            >
              <div className="flex items-center gap-1.5">
                <span className="font-mono text-[11px] text-fg-faint">#{r.id}</span>
                <span className="text-[12.5px] font-medium text-fg">{r.kind}</span>
                <Badge tone={runStatusTone[r.status]} className="ml-auto">
                  {r.status}
                </Badge>
              </div>
              <div className="flex items-center gap-2 text-[11px] text-fg-faint">
                <span>{formatDateTime(r.startedAt ?? r.createdAt)}</span>
                <span className="ml-auto font-mono">{runDuration(r)}</span>
                <span className="font-mono">{r.eventCount} ev</span>
              </div>
            </button>
          </li>
        ))}
      </ul>
      <div className="min-h-[300px] flex-1 overflow-hidden rounded-md border border-line bg-surface-2/40 md:min-h-0">
        {selected ? <EventStream key={selected.id} run={selected} /> : null}
      </div>
    </div>
  );
}
