---
description: Report the current state of an orchestrated feature
argument-hint: <feature-id>
---

Report on feature `$1` without taking action.

1. Read `tasks/$1/decomposition.md`, `tasks/$1/result.json`, `tasks/$1/state.json`, and `tasks/$1/log.md` if they exist.
2. Run `scripts/fleet-status.sh $1` to get the live PR, CI, and deploy state.
3. Summarise the planned slices, current PRs, CI state, review gate, and any blockers or notable log entries.
4. Do not dispatch, retry, or edit code. Do not post to the board. This command is read-only.
