import type { ClaudeEvent, ContentBlock, RunEvent } from "./types";
import { isRecord, safeStringify } from "./utils";

/**
 * Card models the event stream renders. One raw stream-json event can expand
 * to several cards (an assistant message with text + tool_use blocks becomes
 * two cards). `key` is stable per run so React lists stay keyed on replay.
 */
export type EventCard =
  | { kind: "init"; key: string; seq: number; model: string | null; cwd: string | null; toolCount: number; sessionId: string | null; permissionMode: string | null }
  | { kind: "assistant_text"; key: string; seq: number; text: string }
  | { kind: "thinking"; key: string; seq: number; text: string }
  | { kind: "tool_use"; key: string; seq: number; id: string; name: string; input: unknown; summary: string | null; inputJson: string }
  | { kind: "tool_result"; key: string; seq: number; toolUseId: string; isError: boolean; text: string }
  | { kind: "user_text"; key: string; seq: number; text: string }
  | { kind: "result"; key: string; seq: number; subtype: string; isError: boolean; durationMs: number | null; apiMs: number | null; costUsd: number | null; numTurns: number | null; text: string | null }
  | { kind: "unknown"; key: string; seq: number; type: string; subtype: string | null; raw: string };

export type EventCardKind = EventCard["kind"];

const num = (v: unknown): number | null => (typeof v === "number" && Number.isFinite(v) ? v : null);
const str = (v: unknown): string | null => (typeof v === "string" ? v : null);

/** Extract the human-meaningful part of a tool input (Bash command, file path, pattern...). */
export function summarizeToolInput(name: string, input: unknown): string | null {
  if (!isRecord(input)) return null;
  const pick = (...keys: string[]) => {
    for (const k of keys) {
      const v = input[k];
      if (typeof v === "string" && v.trim()) return v.trim();
    }
    return null;
  };
  switch (name) {
    case "Bash":
    case "PowerShell":
      return pick("command");
    case "Read":
    case "Write":
    case "Edit":
    case "MultiEdit":
    case "NotebookEdit":
      return pick("file_path", "path", "notebook_path");
    case "Glob":
    case "Grep": {
      const pattern = pick("pattern");
      const path = pick("path");
      return pattern ? (path ? `${pattern}  in ${path}` : pattern) : path;
    }
    case "WebFetch":
    case "WebSearch":
      return pick("url", "query");
    case "Task":
    case "Agent":
      return pick("description", "prompt");
    case "TodoWrite":
      return Array.isArray(input.todos) ? `${input.todos.length} todos` : null;
    default:
      return pick("command", "file_path", "path", "pattern", "query", "url", "description");
  }
}

/** Flatten tool_result content (string or array of text blocks) to a display string. */
export function toolResultText(content: unknown): string {
  if (typeof content === "string") return content;
  if (Array.isArray(content)) {
    const parts: string[] = [];
    for (const c of content) {
      if (typeof c === "string") parts.push(c);
      else if (isRecord(c) && c.type === "text" && typeof c.text === "string") parts.push(c.text);
      else if (isRecord(c) && c.type === "image") parts.push("[image]");
      else parts.push(safeStringify(c));
    }
    return parts.join("\n");
  }
  if (content === undefined || content === null) return "";
  return safeStringify(content);
}

function blocksOf(content: unknown): ContentBlock[] {
  if (Array.isArray(content)) return content.filter(isRecord) as unknown as ContentBlock[];
  if (typeof content === "string") return [{ type: "text", text: content }];
  return [];
}

/** Map a raw stream-json event into zero or more cards. */
export function summarizeEvent(payload: ClaudeEvent, seq: number): EventCard[] {
  if (!isRecord(payload) || typeof payload.type !== "string") {
    return [{ kind: "unknown", key: `${seq}`, seq, type: "invalid", subtype: null, raw: safeStringify(payload) }];
  }
  const type = payload.type;
  const subtype = str(payload.subtype);

  if (type === "system" && subtype === "init") {
    const tools = Array.isArray(payload.tools) ? payload.tools : [];
    return [
      {
        kind: "init",
        key: `${seq}`,
        seq,
        model: str(payload.model),
        cwd: str(payload.cwd),
        toolCount: tools.length,
        sessionId: str(payload.session_id),
        permissionMode: str(payload.permissionMode),
      },
    ];
  }

  if (type === "assistant" || type === "user") {
    const message = isRecord(payload.message) ? payload.message : null;
    const blocks = blocksOf(message?.content);
    const cards: EventCard[] = [];
    blocks.forEach((block, i) => {
      const key = `${seq}:${i}`;
      switch (block.type) {
        case "text": {
          const text = str(block.text) ?? "";
          if (!text.trim()) break;
          cards.push(type === "assistant" ? { kind: "assistant_text", key, seq, text } : { kind: "user_text", key, seq, text });
          break;
        }
        case "thinking": {
          const text = str((block as { thinking?: unknown }).thinking) ?? "";
          if (text.trim()) cards.push({ kind: "thinking", key, seq, text });
          break;
        }
        case "tool_use": {
          const b = block as { id?: unknown; name?: unknown; input?: unknown };
          const name = str(b.name) ?? "tool";
          cards.push({
            kind: "tool_use",
            key,
            seq,
            id: str(b.id) ?? key,
            name,
            input: b.input,
            summary: summarizeToolInput(name, b.input),
            inputJson: safeStringify(b.input),
          });
          break;
        }
        case "tool_result": {
          const b = block as { tool_use_id?: unknown; content?: unknown; is_error?: unknown };
          cards.push({
            kind: "tool_result",
            key,
            seq,
            toolUseId: str(b.tool_use_id) ?? "",
            isError: b.is_error === true,
            text: toolResultText(b.content),
          });
          break;
        }
        default:
          cards.push({ kind: "unknown", key, seq, type: `${type}/${block.type}`, subtype: null, raw: safeStringify(block) });
      }
    });
    return cards;
  }

  if (type === "result") {
    return [
      {
        kind: "result",
        key: `${seq}`,
        seq,
        subtype: subtype ?? "unknown",
        isError: payload.is_error === true,
        durationMs: num(payload.duration_ms),
        apiMs: num(payload.duration_api_ms),
        costUsd: num(payload.total_cost_usd),
        numTurns: num(payload.num_turns),
        text: str(payload.result),
      },
    ];
  }

  return [{ kind: "unknown", key: `${seq}`, seq, type, subtype, raw: safeStringify(payload) }];
}

export function summarizeRunEvent(ev: RunEvent): EventCard[] {
  return summarizeEvent(ev.payload, ev.seq);
}

/** Parse one jsonl line into an event; returns null for blank/invalid lines. */
export function parseEventLine(line: string): ClaudeEvent | null {
  const t = line.trim();
  if (!t) return null;
  try {
    const v = JSON.parse(t) as unknown;
    return isRecord(v) && typeof v.type === "string" ? (v as ClaudeEvent) : null;
  } catch {
    return null;
  }
}
