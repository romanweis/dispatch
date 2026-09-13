#!/usr/bin/env bash
# rollback-fleet.sh <feature-id>
#
# Takes a feature back out of production after a failed deploy or a failed liveness probe.
#
# This is the ONLY thing that runs after a prod failure. No hotfix is attempted. An agent that
# has just broken production, unsupervised, writing novel code against a slow deploy loop, is
# the worst possible actor at that moment: every attempt is another unreviewed prod deploy.
# So: revert, verify, write the incident, stop, and page a human.
#
# Reverting the backend is only safe because its migrations are additive. The deploy-safety
# lens makes non-additive DDL a BLOCKER precisely so that this script has a way home. If a
# merged backend migration turns out to be destructive, this script refuses to revert it and
# pages instead (only when MIGRATIONS_DIR is configured; otherwise it cannot tell and reverts).

set -euo pipefail

WORKFLOW_ENV="$(dirname "$0")/../workflow.env"
[ -f "$WORKFLOW_ENV" ] || { echo "$(basename "$0"): missing $WORKFLOW_ENV. Copy workflow.env.example and fill it in (Dispatch writes it when rendering the pack)." >&2; exit 1; }
source "$WORKFLOW_ENV"
source "$(dirname "$0")/lib.sh"

FID="${1:?feature-id required}"
TASK_DIR="$ORCH/tasks/$FID"
STATE="$TASK_DIR/state.json"
RESULT="$TASK_DIR/result.json"
INCIDENT="$TASK_DIR/INCIDENT.md"

[ -f "$RESULT" ] || abort "no $RESULT"
[ -f "$STATE" ]  || abort "no $STATE"

step "Rolling back feature $FID"

# Freeze first. Whatever else happens, this feature must not merge another slice.
tmp="$(mktemp)"
jq '.phase = "HALTED" | .frozen = true' "$STATE" > "$tmp" && mv "$tmp" "$STATE"
printf 'Rollback of feature %s in progress. Remove this file to allow merges again.\n' "$FID" > "$ORCH/FREEZE"
orch_commit_push "chore($FID): FREEZE, rolling back" "tasks/$FID/state.json" FREEZE
pass "fleet frozen"

# Reverse dependency order: clients come out FIRST, the backend LAST. The window where new clients
# talk to an old backend is an outage; the window where old clients talk to a new additive backend is
# not. Undo in the order that never opens the bad window.
mapfile -t ROLLBACK < <(jq -r '.prs | sort_by(-(.mergeOrder // 1)) | .[] | "\(.repo)\t\(.url)\t\(.mergeOrder // 1)"' "$RESULT")

REVERTED=()

for s in "${ROLLBACK[@]}"; do
  repo="$(cut -f1 <<<"$s")"; url="$(cut -f2 <<<"$s")"; num="${url##*/}"
  dir="$(repo_dir "$repo")"

  pr="$(gh pr view "$num" --repo "$OWNER/$repo" --json state,mergeCommit)"
  [ "$(jq -r .state <<<"$pr")" = "MERGED" ] || { info "$repo#$num never merged. Nothing to undo"; continue; }
  sha="$(jq -r '.mergeCommit.oid' <<<"$pr")"

  echo
  info "$repo: reverting $sha"

  [ -d "$dir/.git" ] || abort "$repo: no checkout at $dir"
  git -C "$dir" fetch --quiet origin
  git -C "$dir" checkout -q main
  git -C "$dir" pull --ff-only -q origin main

  # Idempotent: git's own revert commits record `This reverts commit <sha>` in the body, so a
  # re-run after a crash finds its own earlier work instead of reverting the revert.
  if git -C "$dir" log --grep="This reverts commit $sha" --format=%H -1 origin/main | grep -q .; then
    pass "    already reverted"
    continue
  fi

  # A destructive migration cannot be undone by reverting the code that ran it. The column is gone.
  if is_backend "$repo" && [ -n "$MIGRATIONS_DIR" ]; then
    if git -C "$dir" show --name-only --format= "$sha" | grep -q "^$MIGRATIONS_DIR/"; then
      if git -C "$dir" show "$sha" -- "$MIGRATIONS_DIR/**" | grep -iqE '^\+.*(DROP TABLE|DROP COLUMN|ALTER COLUMN .* TYPE|RENAME (TO|COLUMN))'; then
        abort "$repo $sha contains destructive DDL. Reverting the code will NOT restore the data.
       This needs a human with a database backup. Not touching it.
       Prod state: the backend is deployed and the destructive migration has already run."
      fi
      warn "    migration present, additive. Reverting the code leaves the column in place (harmless)"
    fi
  fi

  # A squash merge lands as an ordinary single-parent commit, so a plain revert is the normal path.
  # `-m 1` is only needed if someone merged a PR with a merge commit instead.
  git -C "$dir" revert --no-edit "$sha" 2>/dev/null || git -C "$dir" revert --no-edit -m 1 "$sha" \
    || abort "$repo: reverting $sha conflicts with what is now on main. Resolve by hand, now. Prod is broken."
  git -C "$dir" push origin main
  rsha="$(git -C "$dir" rev-parse HEAD)"
  pass "    reverted as $rsha"
  REVERTED+=("$repo	$rsha	$sha")

  # Wait for the revert to actually be live before undoing the next slice down.
  waited=0
  while [ "$waited" -lt "$DEPLOY_TIMEOUT" ]; do
    run="$(gh run list --repo "$OWNER/$repo" --workflow "$DEPLOY_WORKFLOW" --branch main \
           --json headSha,status,conclusion -q "[.[] | select(.headSha == \"$rsha\")] | first" 2>/dev/null || echo null)"
    if [ "$run" != "null" ] && [ -n "$run" ] && [ "$(jq -r .status <<<"$run")" = "completed" ]; then
      c="$(jq -r .conclusion <<<"$run")"
      [ "$c" = "success" ] && { pass "    revert deployed"; break; }
      abort "the REVERT of $repo failed to deploy ($c). Production is in an unknown state.
       This is as bad as it gets from a script. Escalate now: $url"
    fi
    sleep 30; waited=$((waited + 30))
  done
done

step "Writing incident report"

{
  printf '# INCIDENT: feature %s\n\n' "$FID"
  printf 'Rolled back at %s.\n\n' "$(date -Is)"
  printf '## Why\n\n%s\n\n' "$(jq -r '.haltReason // "unknown"' "$STATE")"
  printf '## What was reverted\n\n'
  if [ "${#REVERTED[@]}" -eq 0 ]; then
    printf 'Nothing. No slice of this feature had merged.\n\n'
  else
    printf '| repo | reverted merge | revert commit |\n|---|---|---|\n'
    for r in "${REVERTED[@]}"; do
      printf '| %s | `%s` | `%s` |\n' "$(cut -f1 <<<"$r")" "$(cut -f3 <<<"$r")" "$(cut -f2 <<<"$r")"
    done
    printf '\n'
  fi
  printf '## State of production\n\n'
  printf 'Every reverted slice re-deployed green. Verify by hand before trusting this line.\n\n'
  printf '## What happens next\n\n'
  printf -- '- The fleet is frozen (`FREEZE` in the orchestrator repo). No agent will merge anything until it is removed.\n'
  printf -- '- No fix was attempted. The branches are gone (squash-merged and deleted); the work is recoverable from the reverted commits above.\n'
  printf -- '- A human decides what to do. To ship again, fix forward on a new feature id.\n'
} > "$INCIDENT"

tmp="$(mktemp)"
jq '.phase = "ROLLED_BACK"' "$STATE" > "$tmp" && mv "$tmp" "$STATE"
orch_commit_push "chore($FID): INCIDENT, rolled back" "tasks/$FID/"

step "Feature $FID rolled back. FREEZE is set. Stopping."
info "Incident report: $INCIDENT"
info "No fix will be attempted. This needs a human."
echo
exit 1
