# {{PROJECT}} orchestrator

Orchestrator repo for the `{{ORG}}` GitHub organisation, generated from Dispatch's workflow pack. One Claude Code session per ticket, running in its own Incus container, implements all slices of a feature across the `{{ORG}}` repos in-session, reviews its own work with sub-agents, fixes what they find, and, when the human says so, ships it to production. Parallelism comes from Dispatch running several ticket containers at once.

## How it works

1. A human writes a ticket on the Dispatch board and presses Refine. A session explores `{{WORKSPACE}}` read-only, asks questions through the `ticket` CLI if anything is ambiguous, and submits a spec in the shape of `docs/plan-template.md`.
2. The human approves the spec and presses Start. Dispatch writes `features/<ticket-id>-<slug>.md` and starts a session in a fresh container with the instruction to run `/start-feature <ticket-id>`.
3. **`/start-feature`** decomposes the spec via `feature-decomposer`, implements each slice in dependency order on `feat/<id>-<slug>` branches, opens the PRs, watches CI via `pr-fleet-tracker`, writes `tasks/<id>/result.json`. Then chains into:
4. **`/review-feature`** fans out `slice-reviewer` over every (slice x lens): correctness, security, deploy-safety, tests, conventions. Plus `cross-cutting-reviewer` over the fleet. Records every finding in `tasks/<id>/review.md`, fixes the BLOCKERs and MAJORs, re-greens CI, and repeats until nothing blocking remains (max 3 rounds). Writes a gate verdict to `tasks/<id>/state.json`. Dispatch moves the ticket to `review` when the gate is `PASSED`.
5. **`/ship-feature`** runs only when the human sends a resume message asking for it. `scripts/merge-fleet.sh` merges the fleet tier by tier and takes it to production.

The human reviews the fleet on GitHub and the board, not in a terminal.

## The gate is the only thing between an agent and production

Assume the org has **no branch protection**. `gh pr merge` will merge a PR with red CI, and one that is stale against main, and say nothing. Merging to `main` triggers `{{DEPLOY_WORKFLOW}}`, which deploys to prod.

So everything a protected branch would have enforced lives in `scripts/merge-fleet.sh` instead, and it fails closed. Before each merge it re-checks: the review gate passed, no `FREEZE`, no `hold` label, the PR is mergeable, **every check green by name** (plus any `REQUIRED_CHECKS`), the branch is **not behind main** (a green tick can come from a merge ref computed against an older main; nothing re-runs checks when the base moves), no **migration-number race** with another open feature (when `MIGRATIONS_DIR` is configured), and **no deploy already in flight** on that repo's main.

Then it merges one tier, **waits for the deploy, and probes production directly** (when `PROBE_HOSTS` is configured) before the next tier may merge at all. Merge order is not deploy order: a backend can take far longer to go live than a static client, and merging back-to-back would put clients live before the schema they query.

## Kill switches

- **`FREEZE`**: a file at the root of this repo. Checked against `origin/main` before *every single merge*, so pushing it from anywhere (including a phone) stops a ship that is already running.
- **`hold` label**: put it on any PR in a fleet and that PR will not merge. Also checked immediately before each merge.

## When production breaks

`scripts/rollback-fleet.sh <id>`. It reverts in reverse dependency order (clients first, backend last: a new client against an old backend is the outage; an old client against a new *additive* backend is not), waits for each revert to deploy, writes `tasks/<id>/INCIDENT.md`, sets `FREEZE`, and stops.

**No agent hotfixes production.** It reverts and tells the human through the board.

This is also why `deploy-safety` is a blocking review lens: backend changes must be additive and backwards-compatible in *both* directions, because that is what makes a revert a way home at all.

## Configuration

- `workflow.env`: org, workspace, deploy workflow, backend repo, optional `MIGRATIONS_DIR`, `SCHEMA_FILE`, `REQUIRED_CHECKS`, `PROBE_HOSTS`. Sourced by every script. See `workflow.env.example` for every key and what it enables.
- `repos.yaml`: repo name to role mapping (`role-templates/<role>-task.md`). Read by `feature-decomposer` and the scripts.

Both are written by Dispatch when this repo is generated. Change them in the Dispatch project folder and rebuild the base container, not here.

## Layout

```text
.
|- README.md                 - this file
|- CLAUDE.md                 - session instructions
|- workflow.env              - runtime configuration (org, workspace, deploy workflow, guards)
|- repos.yaml                - repo -> role
|- .claude/
|  |- agents/               - feature-decomposer, pr-fleet-tracker, cross-cutting-reviewer,
|  |                          slice-reviewer, finding-adjudicator
|  |- commands/             - /start-feature, /review-feature, /ship-feature, /feature-status
|  `- skills/               - decompose-feature, watch-ci
|- role-templates/          - per-role conventions
|- scripts/                 - lib.sh, fleet-status.sh, commit-feature-spec.sh,
|                             merge-fleet.sh, rollback-fleet.sh
|- docs/                    - plan-template.md, context-budget.md, review-gate.md, state-files.md
|- features/                - committed feature specs (<ticket-id>-<slug>.md)
`- tasks/                   - per-feature state (read by Dispatch)
```

## Cross-repo PR conventions

- Branch: `feat/<id>-<slug>`
- PR title prefix: `[feat-<id>]`
- Label on every PR: `feature:<id>`

## Running a feature by hand

The scripts and commands work outside Dispatch too: clone the project's repos under `{{WORKSPACE}}`, authenticate `gh`, commit a spec with `scripts/commit-feature-spec.sh <plan-path> <slug>`, and run `/start-feature <id>` in a Claude Code session in this directory. The `ticket` CLI will fail with a clear message when its environment is missing; that is expected outside a ticket container.
