import { useEffect, useState } from "react";
import { Check, CircleHelp, Pencil, Play, X } from "lucide-react";
import { api } from "@/lib/api";
import type { Question, TicketDetail } from "@/lib/types";
import { formatDateTime } from "@/lib/utils";
import { useBoardStore } from "@/store/board";
import { toastError } from "@/store/toasts";
import { Button } from "@/components/ui/Button";
import { SectionTitle, Textarea } from "@/components/ui/Field";
import { Markdown } from "@/components/ui/Markdown";

interface Props {
  ticket: TicketDetail;
  answers: Record<number, string>;
  setAnswers: (fn: (a: Record<number, string>) => Record<number, string>) => void;
  onStart: () => void;
  canStart: boolean;
}

const SPEC_EDITABLE = new Set(["backlog", "needs_input", "ready"]);

export function OverviewTab({ ticket, answers, setAnswers, onStart, canStart }: Props) {
  const open = ticket.questions.filter((q) => q.answer === null);
  const answered = ticket.questions.filter((q) => q.answer !== null);

  return (
    <div className="flex flex-col gap-6">
      <MarkdownField
        title="Body"
        value={ticket.body}
        editable
        emptyText="No body."
        onSave={(body) => api.tickets.patch(ticket.id, { body })}
      />

      {ticket.questions.length > 0 && (
        <section>
          <SectionTitle>
            Questions{" "}
            {open.length > 0 && <span className="ml-1 rounded bg-warn-soft px-1.5 text-warn normal-case tracking-normal">{open.length} open</span>}
          </SectionTitle>
          <div className="flex flex-col gap-2">
            {open.map((q) => (
              <QuestionRow key={q.id} q={q}>
                <Textarea
                  rows={2}
                  value={answers[q.id] ?? ""}
                  onChange={(e) => setAnswers((a) => ({ ...a, [q.id]: e.target.value }))}
                  placeholder="Your answer…"
                  className="mt-1.5"
                />
              </QuestionRow>
            ))}
            {answered.map((q) => (
              <QuestionRow key={q.id} q={q} muted>
                <div className="mt-1.5 whitespace-pre-wrap rounded border border-line/70 bg-surface-2/60 px-2 py-1.5 text-[12.5px] text-fg-muted">{q.answer}</div>
                <div className="mt-1 text-[11px] text-fg-faint">answered {formatDateTime(q.answeredAt)}</div>
              </QuestionRow>
            ))}
          </div>
        </section>
      )}

      <MarkdownField
        title="Spec"
        value={ticket.spec ?? ""}
        editable={SPEC_EDITABLE.has(ticket.status)}
        emptyText={ticket.status === "backlog" ? "No spec yet. Run Refine to have the agent write one." : "No spec."}
        onSave={(spec) => api.tickets.patch(ticket.id, { spec })}
        extra={
          canStart && ticket.spec ? (
            <Button variant="primary" size="sm" icon={<Play size={12} />} onClick={onStart}>
              Approve and start
            </Button>
          ) : null
        }
      />
    </div>
  );
}

function QuestionRow({ q, muted, children }: { q: Question; muted?: boolean; children: React.ReactNode }) {
  return (
    <div className={`rounded-md border px-3 py-2 ${muted ? "border-line/70" : "border-warn/30 bg-warn-soft/30"}`}>
      <div className="flex items-start gap-2 text-[12.5px]">
        <CircleHelp size={14} className={`mt-0.5 shrink-0 ${muted ? "text-fg-faint" : "text-warn"}`} />
        <div className="min-w-0 flex-1">
          <div className="whitespace-pre-wrap text-fg">{q.text}</div>
          <div className="text-[11px] text-fg-faint">asked {formatDateTime(q.askedAt)}</div>
          {children}
        </div>
      </div>
    </div>
  );
}

function MarkdownField({
  title,
  value,
  editable,
  emptyText,
  onSave,
  extra,
}: {
  title: string;
  value: string;
  editable: boolean;
  emptyText: string;
  onSave: (v: string) => Promise<import("@/lib/types").Ticket>;
  extra?: React.ReactNode;
}) {
  const applyTicket = useBoardStore((s) => s.applyTicket);
  const [editing, setEditing] = useState(false);
  const [draft, setDraft] = useState(value);
  const [saving, setSaving] = useState(false);

  useEffect(() => {
    if (!editing) setDraft(value);
  }, [value, editing]);

  const save = async () => {
    if (draft === value) {
      setEditing(false);
      return;
    }
    setSaving(true);
    try {
      const t = await onSave(draft);
      applyTicket(t);
      setEditing(false);
    } catch (err) {
      toastError(err, `Save ${title.toLowerCase()}`);
    } finally {
      setSaving(false);
    }
  };

  return (
    <section>
      <SectionTitle
        right={
          <div className="flex items-center gap-1.5">
            {!editing && extra}
            {editable && !editing && (
              <Button variant="ghost" size="sm" icon={<Pencil size={12} />} onClick={() => setEditing(true)}>
                Edit
              </Button>
            )}
            {editing && (
              <>
                <Button variant="ghost" size="sm" icon={<X size={12} />} onClick={() => setEditing(false)} disabled={saving}>
                  Cancel
                </Button>
                <Button variant="primary" size="sm" icon={<Check size={12} />} onClick={() => void save()} loading={saving}>
                  Save
                </Button>
              </>
            )}
          </div>
        }
      >
        {title}
      </SectionTitle>
      {editing ? (
        <Textarea
          autoFocus
          rows={Math.min(30, Math.max(8, draft.split("\n").length + 2))}
          value={draft}
          onChange={(e) => setDraft(e.target.value)}
          onKeyDown={(e) => {
            if (e.key === "Escape") {
              e.stopPropagation();
              setEditing(false);
            }
            if ((e.metaKey || e.ctrlKey) && e.key === "Enter") void save();
          }}
          className="font-mono text-[12px]"
        />
      ) : value.trim() ? (
        <Markdown>{value}</Markdown>
      ) : (
        <div className="rounded-md border border-dashed border-line px-3 py-3 text-[12px] text-fg-faint">{emptyText}</div>
      )}
    </section>
  );
}
