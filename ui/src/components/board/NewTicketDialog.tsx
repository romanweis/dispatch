import { useEffect, useState, type FormEvent } from "react";
import { useNavigate } from "react-router";
import { api } from "@/lib/api";
import { useBoardStore } from "@/store/board";
import { toastError } from "@/store/toasts";
import { Button } from "@/components/ui/Button";
import { Dialog } from "@/components/ui/Dialog";
import { Input, Label, Select, Textarea } from "@/components/ui/Field";

export function NewTicketDialog({ open, onClose }: { open: boolean; onClose: () => void }) {
  const projects = useBoardStore((s) => s.projects);
  const applyTicket = useBoardStore((s) => s.applyTicket);
  const navigate = useNavigate();
  const [projectId, setProjectId] = useState<number | "">("");
  const [title, setTitle] = useState("");
  const [body, setBody] = useState("");
  const [autoMerge, setAutoMerge] = useState(false);
  const [busy, setBusy] = useState(false);

  useEffect(() => {
    if (open) {
      setTitle("");
      setBody("");
      setAutoMerge(false);
      setBusy(false);
      setProjectId((cur) => (cur === "" && projects.length > 0 ? projects[0].id : cur));
    }
  }, [open, projects]);

  const canSubmit = projectId !== "" && title.trim().length > 0 && !busy;

  const submit = async (e: FormEvent) => {
    e.preventDefault();
    if (!canSubmit) return;
    setBusy(true);
    try {
      const ticket = await api.tickets.create({ projectId: Number(projectId), title: title.trim(), body, autoMerge });
      applyTicket(ticket);
      onClose();
      navigate(`/tickets/${ticket.id}`);
    } catch (err) {
      toastError(err, "Create ticket");
    } finally {
      setBusy(false);
    }
  };

  return (
    <Dialog
      open={open}
      onClose={onClose}
      title="New ticket"
      footer={
        <>
          <Button variant="ghost" onClick={onClose}>
            Cancel
          </Button>
          <Button variant="primary" form="new-ticket-form" type="submit" disabled={!canSubmit} loading={busy}>
            Create
          </Button>
        </>
      }
    >
      <form id="new-ticket-form" onSubmit={submit} className="flex flex-col gap-3">
        <div>
          <Label htmlFor="nt-project">Project</Label>
          <Select id="nt-project" value={projectId} onChange={(e) => setProjectId(e.target.value === "" ? "" : Number(e.target.value))} required>
            {projects.length === 0 && <option value="">No projects</option>}
            {projects.map((p) => (
              <option key={p.id} value={p.id}>
                {p.displayName || p.name} ({p.org})
              </option>
            ))}
          </Select>
        </div>
        <div>
          <Label htmlFor="nt-title">Title</Label>
          <Input id="nt-title" value={title} onChange={(e) => setTitle(e.target.value)} placeholder="Short, imperative summary" autoFocus required />
        </div>
        <div>
          <Label htmlFor="nt-body" hint="markdown">
            Body
          </Label>
          <Textarea
            id="nt-body"
            rows={8}
            value={body}
            onChange={(e) => setBody(e.target.value)}
            placeholder="Context, goals, constraints. The refine step will turn this into a spec."
            className="font-mono text-[12px]"
          />
        </div>
        <label className="flex items-center gap-2 text-[12.5px]">
          <input type="checkbox" checked={autoMerge} onChange={(e) => setAutoMerge(e.target.checked)} className="accent-accent" />
          <span>
            Auto-merge <span className="text-fg-muted">(ship to production as soon as the review gate passes, without waiting in Review)</span>
          </span>
        </label>
      </form>
    </Dialog>
  );
}
