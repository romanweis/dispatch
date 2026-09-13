import { useEffect, type ReactNode } from "react";
import { X } from "lucide-react";
import { cn } from "@/lib/utils";

interface DialogProps {
  open: boolean;
  onClose: () => void;
  title: ReactNode;
  children: ReactNode;
  footer?: ReactNode;
  className?: string;
}

export function Dialog({ open, onClose, title, children, footer, className }: DialogProps) {
  useEffect(() => {
    if (!open) return;
    const onKey = (e: KeyboardEvent) => {
      if (e.key === "Escape") {
        e.stopPropagation();
        onClose();
      }
    };
    window.addEventListener("keydown", onKey, true);
    return () => window.removeEventListener("keydown", onKey, true);
  }, [open, onClose]);

  if (!open) return null;
  return (
    <div className="fixed inset-0 z-50 flex items-start justify-center bg-black/50 p-4 pt-[12vh]" onMouseDown={onClose} role="presentation">
      <div
        role="dialog"
        aria-modal="true"
        onMouseDown={(e) => e.stopPropagation()}
        className={cn("w-full max-w-lg rounded-lg border border-line bg-surface shadow-2xl shadow-black/40", className)}
      >
        <div className="flex items-center justify-between border-b border-line px-4 h-10">
          <h2 className="text-[13px] font-semibold">{title}</h2>
          <button type="button" onClick={onClose} className="text-fg-muted hover:text-fg rounded p-0.5" aria-label="Close">
            <X size={15} />
          </button>
        </div>
        <div className="px-4 py-3">{children}</div>
        {footer && <div className="flex items-center justify-end gap-2 border-t border-line px-4 h-11">{footer}</div>}
      </div>
    </div>
  );
}
