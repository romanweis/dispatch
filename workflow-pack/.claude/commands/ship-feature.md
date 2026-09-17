---
description: Merge a reviewed feature fleet and take it to production (only when the human asked for it)
argument-hint: <feature-id>
---

Ship feature `$1` to production. This merges PRs, and merging to `main` deploys. It is the only irreversible thing this harness does.

## Before you run anything

0. Confirm Dispatch asked for this. The only valid trigger is a ship run from the Dispatch board: a prompt that says Dispatch is asking you to ship this ticket (the human either pressed Ship or enabled auto-merge on the ticket). If you are here because a previous command chained into it, or because the gate passed and it seemed like the next step, stop. Nothing chains into `/ship-feature`.
1. Read `tasks/$1/state.json`. If `gate` is not `PASSED`, stop and say so. A fleet ships through `/review-feature` or not at all.
2. If `frozen` is true, or a `FREEZE` file exists in this repo, stop and `ticket comment` why.
3. If the fleet includes a backend slice, `PROBE_HOSTS` is set in `workflow.env`, and `tasks/$1/probe.json` is missing, write it now (see `/review-feature`). The script will refuse without it, and it is right to.
4. `ticket progress shipping "<n> PRs, <t> tiers"`.

## Dry run first

```bash
scripts/merge-fleet.sh $1 --dry-run
```

This evaluates every guard and merges nothing. Read the output. If a guard fails, fix the cause. Do not work around the guard, and do not edit `workflow.env` or the script. The guards are the only thing standing where branch protection would be if this org had any.

## Ship

```bash
scripts/merge-fleet.sh $1
```

Run it in the foreground with a long timeout (60 minutes is fine; Dispatch raises `BASH_MAX_TIMEOUT_MS` in the ticket container so a wait that long is permitted) and wait for it. If a tool call is refused, stop and `ticket comment` the command and the refusal; merging by hand is never the answer. Never start it in the background: this session is headless and a background process dies when your turn ends, leaving the fleet half-merged. The script owns the whole sequence and you should not second-guess it mid-run. Per tier it re-checks the guards, merges, waits for `{{DEPLOY_WORKFLOW}}`, and (when `PROBE_HOSTS` is configured) **probes production directly** before the next tier is allowed to merge. That last part is the point: a backend can take far longer to go live than a static client, so merging the fleet back-to-back would put the clients live long before the schema they query.

Expect it to take a while. A backend + clients fleet can be 25-40 minutes, most of it waiting. Do not run it with a short timeout, and do not push anything to a fleet repo's `main` while it runs.

## If it stops

The script halts on any of: a red deploy, a **cancelled** main deploy (which means a deploy that was mutating a live host was killed partway and prod may be half-applied), or a failed liveness probe.

**Run `scripts/rollback-fleet.sh $1`.** It reverts in reverse dependency order (clients first, backend last), waits for each revert to deploy, writes `tasks/$1/INCIDENT.md`, sets `FREEZE`, and stops. Then `ticket progress rolled-back "<haltReason>"` and `ticket comment` with the contents of `INCIDENT.md`.

**Do not attempt a fix.** Not a quick one, not an obvious one. An agent hotfixing production unsupervised, having just broken it, makes every outage longer than it needed to be. Revert, write the incident, tell the human, stop.

One exception the script raises on its own: `PROBE_INCONCLUSIVE` means production is probably fine but the schema could not be proven live. Nothing has broken; the fleet just cannot safely continue. Do not roll back, do not merge on. `ticket progress halted "PROBE_INCONCLUSIVE"` and `ticket ask` how to proceed.

## When it finishes

Read the final state from GitHub, not from `state.json` (the session can die between a merge landing and the state commit). Then print, one line per slice: repo, merge commit, deploy conclusion, probe result. `ticket progress shipped "<repos>"`, and say plainly that the feature is live. That `shipped` note is what moves the ticket to Done on the board and deletes this container, so post it only when every slice is merged and its deploy is green.
