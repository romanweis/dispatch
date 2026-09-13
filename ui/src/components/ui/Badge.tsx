import type { ReactNode } from "react";
import { cn } from "@/lib/utils";
import type { Tone } from "./tones";

export type { Tone } from "./tones";

const tones: Record<Tone, string> = {
  neutral: "bg-surface-3 text-fg-muted",
  accent: "bg-accent-soft text-accent",
  ok: "bg-ok-soft text-ok",
  warn: "bg-warn-soft text-warn",
  danger: "bg-danger-soft text-danger",
  info: "bg-info-soft text-info",
};

const dots: Record<Tone, string> = {
  neutral: "bg-fg-faint",
  accent: "bg-accent",
  ok: "bg-ok",
  warn: "bg-warn",
  danger: "bg-danger",
  info: "bg-info",
};

export function Badge({ tone = "neutral", className, children, title }: { tone?: Tone; className?: string; children: ReactNode; title?: string }) {
  return (
    <span
      title={title}
      className={cn("inline-flex items-center gap-1 rounded px-1.5 h-[18px] text-[11px] font-medium leading-none whitespace-nowrap", tones[tone], className)}
    >
      {children}
    </span>
  );
}

/** Small colored dot used in column headers. */
export function Dot({ tone, className }: { tone: Tone; className?: string }) {
  return <span className={cn("inline-block size-2 rounded-full", dots[tone], className)} />;
}
