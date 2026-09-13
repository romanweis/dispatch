import { useState } from "react";
import { Check, Copy } from "lucide-react";
import { cn } from "@/lib/utils";

export function CodeBox({ code, className, maxHeight }: { code: string; className?: string; maxHeight?: number }) {
  const [copied, setCopied] = useState(false);
  const copy = async () => {
    try {
      await navigator.clipboard.writeText(code);
      setCopied(true);
      setTimeout(() => setCopied(false), 1200);
    } catch {
      /* clipboard unavailable (insecure context); ignore */
    }
  };
  return (
    <div className={cn("group relative rounded-md border border-line bg-surface-2", className)}>
      <pre
        className="overflow-auto whitespace-pre-wrap break-all p-2.5 pr-9 font-mono text-[12px] leading-relaxed text-fg"
        style={maxHeight ? { maxHeight } : undefined}
      >
        {code}
      </pre>
      <button
        type="button"
        onClick={copy}
        aria-label="Copy"
        className="absolute right-1.5 top-1.5 rounded border border-line bg-surface p-1 text-fg-muted opacity-70 hover:opacity-100 hover:text-fg"
      >
        {copied ? <Check size={12} className="text-ok" /> : <Copy size={12} />}
      </button>
    </div>
  );
}
