import { useState } from "react";
import { useNavigate } from "react-router";
import { CheckCheck, MessageSquareReply, Play, RefreshCw, Sparkles, Square, Trash2 } from "lucide-react";
import { api } from "@/lib/api";
import type { Run, Ticket, TicketDetail } from "@/lib/types";
import { useBoardStore } from "@/store/board";
import { useTicketStore } from "@/store/ticket";
import { toastError, toastInfo, toastSuccess } from "@/store/toasts";
import { Button } from "@/components/ui/Button";
import { Dialog } from "@/components/ui/Dialog";
import { Textarea } from "@/components/ui/Field";
import { allowedActions } from "@/lib/actions";

interface Props {
  ticket: TicketDetail;
  /** Called when the user clicks "Answer" so the Overview tab can submit the drafts. */
  onAnswer: () => Promise<void>;
  answerReady: boolean;
}

export function TicketActions({ ticket, onAnswer, answerReady }: Props) {
  const navigate = useNavigate();
  const applyTicket = useBoardStore((s) => s.applyTicket);
  const removeTicket = useBoardStore((s) => s.removeTicket);
  const upsertRun = useTicketStore((s) => s.upsertRun);
  const [busy, setBusy] = useState<string | null>(null);
  const [resumeOpen, setResumeOpen] = useState(false);
  const [resumeMsg, setResumeMsg] = useState("");
  const [doneOpen, setDoneOpen] = useState(false);
  const [snapshot, setSnapshot] = useState(false);
  const [deleteOpen, setDeleteOpen] = useState(false);
  const can = allowedActions(ticket);

  const runAction = async (name: string, fn: () => Promise<Run | Ticket | void>, after?: (r: Run | Ticket | void) => void) => {
    setBusy(name);
    try {
      const r = await fn();
      after?.(r);
    } catch (err) {
      toastError(err, name);
    } finally {
      setBusy(null);
    }
  };

  const onRun = (r: Run | Ticket | void) => {
    if (r && "kind" in r) {
      upsertRun(r);
      toastInfo(`Run #${r.id} queued`, r.kind);
    }
  };

  return (
    <>
      <div className="flex flex-wrap items-center gap-1.5">
        {can.refine && (
          <Button variant="primary" icon={<Sparkles size={13} />} loading={busy === "Refine"} onClick={() => runAction("Refine", () => api.tickets.refine(ticket.id), onRun)}>
            Refine
          </Button>
        )}
        {can.answer && (
          <Button
            variant="primary"
            icon={<MessageSquareReply size={13} />}
            disabled={!answerReady}
            loading={busy === "Answer"}
            title={answerReady ? "Submit all answers" : "Fill in every open question first"}
            onClick={() => runAction("Answer", onAnswer)}
          >
            Answer
          </Button>
        )}
        {can.start && (
          <Button
            variant="primary"
            icon={<Play size={13} />}
            disabled={can.startNeedsSpec}
            title={can.startNeedsSpec ? "A spec is required to start work" : undefined}
            loading={busy === "Start work"}
            onClick={() => runAction("Start work", () => api.tickets.start(ticket.id), onRun)}
          >
            Start work
          </Button>
        )}
        {can.resume && (
          <Button icon={<MessageSquareReply size={13} />} onClick={() => setResumeOpen(true)}>
            Resume
          </Button>
        )}
        {can.cancel && (
          <Button variant="danger" icon={<Square size={12} />} loading={busy === "Cancel"} onClick={() => runAction("Cancel", () => api.tickets.cancel(ticket.id), onRun)}>
            Cancel
          </Button>
        )}
        {can.done && (
          <Button icon={<CheckCheck size={13} />} onClick={() => setDoneOpen(true)}>
            Done
          </Button>
        )}
        <span className="mx-0.5 h-4 w-px bg-line" />
        {can.refresh && (
          <Button
            variant="ghost"
            icon={<RefreshCw size={13} />}
            loading={busy === "Refresh state"}
            title="Re-pull workflowState/result from the container"
            onClick={() => runAction("Refresh state", () => api.tickets.refreshContainer(ticket.id), (t) => t && applyTicket(t as Ticket))}
          >
            Refresh state
          </Button>
        )}
        <Button variant="ghost" icon={<Trash2 size={13} />} className="text-fg-muted hover:text-danger" onClick={() => setDeleteOpen(true)} disabled={!can.delete}>
          Delete
        </Button>
      </div>

      <Dialog
        open={resumeOpen}
        onClose={() => setResumeOpen(false)}
        title="Resume with message"
        footer={
          <>
            <Button variant="ghost" onClick={() => setResumeOpen(false)}>
              Cancel
            </Button>
            <Button
              variant="primary"
              disabled={!resumeMsg.trim()}
              loading={busy === "Resume"}
              onClick={() =>
                runAction(
                  "Resume",
                  () => api.tickets.resume(ticket.id, resumeMsg.trim()),
                  (r) => {
                    onRun(r);
                    setResumeMsg("");
                    setResumeOpen(false);
                  },
                )
              }
            >
              Send
            </Button>
          </>
        }
      >
        <p className="mb-2 text-[12px] text-fg-muted">
          Free-form message into the existing Claude session <span className="font-mono">{ticket.claudeSessionId}</span>.
        </p>
        <Textarea autoFocus rows={6} value={resumeMsg} onChange={(e) => setResumeMsg(e.target.value)} placeholder="e.g. CI is red on slice 2, please fix the lint errors and re-run the gate." />
      </Dialog>

      <Dialog
        open={doneOpen}
        onClose={() => setDoneOpen(false)}
        title="Mark as done"
        footer={
          <>
            <Button variant="ghost" onClick={() => setDoneOpen(false)}>
              Cancel
            </Button>
            <Button
              variant="primary"
              loading={busy === "Done"}
              onClick={() =>
                runAction(
                  "Done",
                  () => api.tickets.done(ticket.id, snapshot),
                  (t) => {
                    if (t) applyTicket(t as Ticket);
                    setDoneOpen(false);
                    toastSuccess(`#${ticket.id} done`);
                  },
                )
              }
            >
              Mark done
            </Button>
          </>
        }
      >
        <p className="text-[12.5px] text-fg-muted">This deletes the ticket container {ticket.container && <span className="font-mono text-fg">{ticket.container}</span>}.</p>
        <label className="mt-3 flex items-center gap-2 text-[12.5px]">
          <input type="checkbox" checked={snapshot} onChange={(e) => setSnapshot(e.target.checked)} className="accent-accent" />
          Keep a snapshot of the container before deleting
        </label>
      </Dialog>

      <Dialog
        open={deleteOpen}
        onClose={() => setDeleteOpen(false)}
        title={`Delete ticket #${ticket.id}`}
        footer={
          <>
            <Button variant="ghost" onClick={() => setDeleteOpen(false)}>
              Cancel
            </Button>
            <Button
              variant="danger"
              loading={busy === "Delete"}
              onClick={() =>
                runAction(
                  "Delete",
                  () => api.tickets.delete(ticket.id),
                  () => {
                    removeTicket(ticket.id);
                    setDeleteOpen(false);
                    navigate("/");
                  },
                )
              }
            >
              Delete permanently
            </Button>
          </>
        }
      >
        <p className="text-[12.5px] text-fg-muted">
          Deletes the ticket, its runs, comments and the container. This cannot be undone.
        </p>
      </Dialog>
    </>
  );
}
