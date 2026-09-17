# Dispatch HTTP API contract

Base URL: `http://<server>:9300`. JSON everywhere, `camelCase` property names, timestamps ISO 8601 UTC.
No auth for the UI. The in-container `ticket` CLI sends `Authorization: Bearer <DISPATCH_TOKEN>`; the token is per ticket and only valid for that ticket's `/api/tickets/{id}/...` routes.

## Enums

```
TicketType:   feature | task
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
  type: TicketType;                     // task: skips refinement, body becomes the spec
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
| POST | `/api/tickets` | `{projectId, title, body, autoMerge?, type?, start?}` | `Ticket` 201 | status `backlog`; `type` defaults to `feature`. A `task` with `start` omitted or true is started immediately (see Tasks); if that fails the ticket stays `backlog` with a `system` comment |
| GET | `/api/tickets/{id}` | | `TicketDetail` | |
| PATCH | `/api/tickets/{id}` | `{title?, body?, spec?, autoMerge?}` | `Ticket` | spec editable only in `ready`/`needs_input`/`backlog`; `autoMerge` any time on a feature, 400 when turning it off on a task |
| DELETE | `/api/tickets/{id}` | | 204 | deletes container too |
| POST | `/api/tickets/{id}/refine` | | `Run` 202 | allowed from `backlog`, `needs_input` (when no unanswered questions), `failed` |
| POST | `/api/tickets/{id}/answer` | `{answers: [{questionId, answer}]}` | `Run` 202 | stores answers, then queues run kind `answer` (resume) |
| POST | `/api/tickets/{id}/start` | | `Run` 202 | from `ready`; needs `spec`; sets slug, writes feature file, runs work. Tasks also from `backlog`/`failed`. When the gate is already `PASSED` it queues a `ship` run instead of re-planning finished work |
| POST | `/api/tickets/{id}/ship` | | `Run` 202 | from `review` or `failed` (a ship run that died halfway) with `workflowState.gate == PASSED`; queues run kind `ship` (`/ship-feature`), ticket -> `in_progress` |
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

Workspace sync: every refine run, every work run (feature and task) and a fresh answer run without a session start with the project's sync prompt (`prompts/sync.md`, built-in default otherwise): fetch and fast-forward every repo in the workspace before reading or changing anything, and `ticket ask` instead of forcing when a repo cannot be fast-forwarded. Ticket containers are copies of a project base image whose clones may be old. Resume and ship runs continue an already-synced session and do not repeat it.

Tasks: a ticket with `type == "task"` skips refinement. `start` is allowed from `backlog`, `failed` or `ready`; when the ticket has no spec, Dispatch seeds the feature file with the title and body verbatim. The work run's prompt is the project's plan prompt (`prompts/plan.md`) followed by the work prompt, so one session still does plan -> implement -> review -> fix: the agent rewrites the feature file into a plan-template spec (`ticket spec`, `ticket progress planned`) without waiting for approval, then runs `/start-feature` and `/review-feature` as usual. Tasks always carry `autoMerge == true`: it is forced on at creation (whatever the request says) and a `PATCH` setting it to `false` is a 400, so a task ships itself the moment the review gate passes, with no human click. Convert the work to a feature ticket if you want to review before shipping. The agent may still `ticket ask` when something material is ambiguous.

## Environment inside a ticket container

`DISPATCH_URL` (e.g. `http://10.10.10.1:9300`), `DISPATCH_TOKEN`, `TICKET_ID`, `DISPATCH_PROJECT`, plus `BASH_DEFAULT_TIMEOUT_MS=600000` and `BASH_MAX_TIMEOUT_MS=5400000` so a ship run can wait for `merge-fleet.sh` (CI plus a production deploy per tier) in one foreground call. A project's `env:` block overrides any of them.
