# Workflow pack (template notes)

This directory is the generic, parameterized template for a project's orchestrator repo. Dispatch renders it once per project when the project's orchestrator repo does not exist on GitHub yet (or is empty), pushes the result, and clones it into every base container at `<workspace>/<orchestrator.repo>`.

This file documents the template itself. `README.md` next to it is the README of the *generated* repo. Both are copied verbatim, so this file also ends up in the generated repo, where it explains where the repo came from.

## Placeholders

Text files in the pack contain four placeholders that `infra/build-project-base.sh` replaces with `sed` after copying the tree:

| Placeholder | Source in `project.yaml` | Example |
|---|---|---|
| `{{ORG}}` | `org` | `bognerchess` |
| `{{WORKSPACE}}` | `workspace` | `/home/agent/bognerchess` |
| `{{PROJECT}}` | `name` | `bognerchess` |
| `{{DEPLOY_WORKFLOW}}` | `orchestrator.deployWorkflow` | `deploy.yml` |

Only markdown and other text files use placeholders. Shell scripts do not: they read the same values at runtime from `workflow.env`, so a script never has to be re-rendered and can be copied between orchestrator repos unchanged.

## Files written at render time

- `workflow.env`: shell assignments, sourced by every `scripts/*.sh` via `source "$(dirname "$0")/../workflow.env"` (and by `scripts/lib.sh`, which applies defaults). The full key list with comments is in `workflow.env.example`. Minimum: `ORG`, `WORKSPACE`, `PROJECT`, `DEPLOY_WORKFLOW`. Optional guards are enabled by setting `MIGRATIONS_DIR`, `SCHEMA_FILE`, `REQUIRED_CHECKS` (from `orchestrator.requiredChecks`), `PROBE_HOSTS`, `EXTRA_MAIN_WORKFLOWS`.
- `repos.yaml`: the repo list with roles, straight from `project.yaml`'s `repos`:

  ```yaml
  repos:
    - name: backend
      role: backend
  ```

  `feature-decomposer` reads it to assign a role to each slice; `merge-fleet.sh` warns about slices whose repo is not listed. Roles must have a matching `role-templates/<role>-task.md`. Shipped roles: `backend`, `frontend-relay`, `frontend-apollo`, `astro`, `shared-types`, `python`, `infra`.

Both files are committed to the generated repo. They are configuration for that project, not secrets.

## What is deliberately not in the pack

- `.claude/settings.json` is `{"hooks":{}}`. Dispatch passes `--permission-mode` on the command line, so no permission defaults live here.
- No release-notes tooling and no chat integrations. The human channel is the `ticket` CLI (`/usr/local/bin/ticket`, pushed by Dispatch), documented in `CLAUDE.md` under "Talking to the human".
- No hardcoded hosts, org names, container names, or project-specific check names. Everything of that kind is in `workflow.env`.

## Contract with Dispatch

Dispatch reads two files from `tasks/<ticket-id>/` in the generated repo and exposes them on the board as `workflowState` and `result`. Their shapes are frozen and documented in `docs/state-files.md`. In particular, Dispatch moves a ticket to `review` when `state.json.gate == "PASSED"`.

## Adding a role

1. Add `role-templates/<role>-task.md` (scope, rules, stop conditions; keep it to repo-specific deltas, the shared rules live in `CLAUDE.md`).
2. Add the role name to the list in `.claude/agents/feature-decomposer.md`.
3. Use it in a project's `project.yaml`.
