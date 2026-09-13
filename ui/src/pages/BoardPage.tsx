import { useEffect, useState } from "react";
import { Outlet, useParams } from "react-router";
import { Plus, RadioTower, RefreshCw, WifiOff } from "lucide-react";
import { useBoardStore, type ConnectionState } from "@/store/board";
import { cn } from "@/lib/utils";
import { Board } from "@/components/board/Board";
import { ProjectFilter } from "@/components/board/ProjectFilter";
import { NewTicketDialog } from "@/components/board/NewTicketDialog";
import { Button } from "@/components/ui/Button";

const connLabel: Record<ConnectionState, { text: string; cls: string; icon: typeof RadioTower }> = {
  connecting: { text: "connecting", cls: "text-fg-faint", icon: RefreshCw },
  live: { text: "live", cls: "text-ok", icon: RadioTower },
  polling: { text: "polling", cls: "text-warn", icon: RefreshCw },
  offline: { text: "offline", cls: "text-danger", icon: WifiOff },
};

export function BoardPage() {
  const connect = useBoardStore((s) => s.connect);
  const connection = useBoardStore((s) => s.connection);
  const loaded = useBoardStore((s) => s.loaded);
  const loadError = useBoardStore((s) => s.loadError);
  const loadAll = useBoardStore((s) => s.loadAll);
  const [newOpen, setNewOpen] = useState(false);
  const params = useParams<{ id?: string }>();
  const activeTicketId = params.id ? Number(params.id) : null;

  useEffect(() => connect(), [connect]);

  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      const el = e.target as HTMLElement | null;
      if (el && (el.tagName === "TEXTAREA" || el.tagName === "INPUT" || el.tagName === "SELECT")) return;
      if (e.key === "c" && !e.metaKey && !e.ctrlKey && !e.altKey && activeTicketId === null) {
        e.preventDefault();
        setNewOpen(true);
      }
    };
    window.addEventListener("keydown", onKey);
    return () => window.removeEventListener("keydown", onKey);
  }, [activeTicketId]);

  const conn = connLabel[connection];
  const ConnIcon = conn.icon;

  return (
    <div className="flex h-full flex-col">
      <header className="flex h-12 shrink-0 items-center gap-4 border-b border-line px-4">
        <div className="flex items-center gap-2">
          <span className="grid size-6 place-items-center rounded bg-accent text-[12px] font-bold text-accent-fg">D</span>
          <span className="text-[13.5px] font-semibold tracking-tight">Dispatch</span>
        </div>
        <div className="hidden h-4 w-px bg-line sm:block" />
        <div className="min-w-0 flex-1 overflow-x-auto">
          <ProjectFilter />
        </div>
        <div className={cn("hidden items-center gap-1 text-[11.5px] sm:flex", conn.cls)} title={loadError ?? `Board feed: ${conn.text}`}>
          <ConnIcon size={12} className={connection === "connecting" ? "animate-spin" : ""} />
          {conn.text}
        </div>
        <Button variant="primary" icon={<Plus size={14} />} onClick={() => setNewOpen(true)} title="New ticket (c)">
          New ticket
        </Button>
      </header>

      <main className="min-h-0 flex-1 pt-3">
        {!loaded ? (
          <div className="flex h-full items-center justify-center text-[12.5px] text-fg-faint">Loading board…</div>
        ) : loadError && Object.keys(useBoardStore.getState().tickets).length === 0 ? (
          <div className="flex h-full flex-col items-center justify-center gap-3 text-[12.5px] text-fg-muted">
            <div>
              Could not reach the API: <span className="text-danger">{loadError}</span>
            </div>
            <Button onClick={() => void loadAll()} icon={<RefreshCw size={13} />}>
              Retry
            </Button>
          </div>
        ) : (
          <Board activeTicketId={activeTicketId} />
        )}
      </main>

      <NewTicketDialog open={newOpen} onClose={() => setNewOpen(false)} />
      <Outlet />
    </div>
  );
}
