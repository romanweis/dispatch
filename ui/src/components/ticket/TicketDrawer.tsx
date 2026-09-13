import { useCallback, useEffect, useState } from "react";
import { useNavigate } from "react-router";
import { Loader2, TerminalSquare, X } from "lucide-react";
import { api } from "@/lib/api";
import { cn } from "@/lib/utils";
import { useTicketStore } from "@/store/ticket";
import { toastError, toastInfo } from "@/store/toasts";
import { Button } from "@/components/ui/Button";
import { CodeBox } from "@/components/ui/CodeBox";
import { TicketHeader } from "./TicketHeader";
import { allowedActions } from "@/lib/actions";
import { TicketActions } from "./TicketActions";
import { OverviewTab } from "./OverviewTab";
import { RunsTab } from "./RunsTab";
import { WorkflowTab } from "./WorkflowTab";
import { CommentsTab } from "./CommentsTab";

type Tab = "overview" | "runs" | "workflow" | "comments";
const TABS: { id: Tab; label: string }[] = [
  { id: "overview", label: "Overview" },
  { id: "runs", label: "Runs" },
  { id: "workflow", label: "Workflow" },
  { id: "comments", label: "Comments" },
];

interface Props {
  ticketId: number;
  /** Rendered as a side panel over the board (wide) or a full page (narrow). */
  mode: "panel" | "page";
}

export function TicketDrawer({ ticketId, mode }: Props) {
  const navigate = useNavigate();
  const detail = useTicketStore((s) => s.details[ticketId]);
  const loading = useTicketStore((s) => s.loading[ticketId]);
  const error = useTicketStore((s) => s.errors[ticketId]);
  const load = useTicketStore((s) => s.load);
  const upsertRun = useTicketStore((s) => s.upsertRun);
  const [tab, setTab] = useState<Tab>("overview");
  const [answers, setAnswers] = useState<Record<number, string>>({});

  const close = useCallback(() => navigate("/"), [navigate]);

  useEffect(() => {
    void load(ticketId);
    setTab("overview");
    setAnswers({});
  }, [ticketId, load]);

  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if (e.key !== "Escape") return;
      const el = e.target as HTMLElement | null;
      if (el && (el.tagName === "TEXTAREA" || el.tagName === "INPUT")) return;
      close();
    };
    window.addEventListener("keydown", onKey);
    return () => window.removeEventListener("keydown", onKey);
  }, [close]);

  const openQuestions = detail?.questions.filter((q) => q.answer === null) ?? [];
  const answerReady = openQuestions.length > 0 && openQuestions.every((q) => (answers[q.id] ?? "").trim().length > 0);

  const submitAnswers = async () => {
    if (!detail) return;
    const run = await api.tickets.answer(
      detail.id,
      openQuestions.map((q) => ({ questionId: q.id, answer: answers[q.id].trim() })),
    );
    upsertRun(run);
    setAnswers({});
    toastInfo(`Run #${run.id} queued`, "answer");
    setTab("runs");
  };

  const startWork = async () => {
    if (!detail) return;
    try {
      const run = await api.tickets.start(detail.id);
      upsertRun(run);
      toastInfo(`Run #${run.id} queued`, "work");
      setTab("runs");
    } catch (err) {
      toastError(err, "Start work");
    }
  };

  const containerCls = mode === "panel"
    ? "fixed inset-y-0 right-0 z-40 w-[min(860px,calc(100vw-280px))] border-l border-line bg-bg shadow-2xl shadow-black/50"
    : "fixed inset-0 z-40 bg-bg";

  return (
    <aside className={cn(containerCls, "flex flex-col")} role="dialog" aria-label={detail ? `Ticket #${detail.id}` : "Ticket"}>
      <div className="flex items-start gap-3 border-b border-line px-4 py-3">
        <div className="min-w-0 flex-1">
          {detail ? (
            <TicketHeader ticket={detail} />
          ) : loading ? (
            <div className="flex items-center gap-2 text-[12.5px] text-fg-muted">
              <Loader2 size={14} className="animate-spin" /> Loading ticket #{ticketId}…
            </div>
          ) : (
            <div className="text-[12.5px] text-danger">{error ?? "Ticket not found"}</div>
          )}
        </div>
        <button type="button" onClick={close} className="rounded p-1 text-fg-muted hover:bg-surface-2 hover:text-fg" aria-label="Close (Esc)">
          <X size={16} />
        </button>
      </div>

      {detail && (
        <>
          <div className="border-b border-line px-4 py-2">
            <TicketActions ticket={detail} onAnswer={submitAnswers} answerReady={answerReady} />
          </div>
          <nav className="flex items-center gap-0.5 border-b border-line px-2">
            {TABS.map((t) => {
              const badge =
                t.id === "runs" ? detail.runs.length : t.id === "comments" ? detail.comments.length : t.id === "overview" && openQuestions.length ? openQuestions.length : 0;
              return (
                <button
                  key={t.id}
                  type="button"
                  onClick={() => setTab(t.id)}
                  className={cn(
                    "relative -mb-px flex h-9 items-center gap-1.5 border-b-2 px-2.5 text-[12.5px] transition-colors",
                    tab === t.id ? "border-accent text-fg" : "border-transparent text-fg-muted hover:text-fg",
                  )}
                >
                  {t.label}
                  {badge > 0 && <span className={cn("rounded px-1 font-mono text-[10.5px]", t.id === "overview" ? "bg-warn-soft text-warn" : "bg-surface-3 text-fg-muted")}>{badge}</span>}
                </button>
              );
            })}
          </nav>
          <div className={cn("min-h-0 flex-1 overflow-y-auto px-4 py-4", tab === "runs" && "flex flex-col")}>
            {tab === "overview" && (
              <OverviewTab ticket={detail} answers={answers} setAnswers={(fn) => setAnswers(fn)} onStart={() => void startWork()} canStart={allowedActions(detail).start && !!detail.spec} />
            )}
            {tab === "runs" && (
              <div className="min-h-0 flex-1">
                <RunsTab ticket={detail} />
              </div>
            )}
            {tab === "workflow" && <WorkflowTab ticket={detail} />}
            {tab === "comments" && <CommentsTab ticket={detail} />}
          </div>
          {detail.attachCommand && tab !== "runs" && (
            <div className="border-t border-line px-4 py-3">
              <div className="mb-1.5 flex items-center gap-1.5 text-[11.5px] font-semibold uppercase tracking-wide text-fg-muted">
                <TerminalSquare size={12} /> Attach in terminal
              </div>
              <CodeBox code={detail.attachCommand} />
            </div>
          )}
        </>
      )}
      {!detail && !loading && (
        <div className="p-4">
          <Button onClick={() => void load(ticketId)}>Retry</Button>
        </div>
      )}
    </aside>
  );
}
