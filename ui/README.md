# Dispatch UI

Kanban board for the Dispatch ticket system. React 19, Vite, TypeScript, Tailwind v4, zustand, `@dnd-kit`, `react-router` v7.

## Development

Requires Node 22 and pnpm.

```sh
pnpm install
pnpm dev          # http://localhost:5173
```

The dev server proxies `/api` and `/healthz` to the Dispatch API at `http://127.0.0.1:9300`.
Point it somewhere else with `VITE_API_TARGET`:

```sh
VITE_API_TARGET=http://10.10.10.1:9300 pnpm dev
```

Start the API from `server/Dispatch.Api`. For offline UI work run it in fake-Incus mode so no containers are
created (runs are simulated, container actions are no-ops):

```sh
DISPATCH_FAKE_INCUS=1 dotnet run --project ../server/Dispatch.Api
```

## Scripts

| Command | What |
|---|---|
| `pnpm dev` | Vite dev server with API proxy |
| `pnpm build` | `tsc -b` + production build to `dist/` (served by the API from `wwwroot`) |
| `pnpm test` | Vitest unit tests (`src/**/*.test.ts`) |
| `pnpm lint` | ESLint (flat config, typescript-eslint, react-hooks) |

## Layout

```
src/
  lib/          api.ts (typed fetch client), types.ts (mirror of docs/api.md), sse.ts (EventSource + backoff),
                events.ts (summarizeEvent: stream-json -> card models), utils.ts
  store/        board.ts (tickets, projects, filter, live feed / polling fallback, optimistic move),
                ticket.ts (TicketDetail cache kept fresh by the feed), toasts.ts
  components/
    board/      Board (dnd-kit), Column, TicketCard, ProjectFilter, NewTicketDialog
    ticket/     TicketDrawer (+ Header, Actions, Overview/Runs/Workflow/Comments tabs)
    events/     EventStream (run SSE with replay dedupe, follow toggle), EventCard
    ui/         Button, Badge, Dialog, Field, Markdown, CodeBox, Toasts
  pages/        BoardPage (`/`), TicketPage (`/tickets/:id`; panel on >=1024px, full page below)
```

## Behaviour notes

- Board updates come from `GET /api/events` (SSE). If the stream errors the store polls `GET /api/tickets`
  every 10s and reconnects with exponential backoff (1s .. 20s); on reconnect it reloads once to catch up.
- Dragging a card to another column calls `POST /api/tickets/{id}/move`; the card moves optimistically and
  reverts with an error toast if the API rejects it (`invalid_transition`, `run_active`).
- The run stream (`GET /api/runs/{id}/stream`) replays from seq 0 on every (re)connect; events are de-duplicated by `seq`.
- Keyboard: `Esc` closes the drawer, `c` opens the New ticket dialog, `Ctrl/Cmd+Enter` saves textareas.
- Theme: dark by default, light via `prefers-color-scheme: light`.
