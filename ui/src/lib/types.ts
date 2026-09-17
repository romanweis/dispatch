// Mirror of docs/api.md. Keep in sync with the contract.

/** feature: refined into a spec first. task: skips refinement, the body is the spec. */
export type TicketType = "feature" | "task";

export type TicketStatus =
  | "backlog"
  | "refining"
  | "needs_input"
  | "ready"
  | "in_progress"
  | "review"
  | "done"
  | "failed";

export const TICKET_STATUSES: readonly TicketStatus[] = [
  "backlog",
  "refining",
  "needs_input",
  "ready",
  "in_progress",
  "review",
  "done",
  "failed",
] as const;

export const STATUS_LABEL: Record<TicketStatus, string> = {
  backlog: "Backlog",
  refining: "Refining",
  needs_input: "Needs input",
  ready: "Ready",
  in_progress: "In progress",
  review: "Review",
  done: "Done",
  failed: "Failed",
};

export type RunKind = "refine" | "answer" | "work" | "resume" | "ship";
export type RunStatus = "pending" | "running" | "done" | "failed" | "cancelled";
export type CommentAuthor = "user" | "agent" | "system";
export type ContainerState = "none" | "running" | "stopped" | "missing";

export interface Project {
  id: number;
  name: string;
  displayName: string;
  org: string;
  workspace: string;
  baseContainer: string;
  maxParallel: number;
  repos: string[];
  createdAt: string;
}

export interface LastProgress {
  phase: string;
  note: string | null;
  at: string;
}

/** Raw tasks/<id>/state.json from the orchestrator repo. Shape is only loosely known. */
export interface WorkflowSlice {
  id?: string;
  name?: string;
  repo?: string;
  tier?: string | number;
  pr?: string | number | null;
  prUrl?: string | null;
  prNumber?: number | null;
  blockers?: number | string[] | null;
  majors?: number | string[] | null;
  merged?: boolean | null;
  status?: string;
  [key: string]: unknown;
}

export interface WorkflowState {
  phase?: string;
  gate?: string;
  round?: number;
  slices?: WorkflowSlice[];
  [key: string]: unknown;
}

/** Entry of raw tasks/<id>/result.json `prs`. */
export interface ResultPr {
  repo?: string;
  url?: string;
  number?: number;
  title?: string;
  ci?: string;
  ciState?: string;
  state?: string;
  merged?: boolean;
  [key: string]: unknown;
}

export interface TicketResult {
  prs?: ResultPr[];
  [key: string]: unknown;
}

export interface Ticket {
  id: number;
  projectId: number;
  projectName: string;
  title: string;
  /** markdown */
  body: string;
  type: TicketType;
  status: TicketStatus;
  /** when true a passed review gate queues a ship run (/ship-feature) instead of waiting in review */
  autoMerge: boolean;
  slug: string | null;
  /** markdown, agent-written during refinement */
  spec: string | null;
  container: string | null;
  containerState: ContainerState;
  claudeSessionId: string | null;
  workflowState: WorkflowState | null;
  result: TicketResult | null;
  openQuestions: number;
  activeRunId: number | null;
  lastProgress: LastProgress | null;
  createdAt: string;
  updatedAt: string;
}

export interface Question {
  id: number;
  ticketId: number;
  text: string;
  answer: string | null;
  askedAt: string;
  answeredAt: string | null;
}

export interface Comment {
  id: number;
  ticketId: number;
  author: CommentAuthor;
  text: string;
  createdAt: string;
}

export interface Run {
  id: number;
  ticketId: number;
  kind: RunKind;
  status: RunStatus;
  sessionId: string | null;
  startedAt: string | null;
  endedAt: string | null;
  exitCode: number | null;
  error: string | null;
  eventCount: number;
  createdAt: string;
}

export interface RunEvent {
  seq: number;
  recordedAt: string;
  /** raw claude stream-json event */
  payload: ClaudeEvent;
}

export interface TicketDetail extends Ticket {
  questions: Question[];
  comments: Comment[];
  runs: Run[];
  attachCommand: string;
}

export interface ApiErrorBody {
  error: string;
  code: string;
}

// ---- claude stream-json events -------------------------------------------

export interface TextBlock {
  type: "text";
  text: string;
}
export interface ThinkingBlock {
  type: "thinking";
  thinking: string;
}
export interface ToolUseBlock {
  type: "tool_use";
  id: string;
  name: string;
  input: unknown;
}
export interface ToolResultBlock {
  type: "tool_result";
  tool_use_id: string;
  content: unknown;
  is_error?: boolean;
}
export type UnknownBlock = { type: string; [key: string]: unknown };
export type ContentBlock = TextBlock | ThinkingBlock | ToolUseBlock | ToolResultBlock | UnknownBlock;

export interface SystemInitEvent {
  type: "system";
  subtype: "init";
  session_id?: string;
  model?: string;
  cwd?: string;
  tools?: string[];
  permissionMode?: string;
  [key: string]: unknown;
}

export interface AssistantEvent {
  type: "assistant";
  message: {
    id?: string;
    role: "assistant";
    content: ContentBlock[];
    stop_reason?: string | null;
    usage?: Record<string, number | undefined>;
  };
  [key: string]: unknown;
}

export interface UserEvent {
  type: "user";
  message: {
    role: "user";
    content: ContentBlock[] | string;
  };
  [key: string]: unknown;
}

export interface ResultEvent {
  type: "result";
  subtype: string;
  is_error?: boolean;
  num_turns?: number;
  duration_ms?: number;
  duration_api_ms?: number;
  total_cost_usd?: number;
  session_id?: string;
  result?: string | null;
  [key: string]: unknown;
}

export type ClaudeEvent =
  | SystemInitEvent
  | AssistantEvent
  | UserEvent
  | ResultEvent
  | ({ type: string } & Record<string, unknown>);
