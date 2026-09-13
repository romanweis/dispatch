import { readFileSync } from "node:fs";
import { resolve } from "node:path";
import { describe, expect, it } from "vitest";
import { parseEventLine, summarizeEvent, summarizeToolInput, toolResultText, type EventCard } from "./events";
import type { ClaudeEvent } from "./types";

const FIXTURES = resolve(__dirname, "../../../server/Dispatch.Claude.Tests/Fixtures");

function loadFixture(name: string): ClaudeEvent[] {
  const text = readFileSync(resolve(FIXTURES, name), "utf8");
  return text
    .split(/\r?\n/)
    .map(parseEventLine)
    .filter((e): e is ClaudeEvent => e !== null);
}

function cardsOf(events: ClaudeEvent[]): EventCard[] {
  return events.flatMap((e, i) => summarizeEvent(e, i + 1));
}

describe("summarizeEvent on fixtures", () => {
  it("maps session-with-tool.jsonl to init, text, tool_use, tool_result, text, result", () => {
    const events = loadFixture("session-with-tool.jsonl");
    expect(events).toHaveLength(5);
    const cards = cardsOf(events);
    expect(cards.map((c) => c.kind)).toEqual(["init", "assistant_text", "tool_use", "tool_result", "assistant_text", "result"]);

    const init = cards[0];
    expect(init.kind).toBe("init");
    if (init.kind === "init") {
      expect(init.model).toBe("claude-opus-4-7");
      expect(init.cwd).toBe("C:/work");
      expect(init.toolCount).toBe(1);
      expect(init.permissionMode).toBe("acceptEdits");
      expect(init.sessionId).toBe("sess-tool");
    }

    const text = cards[1];
    if (text.kind === "assistant_text") expect(text.text).toBe("Reading the file.");

    const tool = cards[2];
    expect(tool.kind).toBe("tool_use");
    if (tool.kind === "tool_use") {
      expect(tool.id).toBe("toolu_42");
      expect(tool.name).toBe("Read");
      expect(tool.summary).toBe("C:/work/README.md");
      expect(JSON.parse(tool.inputJson)).toEqual({ file_path: "C:/work/README.md" });
      // stable keys: seq + block index
      expect(tool.key).toBe("2:1");
      expect(text.key).toBe("2:0");
    }

    const result = cards[3];
    expect(result.kind).toBe("tool_result");
    if (result.kind === "tool_result") {
      expect(result.toolUseId).toBe("toolu_42");
      expect(result.isError).toBe(false);
      expect(result.text).toBe("file contents here");
    }

    const final = cards[5];
    expect(final.kind).toBe("result");
    if (final.kind === "result") {
      expect(final.subtype).toBe("success");
      expect(final.isError).toBe(false);
      expect(final.numTurns).toBe(2);
      expect(final.durationMs).toBe(2500);
      expect(final.apiMs).toBe(1800);
      expect(final.costUsd).toBeNull();
    }
  });

  it("maps session-basic.jsonl", () => {
    const events = loadFixture("session-basic.jsonl");
    expect(events.length).toBeGreaterThan(0);
    const cards = cardsOf(events);
    expect(cards[0].kind).toBe("init");
    expect(cards.at(-1)?.kind).toBe("result");
    // Every card has a unique key.
    expect(new Set(cards.map((c) => c.key)).size).toBe(cards.length);
  });
});

describe("summarizeEvent edge cases", () => {
  it("surfaces Bash commands and cost", () => {
    const cards = summarizeEvent(
      {
        type: "assistant",
        message: { role: "assistant", content: [{ type: "tool_use", id: "t1", name: "Bash", input: { command: "pnpm test", description: "Run tests" } }] },
      },
      3,
    );
    expect(cards).toHaveLength(1);
    expect(cards[0].kind === "tool_use" && cards[0].summary).toBe("pnpm test");

    const [res] = summarizeEvent({ type: "result", subtype: "success", is_error: false, total_cost_usd: 0.1234, duration_ms: 10 }, 4);
    expect(res.kind === "result" && res.costUsd).toBe(0.1234);
  });

  it("drops empty text blocks and keeps thinking blocks", () => {
    const cards = summarizeEvent(
      { type: "assistant", message: { role: "assistant", content: [{ type: "text", text: "   " }, { type: "thinking", thinking: "hmm" }] } },
      1,
    );
    expect(cards.map((c) => c.kind)).toEqual(["thinking"]);
  });

  it("flattens array tool_result content and flags errors", () => {
    const cards = summarizeEvent(
      {
        type: "user",
        message: {
          role: "user",
          content: [{ type: "tool_result", tool_use_id: "x", is_error: true, content: [{ type: "text", text: "line 1" }, { type: "text", text: "line 2" }] }],
        },
      },
      9,
    );
    expect(cards[0].kind).toBe("tool_result");
    if (cards[0].kind === "tool_result") {
      expect(cards[0].isError).toBe(true);
      expect(cards[0].text).toBe("line 1\nline 2");
    }
  });

  it("treats a plain string user message as user_text", () => {
    const cards = summarizeEvent({ type: "user", message: { role: "user", content: "hello" } }, 2);
    expect(cards).toEqual([{ kind: "user_text", key: "2:0", seq: 2, text: "hello" }]);
  });

  it("falls back to unknown for unrecognised types and subtypes", () => {
    const [a] = summarizeEvent({ type: "system", subtype: "compact_boundary", foo: 1 }, 5);
    expect(a.kind).toBe("unknown");
    if (a.kind === "unknown") {
      expect(a.type).toBe("system");
      expect(a.subtype).toBe("compact_boundary");
      expect(JSON.parse(a.raw)).toEqual({ type: "system", subtype: "compact_boundary", foo: 1 });
    }
    const [b] = summarizeEvent({ type: "rate_limit_event" }, 6);
    expect(b.kind).toBe("unknown");
    const [c] = summarizeEvent(null as unknown as ClaudeEvent, 7);
    expect(c.kind === "unknown" && c.type).toBe("invalid");
  });

  it("parseEventLine ignores blank and malformed lines", () => {
    expect(parseEventLine("")).toBeNull();
    expect(parseEventLine("{not json")).toBeNull();
    expect(parseEventLine('{"no":"type"}')).toBeNull();
    expect(parseEventLine('{"type":"result"}')).toEqual({ type: "result" });
  });
});

describe("helpers", () => {
  it("summarizeToolInput picks meaningful fields per tool", () => {
    expect(summarizeToolInput("Grep", { pattern: "foo", path: "src" })).toBe("foo  in src");
    expect(summarizeToolInput("Edit", { file_path: "a.ts", old_string: "x" })).toBe("a.ts");
    expect(summarizeToolInput("WebFetch", { url: "https://x.y" })).toBe("https://x.y");
    expect(summarizeToolInput("TodoWrite", { todos: [1, 2] })).toBe("2 todos");
    expect(summarizeToolInput("Custom", { query: "q" })).toBe("q");
    expect(summarizeToolInput("Custom", "string input")).toBeNull();
  });

  it("toolResultText handles strings, arrays, objects and nulls", () => {
    expect(toolResultText("s")).toBe("s");
    expect(toolResultText(null)).toBe("");
    expect(toolResultText([{ type: "image" }])).toBe("[image]");
    expect(toolResultText({ a: 1 })).toBe('{\n  "a": 1\n}');
  });
});
