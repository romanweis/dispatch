import { useState } from "react";
import { Bot, Send, Settings2, User } from "lucide-react";
import { api } from "@/lib/api";
import type { Comment, TicketDetail } from "@/lib/types";
import { cn, formatDateTime } from "@/lib/utils";
import { useTicketStore } from "@/store/ticket";
import { toastError } from "@/store/toasts";
import { Button } from "@/components/ui/Button";
import { Textarea } from "@/components/ui/Field";
import { Markdown } from "@/components/ui/Markdown";

const authorIcon: Record<Comment["author"], typeof User> = { user: User, agent: Bot, system: Settings2 };

export function CommentsTab({ ticket }: { ticket: TicketDetail }) {
  const addComment = useTicketStore((s) => s.addComment);
  const [text, setText] = useState("");
  const [busy, setBusy] = useState(false);

  const submit = async () => {
    const t = text.trim();
    if (!t) return;
    setBusy(true);
    try {
      const c = await api.tickets.addComment(ticket.id, t);
      addComment(c);
      setText("");
    } catch (err) {
      toastError(err, "Add comment");
    } finally {
      setBusy(false);
    }
  };

  return (
    <div className="flex flex-col gap-4">
      {ticket.comments.length === 0 ? (
        <div className="py-6 text-center text-[12.5px] text-fg-faint">No comments.</div>
      ) : (
        <ul className="flex flex-col gap-2">
          {ticket.comments.map((c) => {
            const Icon = authorIcon[c.author] ?? User;
            return (
              <li key={c.id} className={cn("rounded-md border px-3 py-2", c.author === "agent" ? "border-accent/30" : c.author === "system" ? "border-line/60 bg-surface-2/40" : "border-line")}>
                <div className="mb-1 flex items-center gap-1.5 text-[11.5px] text-fg-muted">
                  <Icon size={12} className={c.author === "agent" ? "text-accent" : ""} />
                  <span className="font-medium text-fg">{c.author}</span>
                  <span className="text-fg-faint">{formatDateTime(c.createdAt)}</span>
                </div>
                <Markdown>{c.text}</Markdown>
              </li>
            );
          })}
        </ul>
      )}
      <div className="flex flex-col gap-2">
        <Textarea
          rows={3}
          value={text}
          onChange={(e) => setText(e.target.value)}
          onKeyDown={(e) => {
            if ((e.metaKey || e.ctrlKey) && e.key === "Enter") void submit();
          }}
          placeholder="Add a comment (markdown, Ctrl+Enter to send)"
        />
        <div className="flex justify-end">
          <Button variant="primary" icon={<Send size={12} />} disabled={!text.trim()} loading={busy} onClick={() => void submit()}>
            Comment
          </Button>
        </div>
      </div>
    </div>
  );
}
