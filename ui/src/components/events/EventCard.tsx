import { memo, useState, type ReactNode } from "react";
import { AlertTriangle, Bot, Brain, ChevronDown, ChevronRight, Cpu, FileJson, Flag, Terminal, User, Wrench } from "lucide-react";
import type { EventCard as Card } from "@/lib/events";
import { cn, formatMs, formatUsd, truncate } from "@/lib/utils";
import { Badge, type Tone } from "@/components/ui/Badge";
import { Markdown } from "@/components/ui/Markdown";

const RESULT_PREVIEW = 2000;

function Row({ icon, tone = "neutral", title, children, className }: { icon: ReactNode; tone?: Tone; title: ReactNode; children?: ReactNode; className?: string }) {
  const rail: Record<Tone, string> = {
    neutral: "border-l-line-strong",
    accent: "border-l-accent",
    ok: "border-l-ok",
    warn: "border-l-warn",
    danger: "border-l-danger",
    info: "border-l-info",
  };
  return (
    <div className={cn("rounded-md border border-line border-l-2 bg-surface px-2.5 py-1.5", rail[tone], className)}>
      <div className="flex min-w-0 items-center gap-2 text-[12px] text-fg-muted">
        <span className="shrink-0">{icon}</span>
        <div className="flex min-w-0 flex-1 items-center gap-2">{title}</div>
      </div>
      {children && <div className="mt-1.5">{children}</div>}
    </div>
  );
}

function Toggle({ open, onToggle, label }: { open: boolean; onToggle: () => void; label: string }) {
  return (
    <button type="button" onClick={onToggle} className="inline-flex items-center gap-1 text-[11.5px] text-fg-muted hover:text-fg">
      {open ? <ChevronDown size={12} /> : <ChevronRight size={12} />}
      {label}
    </button>
  );
}

function Pre({ children, className }: { children: string; className?: string }) {
  return <pre className={cn("mt-1.5 max-h-[420px] overflow-auto whitespace-pre-wrap break-all rounded bg-surface-2 p-2 font-mono text-[11.5px] leading-relaxed text-fg", className)}>{children}</pre>;
}

export const EventCardView = memo(function EventCardView({ card, toolNames }: { card: Card; toolNames?: Record<string, string> }) {
  switch (card.kind) {
    case "init":
      return (
        <Row icon={<Cpu size={13} />} tone="info" title={<><span className="font-medium text-fg">session</span>{card.model && <Badge tone="info">{card.model}</Badge>}{card.permissionMode && <Badge>{card.permissionMode}</Badge>}<span className="ml-auto font-mono text-[11px] text-fg-faint">{card.toolCount} tools</span></>}>
          {card.cwd && <div className="font-mono text-[11.5px] text-fg-muted">cwd {card.cwd}</div>}
        </Row>
      );
    case "assistant_text":
      return (
        <Row icon={<Bot size={13} className="text-accent" />} tone="accent" title={<span className="font-medium text-fg">assistant</span>}>
          <Markdown>{card.text}</Markdown>
        </Row>
      );
    case "user_text":
      return (
        <Row icon={<User size={13} />} title={<span className="font-medium text-fg">user</span>}>
          <div className="whitespace-pre-wrap text-[12.5px]">{card.text}</div>
        </Row>
      );
    case "thinking":
      return <ThinkingCard text={card.text} />;
    case "tool_use":
      return <ToolUseCard card={card} />;
    case "tool_result":
      return <ToolResultCard card={card} toolName={toolNames?.[card.toolUseId]} />;
    case "result":
      return (
        <Row
          icon={<Flag size={13} />}
          tone={card.isError ? "danger" : "ok"}
          title={
            <>
              <span className="font-medium text-fg">result</span>
              <Badge tone={card.isError ? "danger" : "ok"}>{card.subtype}</Badge>
              <span className="ml-auto flex items-center gap-3 font-mono text-[11px] text-fg-faint">
                {card.numTurns !== null && <span>{card.numTurns} turns</span>}
                <span>{formatMs(card.durationMs)}</span>
                {card.apiMs !== null && <span title="API time">api {formatMs(card.apiMs)}</span>}
                {card.costUsd !== null && <span>{formatUsd(card.costUsd)}</span>}
              </span>
            </>
          }
        >
          {card.text && <Markdown>{card.text}</Markdown>}
        </Row>
      );
    case "unknown":
      return <UnknownCard card={card} />;
  }
});

function ThinkingCard({ text }: { text: string }) {
  const [open, setOpen] = useState(false);
  return (
    <Row icon={<Brain size={13} />} title={<><span className="font-medium text-fg">thinking</span><span className="ml-auto"><Toggle open={open} onToggle={() => setOpen((v) => !v)} label={`${text.length} chars`} /></span></>}>
      {open && <div className="whitespace-pre-wrap text-[12px] italic text-fg-muted">{text}</div>}
    </Row>
  );
}

function ToolUseCard({ card }: { card: Extract<Card, { kind: "tool_use" }> }) {
  const [open, setOpen] = useState(false);
  const isBash = card.name === "Bash" || card.name === "PowerShell";
  return (
    <Row
      icon={isBash ? <Terminal size={13} className="text-warn" /> : <Wrench size={13} className="text-warn" />}
      tone="warn"
      title={
        <>
          <Badge tone="warn">{card.name}</Badge>
          {card.summary && !isBash && <span className="truncate font-mono text-[11.5px] text-fg">{card.summary}</span>}
          <span className="ml-auto shrink-0">
            <Toggle open={open} onToggle={() => setOpen((v) => !v)} label="input" />
          </span>
        </>
      }
    >
      {isBash && card.summary && <pre className="whitespace-pre-wrap break-all rounded bg-surface-2 p-2 font-mono text-[11.5px] text-fg">{card.summary}</pre>}
      {open && <Pre>{card.inputJson}</Pre>}
    </Row>
  );
}

function ToolResultCard({ card, toolName }: { card: Extract<Card, { kind: "tool_result" }>; toolName?: string }) {
  const [open, setOpen] = useState(false);
  const [full, setFull] = useState(false);
  const { text, truncated } = truncate(card.text, RESULT_PREVIEW);
  const lines = card.text.split("\n").length;
  return (
    <Row
      icon={card.isError ? <AlertTriangle size={13} className="text-danger" /> : <ChevronRight size={13} className="text-fg-faint" />}
      tone={card.isError ? "danger" : "neutral"}
      className="bg-surface/60"
      title={
        <>
          <span className="text-fg-muted">
            result{toolName && <span className="text-fg-faint"> · {toolName}</span>}
          </span>
          {card.isError && <Badge tone="danger">error</Badge>}
          <span className="ml-auto flex items-center gap-2">
            <span className="font-mono text-[11px] text-fg-faint">{card.text.length} chars · {lines} lines</span>
            <Toggle open={open} onToggle={() => setOpen((v) => !v)} label={open ? "hide" : "show"} />
          </span>
        </>
      }
    >
      {open && (
        <>
          <Pre>{full ? card.text : text}</Pre>
          {truncated && (
            <button type="button" onClick={() => setFull((v) => !v)} className="mt-1 text-[11.5px] text-accent hover:underline">
              {full ? "Show less" : `Show all (${card.text.length - RESULT_PREVIEW} more chars)`}
            </button>
          )}
        </>
      )}
    </Row>
  );
}

function UnknownCard({ card }: { card: Extract<Card, { kind: "unknown" }> }) {
  const [open, setOpen] = useState(false);
  return (
    <Row icon={<FileJson size={13} />} title={<><span className="font-mono text-[11.5px] text-fg">{card.type}</span>{card.subtype && <Badge>{card.subtype}</Badge>}<span className="ml-auto"><Toggle open={open} onToggle={() => setOpen((v) => !v)} label="raw" /></span></>}>
      {open && <Pre>{card.raw}</Pre>}
    </Row>
  );
}
