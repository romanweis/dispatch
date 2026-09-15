# Dispatch HTTP API contract

Base URL: `http://<server>:9300`. JSON everywhere, `camelCase` property names, timestamps ISO 8601 UTC.
No auth for the UI. The in-container `ticket` CLI sends `Authorization: Bearer <DISPATCH_TOKEN>`; the token is per ticket and only valid for that ticket's `/api/tickets/{id}/...` routes.

## Enums

```
TicketStatus: backlog | refining | needs_input | ready | in_progress | review | done | failed
RunKind:      refine | answer | work | resume | ship
RunStatus:    pending | running | done | failed | cancelled
CommentAuthor: user | agent | system
```

## Shapes

```ts
Project {
  id: number; name: string; displayName: string; org: string;
  workspace: string;            // e.g. "/home/agent/bognerchess"
  baseContainer: string;        // e.g. "bognerchess-base"
  maxParallel: number;
  repos: string[];
  createdAt: string;
}

Ticket {
  id: number; projectId: number; projectName: string;
  title: string; body: string;          // body is markdown
  status: TicketStatus;
  autoMerge: boolean;                   // passed review gate -> ship run instead of waiting in review
  slug: string | null;                  // set when work starts, kebab-case + 3 char salt
  spec: string | null;                  // markdown, agent-written during refinement
  container: string | null;             // "t-<id>" once created
  containerState: "none" | "running" | "stopped" | "missing";
  claudeSessionId: string | null;
  workflowState: object | null;         // raw tasks/<id>/state.json from the orchestrator repo
  result: object | null;                // raw tasks/<id>/result.json
  openQuestions: number;
  activeRunId: number | null;
  lastProgress: { phase: string; note: string | null; at: string } | null;   // latest `ticket progress` note
  createdAt: string; updatedAt: string;
}

Question { id: number; ticketId: number; text: string; answer: string | null; askedAt: string; answeredAt: string | null; }
Comment  { id: number; ticketId: number; author: CommentAuthor; text: string; createdAt: string; }
Run      { id: number; ticketId: number; kind: RunKind; status: RunStatus; sessionId: string | null;
           startedAt: string | null; endedAt: string | null; exitCode: number | null; error: string | null;
           eventCount: number; createdAt: string; }
RunEvent { seq: number; recordedAt: string; payload: object; }   // payload = raw claude stream-json event
TicketDetail = Ticket & { questions: Question[]; comments: Comment[]; runs: Run[]; attachCommand: string; }
```

## Endpoints (UI)

| Method | Path | Body / Query | Returns | Notes |
|---|---|---|---|---|
| GET | `/api/projects` | | `Project[]` | |
| GET | `/api/tickets` | `?projectId=&status=` | `Ticket[]` | ordered by updatedAt desc |
| POST | `/api/tickets` | `{projectId, title, body, autoMerge?}` | `Ticket` 201 | status `backlog` |
| GET | `/api/tickets/{id}` | | `TicketDetail` | |
| PATCH | `/api/tickets/{id}` | `{title?, body?, spec?, autoMerge?}` | `Ticket` | spec editable only in `ready`/`needs_input`/`backlog`; `autoMerge` any time |
| DELETE | `/api/tickets/{id}` | | 204 | deletes container too |
| POST | `/api/tickets/{id}/refine` | | `Run` 202 | allowed from `backlog`, `needs_input` (when no unanswered questions), `failed` |
| POST | `/api/tickets/{id}/answer` | `{answers: [{questionId, answer}]}` | `Run` 202 | stores answers, then queues run kind `answer` (resume) |
| POST | `/api/tickets/{id}/start` | | `Run` 202 | from `ready`; needs `spec`; sets slug, writes feature file, runs work |
| POST | `/api/tickets/{id}/ship` | | `Run` 202 | from `review` with `workflowState.gate == PASSED`; queues run kind `ship` (`/ship-feature`), ticket -> `in_progress` |
| POST | `/api/tickets/{id}/resume` | `{message}` | `Run` 202 | free-form message into the existing session, from any non-running state with a session |
| POST | `/api/tickets/{id}/cancel` | | `Run` | kills the active run, ticket -> `failed` |
| POST | `/api/tickets/{id}/done` | `{snapshot?: boolean}` | `Ticket` | from `review`/`in_progress`/`failed`; deletes container |
| POST | `/api/tickets/{id}/move` | `{status}` | `Ticket` | manual override for board drag (only between non-running states) |
| POST | `/api/tickets/{id}/comments` | `{text}` | `Comment` 201 | author `user` |
| POST | `/api/tickets/{id}/container/refresh` | | `Ticket` | re-pulls workflowState/result from the container |
| GET | `/api/tickets/{id}/runs` | | `Run[]` | |
| GET | `/api/runs/{id}` | | `Run` | |
| GET | `/api/runs/{id}/events` | `?since=<seq>&limit=500` | `RunEvent[]` | |
| GET | `/api/runs/{id}/stream` | | SSE | replays from seq 0 then live; event `run_event` data=`RunEvent`; event `run_done` data=`Run` |
| GET | `/api/events` | | SSE | board feed: event `ticket` data=`Ticket`; event `run` data=`Run` |
| GET | `/healthz` | | `{status:"ok"}` | |

Errors: `{ error: string, code: string }` with 400 (invalid transition: code `invalid_transition`), 404, 409 (run already active: `run_active`).

## Endpoints (in-container `ticket` CLI, bearer token)

| Method | Path | Body | Returns | Effect |
|---|---|---|---|---|
| GET | `/api/tickets/{id}` | | `TicketDetail` | |
| POST | `/api/tickets/{id}/questions` | `{questions: string[]}` | `Question[]` 201 | ticket -> `needs_input` when the current run ends |
| POST | `/api/tickets/{id}/spec` | `{spec: string}` | `Ticket` | ticket -> `ready` when the current run ends (refine/answer runs); during work runs it just updates the spec |
| POST | `/api/tickets/{id}/comments` | `{text}` | `Comment` 201 | author `agent` |
| POST | `/api/tickets/{id}/progress` | `{phase: string, note?: string}` | 204 | free-form progress note shown on the card |

The run outcome is decided when the claude process exits: if the run recorded new questions -> `needs_input`; else if it recorded a spec (refine/answer) -> `ready`; else for `work` runs the ticket stays `in_progress` unless `workflowState.gate == "PASSED"` -> `review`; non-zero exit or missing result -> `failed`.

Auto-merge: when a run would land in `review` and the ticket has `autoMerge == true`, Dispatch instead queues a run of kind `ship` and keeps the ticket `in_progress`. A `ship` run (also started by `POST .../ship`) ends as: questions -> `needs_input`; a `ticket progress shipped` note during the run -> `done` (container deleted, no snapshot); any other clean exit (halted, rolled back) -> `review` for the human; error -> `failed`. Auto-merge never re-triggers itself after a ship run.

## Environment inside a ticket container

`DISPATCH_URL` (e.g. `http://10.10.10.1:9300`), `DISPATCH_TOKEN`, `TICKET_ID`, `DISPATCH_PROJECT`.
