import { useEffect, useMemo, useRef, useState } from "react";
import { ArrowDownToLine, Loader2, RadioTower, WifiOff } from "lucide-react";
import { api } from "@/lib/api";
import { summarizeRunEvent, type EventCard } from "@/lib/events";
import { connectSse } from "@/lib/sse";
import type { Run, RunEvent } from "@/lib/types";
import { cn } from "@/lib/utils";
import { useTicketStore } from "@/store/ticket";
import { Badge } from "@/components/ui/Badge";
import { runStatusTone } from "@/components/ui/tones";
import { EventCardView } from "./EventCard";

type StreamState = "connecting" | "live" | "reconnecting" | "closed";

interface Props {
  run: Run;
}

export function EventStream({ run }: Props) {
  const [cards, setCards] = useState<EventCard[]>([]);
  const [state, setState] = useState<StreamState>("connecting");
  const [follow, setFollow] = useState(true);
  const seen = useRef<Set<number>>(new Set());
  const scrollRef = useRef<HTMLDivElement>(null);
  const upsertRun = useTicketStore((s) => s.upsertRun);
  const runId = run.id;

  useEffect(() => {
    seen.current = new Set();
    setCards([]);
    setState("connecting");
    let pending: EventCard[] = [];
    let flush: ReturnType<typeof setTimeout> | null = null;
    const scheduleFlush = () => {
      if (flush) return;
      flush = setTimeout(() => {
        flush = null;
        const batch = pending;
        pending = [];
        if (batch.length) setCards((prev) => [...prev, ...batch]);
      }, 40);
    };
    const handle = connectSse<{ run_event: RunEvent; run_done: Run }>({
      url: api.runs.streamUrl(runId),
      events: {
        run_event: (ev) => {
          if (seen.current.has(ev.seq)) return; // replay after reconnect
          seen.current.add(ev.seq);
          pending.push(...summarizeRunEvent(ev));
          scheduleFlush();
        },
        run_done: (r) => {
          upsertRun(r);
          setState("closed");
          handle.close();
        },
      },
      onOpen: () => setState("live"),
      onError: (info) => {
        if (info.kind === "parse") return;
        setState(info.nextDelayMs === null ? "closed" : "reconnecting");
      },
      minDelayMs: 1000,
      maxDelayMs: 15_000,
    });
    return () => {
      handle.close();
      if (flush) clearTimeout(flush);
    };
  }, [runId, upsertRun]);

  // A finished run: the server closes the stream after replay + run_done. If the
  // run was already finished before we subscribed we still get run_done, so the
  // state machine above covers both.

  useEffect(() => {
    if (!follow) return;
    const el = scrollRef.current;
    if (el) el.scrollTop = el.scrollHeight;
  }, [cards, follow]);

  const onScroll = () => {
    const el = scrollRef.current;
    if (!el) return;
    const atBottom = el.scrollHeight - el.scrollTop - el.clientHeight < 24;
    if (!atBottom && follow) setFollow(false);
    if (atBottom && !follow) setFollow(true);
  };

  const toolNames = useMemo(() => {
    const m: Record<string, string> = {};
    for (const c of cards) if (c.kind === "tool_use") m[c.id] = c.name;
    return m;
  }, [cards]);

  const finished = run.status === "done" || run.status === "failed" || run.status === "cancelled";

  return (
    <div className="flex h-full min-h-0 flex-col">
      <div className="flex h-8 shrink-0 items-center gap-2 border-b border-line px-3 text-[11.5px] text-fg-muted">
        <span className="font-mono text-fg">run #{run.id}</span>
        <Badge>{run.kind}</Badge>
        <Badge tone={runStatusTone[run.status]}>{run.status}</Badge>
        <span className="ml-1 flex items-center gap-1">
          {state === "live" && !finished && <><RadioTower size={12} className="text-ok" /> live</>}
          {state === "connecting" && <><Loader2 size={12} className="animate-spin" /> connecting</>}
          {state === "reconnecting" && <><WifiOff size={12} className="text-warn" /> reconnecting</>}
          {(state === "closed" || (state === "live" && finished)) && <span className="text-fg-faint">{cards.length} cards</span>}
        </span>
        <button
          type="button"
          onClick={() => {
            setFollow((v) => !v);
            const el = scrollRef.current;
            if (el && !follow) el.scrollTop = el.scrollHeight;
          }}
          className={cn("ml-auto inline-flex items-center gap-1 rounded border px-1.5 h-5", follow ? "border-accent/50 bg-accent-soft text-accent" : "border-line text-fg-muted hover:text-fg")}
          title="Auto-scroll to newest"
        >
          <ArrowDownToLine size={11} /> follow
        </button>
      </div>
      <div ref={scrollRef} onScroll={onScroll} className="flex-1 space-y-1.5 overflow-y-auto p-3">
        {cards.length === 0 && (
          <div className="py-8 text-center text-[12px] text-fg-faint">
            {state === "connecting" ? "Waiting for events…" : run.eventCount === 0 ? "No events recorded." : "No events yet."}
          </div>
        )}
        {cards.map((c) => (
          <EventCardView key={c.key} card={c} toolNames={toolNames} />
        ))}
        {run.error && (
          <div className="rounded-md border border-danger/40 bg-danger-soft px-2.5 py-1.5 text-[12px] text-danger">
            <span className="font-medium">run error:</span> {run.error}
            {run.exitCode !== null && <span className="ml-2 font-mono text-[11px] opacity-80">exit {run.exitCode}</span>}
          </div>
        )}
      </div>
    </div>
  );
}
