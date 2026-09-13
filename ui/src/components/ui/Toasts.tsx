import { AlertCircle, CheckCircle2, Info, X } from "lucide-react";
import { useToasts, type ToastTone } from "@/store/toasts";
import { cn } from "@/lib/utils";

const styles: Record<ToastTone, { icon: typeof Info; cls: string }> = {
  error: { icon: AlertCircle, cls: "border-danger/40 text-danger" },
  info: { icon: Info, cls: "border-info/40 text-info" },
  success: { icon: CheckCircle2, cls: "border-ok/40 text-ok" },
};

export function Toasts() {
  const toasts = useToasts((s) => s.toasts);
  const dismiss = useToasts((s) => s.dismiss);
  if (toasts.length === 0) return null;
  return (
    <div className="pointer-events-none fixed bottom-4 right-4 z-[60] flex w-[360px] max-w-[calc(100vw-2rem)] flex-col gap-2">
      {toasts.map((t) => {
        const { icon: Icon, cls } = styles[t.tone];
        return (
          <div
            key={t.id}
            role="status"
            className={cn("toast-in pointer-events-auto flex items-start gap-2 rounded-md border bg-surface px-3 py-2 shadow-lg shadow-black/30", cls)}
          >
            <Icon size={15} className="mt-0.5 shrink-0" />
            <div className="min-w-0 flex-1">
              <div className="text-[12.5px] font-medium text-fg break-words">{t.title}</div>
              {t.detail && <div className="mt-0.5 font-mono text-[11px] text-fg-muted">{t.detail}</div>}
            </div>
            <button type="button" onClick={() => dismiss(t.id)} className="text-fg-faint hover:text-fg" aria-label="Dismiss">
              <X size={13} />
            </button>
          </div>
        );
      })}
    </div>
  );
}
