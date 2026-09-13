# Dispatch

Local ticket board that runs Claude Code agents in per-ticket Incus containers.

- `server/` .NET 10: `Dispatch.Api` (REST + SSE + run queue + incus driver), `Dispatch.Claude` (claude stream-json wrapper)
- `ui/` React + Vite kanban
- `cli/ticket` bash CLI the agent uses inside a container to ask questions / submit specs
- `workflow-pack/` generic orchestrator repo template (features/, tasks/, /start-feature, /review-feature, /ship-feature)
- `projects/<name>/` project onboarding folder (see `docs/project-config.md`)
- `infra/` server setup, base image and project base container scripts

Contracts: `docs/api.md`, `docs/project-config.md`. Plan of record: see the initial commit message.
