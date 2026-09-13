import type { ContainerState, RunStatus, TicketStatus } from "@/lib/types";

export type Tone = "neutral" | "accent" | "ok" | "warn" | "danger" | "info";

export const statusTone: Record<TicketStatus, Tone> = {
  backlog: "neutral",
  refining: "info",
  needs_input: "warn",
  ready: "accent",
  in_progress: "info",
  review: "accent",
  done: "ok",
  failed: "danger",
};

export const runStatusTone: Record<RunStatus, Tone> = {
  pending: "neutral",
  running: "info",
  done: "ok",
  failed: "danger",
  cancelled: "warn",
};

export const containerTone: Record<ContainerState, Tone> = {
  none: "neutral",
  running: "ok",
  stopped: "warn",
  missing: "danger",
};
