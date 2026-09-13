---
name: watch-ci
description: Watch the fleet of PRs for a feature until all CI is green or a hard failure is identified.
---

Use the `pr-fleet-tracker` agent after the slice PRs exist.

- Keep this bounded: it watches CI, appends short log lines, and reports green vs red.
- Default to the cheapest available model. Escalate only if the watcher is blocked by ambiguity or repeated failure analysis.
- Do not widen the watcher into implementation work.
- The watcher does not talk to the board. When it reports `ALL_GREEN`, the orchestrator session posts `ticket progress ci-green`; on `HAS_FAILURES` the session reads the logs and fixes in scope.
- Hand off to `cross-cutting-reviewer` only after the fleet is green.

See `.claude/agents/pr-fleet-tracker.md` for the exact loop and output contract.
