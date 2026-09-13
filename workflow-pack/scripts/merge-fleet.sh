#!/usr/bin/env bash
# merge-fleet.sh <feature-id> [--dry-run]
#
# Merges every PR of a feature fleet, tier by tier, and takes the feature to production.
#
# Assume the org has NO branch protection (GitHub Free + private repos: the branch protection
# API returns 403). `gh pr merge` will happily merge a PR with red CI, and a PR that is stale
# against main. Merging to main triggers $DEPLOY_WORKFLOW, which deploys to production.
#
# Everything a protected branch would have enforced is enforced here instead. Every guard fails
# closed: if a check cannot be evaluated, the merge does not happen.
#
# Merge order is NOT deploy order. A backend typically takes far longer to go live than a static
# client. Merging a fleet back-to-back would put the clients live long before the backend schema
# they query. So a tier is not merely merged: it is merged, deployed, and (when PROBE_HOSTS is
# configured) PROBED LIVE before the next tier is allowed to merge at all.
#
# Configuration comes from ../workflow.env (see workflow.env.example): ORG, WORKSPACE,
# DEPLOY_WORKFLOW, BACKEND_REPO, SCHEMA_FILE, MIGRATIONS_DIR, REQUIRED_CHECKS, PROBE_HOSTS,
# EXTRA_MAIN_WORKFLOWS and the timeouts.

set -euo pipefail

WORKFLOW_ENV="$(dirname "$0")/../workflow.env"
[ -f "$WORKFLOW_ENV" ] || { echo "$(basename "$0"): missing $WORKFLOW_ENV. Copy workflow.env.example and fill it in (Dispatch writes it when rendering the pack)." >&2; exit 1; }
source "$WORKFLOW_ENV"
source "$(dirname "$0")/lib.sh"

FID="${1:?feature-id required}"
DRY_RUN=0
[ "${2:-}" = "--dry-run" ] && DRY_RUN=1

TASK_DIR="$ORCH/tasks/$FID"
STATE="$TASK_DIR/state.json"
RESULT="$TASK_DIR/result.json"
PROBE="$TASK_DIR/probe.json"
MAX_FRESHEN=2

# A guard failure in dry-run is reported and the run continues, so one pass shows every verdict.
# Outside dry-run a guard failure is terminal.
DRY_FAILURES=0
guard_fail() {
  if [ "$DRY_RUN" = 1 ]; then
    printf '  \033[31mFAIL\033[0m  %s\n' "$*"
    DRY_FAILURES=$((DRY_FAILURES + 1))
    return 0
  fi
  abort "$*"
}

# ---------------------------------------------------------------------------------------------
# Pre-flight. Evaluated once, before anything is merged.
# ---------------------------------------------------------------------------------------------

step "Pre-flight: feature $FID$([ "$DRY_RUN" = 1 ] && echo '  (dry run: nothing will be merged)')"

[ -f "$RESULT" ] || abort "no $RESULT. Run /start-feature $FID first."
[ -f "$STATE" ]  || abort "no $STATE. Run /review-feature $FID first."

# G0: the review gate must have passed. This is the only thing standing in for human review.
GATE="$(jq -r '.gate // "MISSING"' "$STATE")"
[ "$GATE" = "PASSED" ] || abort "gate is '$GATE', not PASSED. A fleet only ships through /review-feature."
[ "$(jq -r '.frozen // false' "$STATE")" = "false" ] || abort "state.json says frozen=true. A human must clear it."
pass "G0  review gate PASSED"

# G1: FREEZE kill switch. Checked against the remote, so a human can stop a running ship by pushing
# a FREEZE file from anywhere, including a phone.
git -C "$ORCH" fetch --quiet origin main
if git -C "$ORCH" cat-file -e origin/main:FREEZE 2>/dev/null || [ -f "$ORCH/FREEZE" ]; then
  abort "FREEZE is set. Nothing merges. Remove FREEZE and push to clear it."
fi
pass "G1  no FREEZE"

# G2: the fleet list. result.json is authoritative.
#
# `gh search prs --label` is an eventually-consistent index and can return FEWER PRs than exist.
# Trusting it could merge the backend and then never merge the client that calls the new field,
# which is exactly the outage this script exists to prevent. So: result.json is the fleet, the
# search is only a cross-check, and a mismatch is reported.
mapfile -t SLICES < <(jq -r '.prs[] | "\(.repo)\t\(.url)\t\(.mergeOrder // 1)"' "$RESULT")
[ "${#SLICES[@]}" -gt 0 ] || abort "result.json lists no PRs"

for s in "${SLICES[@]}"; do
  repo="$(cut -f1 <<<"$s")"
  [ -n "$(role_for "$repo")" ] || warn "G2  '$repo' is not listed in repos.yaml; proceeding, but check the decomposition"
done

SEARCH_COUNT="$(gh search prs --label "feature:$FID" --owner "$OWNER" --state open --json url -q 'length' 2>/dev/null || echo 0)"
OPEN_COUNT=0
for s in "${SLICES[@]}"; do
  repo="$(cut -f1 <<<"$s")"; url="$(cut -f2 <<<"$s")"; num="${url##*/}"
  st="$(gh pr view "$num" --repo "$OWNER/$repo" --json state -q .state)"
  [ "$st" = "OPEN" ] && OPEN_COUNT=$((OPEN_COUNT + 1))
done
if [ "$SEARCH_COUNT" -ne "$OPEN_COUNT" ]; then
  warn "G2  search index says $SEARCH_COUNT open PRs, result.json says $OPEN_COUNT."
  warn "    The index lags; result.json wins. If a PR is genuinely missing from result.json, stop and fix it."
fi
pass "G2  fleet = ${#SLICES[@]} PRs from result.json ($OPEN_COUNT still open)"

# G3: a backend slice cannot be probed without knowing which schema symbols to assert are live.
HAS_BACKEND=0
for s in "${SLICES[@]}"; do is_backend "$(cut -f1 <<<"$s")" && HAS_BACKEND=1; done
if [ "$HAS_BACKEND" = 1 ] && [ -n "$PROBE_HOSTS" ] && [ ! -f "$PROBE" ]; then
  abort "$BACKEND_REPO is in the fleet but $PROBE is missing.
       Without it the script cannot verify the new schema is actually live before the clients
       deploy against it, which is the one failure that takes prod down. /review-feature writes it."
fi
[ "$HAS_BACKEND" = 1 ] && [ -f "$PROBE" ] && pass "G3  probe.json present"

# G4: every repo must be probeable, or probing must be off altogether.
if [ -z "$PROBE_HOSTS" ]; then
  warn "G4  PROBE_HOSTS is empty: production will NOT be probed after each tier."
  warn "    The deploy workflow's conclusion is the only evidence a tier is live. Set PROBE_HOSTS in workflow.env."
else
  for s in "${SLICES[@]}"; do
    repo="$(cut -f1 <<<"$s")"
    [ -n "$(host_for "$repo")" ] || abort "no public host known for '$repo'. Add it to PROBE_HOSTS in workflow.env; a slice that cannot be probed cannot be shipped."
  done
  pass "G4  every repo has a known public host"
fi

# ---------------------------------------------------------------------------------------------
# Per-slice guards. Re-evaluated immediately before each individual merge, never cached: main moves
# under us, another ticket container may be mid-deploy, and a human may have put a hold label on.
# ---------------------------------------------------------------------------------------------

# All check runs on the PR head must be SUCCESS. Enumerated by name, never a rollup: no required
# checks exist server-side, so this function IS the required-checks list.
guard_checks() {
  local repo="$1" num="$2" json failed name
  json="$(gh pr checks "$num" --repo "$OWNER/$repo" --json name,state 2>/dev/null || echo '[]')"
  [ "$(jq 'length' <<<"$json")" -gt 0 ] || { guard_fail "$repo#$num has no checks at all"; return; }

  failed="$(jq -r '.[] | select(.state != "SUCCESS" and .state != "SKIPPED" and .state != "NEUTRAL") | "\(.name)=\(.state)"' <<<"$json" | paste -sd, -)"
  if [ -n "$failed" ]; then
    guard_fail "$repo#$num checks not green: $failed"
    return
  fi

  # Extra checks that must be present AND green by name (project.yaml orchestrator.requiredChecks).
  if [ -n "$REQUIRED_CHECKS" ]; then
    IFS=',' read -ra req <<<"$REQUIRED_CHECKS"
    for name in "${req[@]}"; do
      name="${name## }"; name="${name%% }"
      [ -n "$name" ] || continue
      if ! jq -e --arg n "$name" '[.[] | select(.name == $n) | select(.state == "SUCCESS")] | length > 0' <<<"$json" >/dev/null; then
        guard_fail "$repo#$num has no green check named '$name' (REQUIRED_CHECKS)"
        return
      fi
    done
    pass "    required checks green ($REQUIRED_CHECKS)"
  fi

  pass "    all checks green ($(jq 'length' <<<"$json") checks)"
}

# GitHub returns mergeable=UNKNOWN while it computes mergeability. A naive read either aborts
# spuriously or reads "not CONFLICTING" as "fine". Poll it out.
guard_mergeable() {
  local repo="$1" num="$2" m i
  for i in $(seq 1 15); do
    m="$(gh pr view "$num" --repo "$OWNER/$repo" --json mergeable -q .mergeable)"
    [ "$m" != "UNKNOWN" ] && break
    sleep 2
  done
  case "$m" in
    MERGEABLE)   pass "    mergeable" ;;
    CONFLICTING) guard_fail "$repo#$num conflicts with main" ;;
    *)           guard_fail "$repo#$num mergeability still $m after 30s (fail closed)" ;;
  esac
}

wait_for_ci() {
  local repo="$1" num="$2" waited=0 states
  info "    waiting for CI on $repo#$num"
  while [ "$waited" -lt "$CI_TIMEOUT" ]; do
    states="$(gh pr checks "$num" --repo "$OWNER/$repo" --json state -q '[.[].state] | unique | join(",")' 2>/dev/null || echo "")"
    case "$states" in
      *PENDING*|*QUEUED*|*IN_PROGRESS*|"") sleep 30; waited=$((waited + 30));;
      *) return 0 ;;
    esac
  done
  abort "$repo#$num CI still running after ${CI_TIMEOUT}s"
}

# A green tick can come from a merge ref computed against an OLD main. Nothing re-runs a PR's checks
# when its base moves. So "green" does not mean "green against the main we are about to merge into".
# behind_by is the exact answer.
guard_fresh() {
  local repo="$1" num="$2" head behind attempt=0
  while :; do
    head="$(gh pr view "$num" --repo "$OWNER/$repo" --json headRefOid -q .headRefOid)"
    behind="$(gh api "repos/$OWNER/$repo/compare/main...$head" -q '.behind_by')"
    if [ "$behind" -eq 0 ]; then
      pass "    up to date with main"
      return
    fi
    attempt=$((attempt + 1))
    if [ "$attempt" -gt "$MAX_FRESHEN" ]; then
      guard_fail "$repo#$num is $behind commits behind main and did not settle after $MAX_FRESHEN freshen attempts"
      return
    fi
    warn "    $behind commits behind main. Merging main in (attempt $attempt/$MAX_FRESHEN)"
    if [ "$DRY_RUN" = 1 ]; then info "dry-run: would merge origin/main into the branch and re-run CI"; return; fi

    local branch dir
    branch="$(gh pr view "$num" --repo "$OWNER/$repo" --json headRefName -q .headRefName)"
    dir="$(repo_dir "$repo")"
    [ -d "$dir/.git" ] || abort "$repo: no checkout at $dir"
    git -C "$dir" fetch origin
    git -C "$dir" checkout "$branch"
    git -C "$dir" merge --no-edit origin/main || abort "$repo: merging main into $branch conflicts. Resolve by hand."
    git -C "$dir" push origin "$branch"   # never force-push
    wait_for_ci "$repo" "$num"
  done
}

# Optional. Only for $BACKEND_REPO and only when MIGRATIONS_DIR (and/or SCHEMA_FILE) is set.
#
# Sequentially numbered migrations (0129_, 0130_, 0131_...) race: two features developed in
# parallel on two containers will both pick the next free number, both pass CI independently, and
# git will merge them cleanly because the filenames differ. Prod then runs them in an order nobody
# chose. Nothing else in this pipeline can see that, so it is checked here. An agent scoped to one
# feature has no authority to arbitrate between two, so it stops.
guard_migrations() {
  local repo="$1" num="$2" dup others other pattern
  is_backend "$repo" || return 0
  if [ -z "$MIGRATIONS_DIR" ] && [ -z "$SCHEMA_FILE" ]; then
    info "    migration/schema race guard disabled (MIGRATIONS_DIR and SCHEMA_FILE unset)"
    return 0
  fi

  if [ -n "$MIGRATIONS_DIR" ]; then
    # `|| true` is load-bearing: with pipefail, a PR that touches no migration files makes the
    # greps exit non-zero and the empty-match pipeline would kill the whole ship via set -e before
    # this guard ever evaluated. The guard's verdict is unchanged: any real duplicate still lands
    # in $dup and fails below.
    dup="$(gh pr diff "$num" --repo "$OWNER/$repo" --name-only \
          | { grep -oE "^$MIGRATIONS_DIR/[0-9]{4}" || true; } \
          | { grep -oE '[0-9]{4}$' || true; } | sort | uniq -d | paste -sd, -)"
    [ -z "$dup" ] || { guard_fail "$repo#$num introduces duplicate migration prefixes: $dup"; return; }
  fi

  pattern=""
  [ -n "$MIGRATIONS_DIR" ] && pattern="^$MIGRATIONS_DIR/"
  if [ -n "$SCHEMA_FILE" ]; then
    [ -n "$pattern" ] && pattern="$pattern|"
    pattern="${pattern}^$SCHEMA_FILE\$"
  fi

  others="$(gh pr list --repo "$OWNER/$repo" --state open --json number,labels,title \
            -q ".[] | select(.number != $num) | select(.labels[].name | startswith(\"feature:\")) | .number" 2>/dev/null || true)"
  for other in $others; do
    if gh pr diff "$other" --repo "$OWNER/$repo" --name-only | grep -qE "$pattern"; then
      guard_fail "$repo#$other (another open feature) also touches migrations or the schema file.
       Two features racing for the same migration number cannot be resolved by a feature-scoped
       agent. Merge one, let it land, then re-run this."
      return
    fi
  done
  pass "    no migration/schema race with another open feature"
}

# Never merge into a repo whose main deploy is already running.
#
# If the deploy workflow uses `cancel-in-progress: true` and every push to main lands in the SAME
# concurrency group, a second merge CANCELS the first deploy, possibly mid `docker compose up -d`,
# leaving prod with some containers new and some old. This guard is what makes several ticket
# containers shipping in parallel survivable.
guard_deploy_idle() {
  local repo="$1" waited=0 busy wf
  local -a wfs=("$DEPLOY_WORKFLOW")
  if is_backend "$repo" && [ -n "$EXTRA_MAIN_WORKFLOWS" ]; then
    IFS=',' read -ra extra <<<"$EXTRA_MAIN_WORKFLOWS"
    wfs+=("${extra[@]}")
  fi
  for wf in "${wfs[@]}"; do
    wf="${wf## }"; wf="${wf%% }"
    [ -n "$wf" ] || continue
    waited=0
    while [ "$waited" -lt "$IDLE_TIMEOUT" ]; do
      busy="$(gh run list --repo "$OWNER/$repo" --workflow "$wf" --branch main \
              --json status -q '[.[] | select(.status == "in_progress" or .status == "queued")] | length' 2>/dev/null || echo 0)"
      [ "$busy" -eq 0 ] && break
      warn "    $repo: $wf is running on main. Waiting (${waited}s)"
      sleep 30; waited=$((waited + 30))
    done
    [ "$waited" -lt "$IDLE_TIMEOUT" ] || { guard_fail "$repo: $wf still running on main after ${IDLE_TIMEOUT}s"; return; }
  done
  pass "    no deploy in flight on main"
}

# ---------------------------------------------------------------------------------------------
# Deploy + probe
# ---------------------------------------------------------------------------------------------

wait_for_deploy() {
  local repo="$1" sha="$2" waited=0 run status conclusion
  info "    waiting for $DEPLOY_WORKFLOW of $sha"
  while [ "$waited" -lt "$DEPLOY_TIMEOUT" ]; do
    run="$(gh run list --repo "$OWNER/$repo" --workflow "$DEPLOY_WORKFLOW" --branch main \
           --json databaseId,headSha,status,conclusion \
           -q "[.[] | select(.headSha == \"$sha\")] | first" 2>/dev/null || echo "null")"
    if [ "$run" != "null" ] && [ -n "$run" ]; then
      status="$(jq -r '.status' <<<"$run")"
      if [ "$status" = "completed" ]; then
        conclusion="$(jq -r '.conclusion' <<<"$run")"
        # `cancelled` is NOT success here. On a PR a cancelled check just means a newer push
        # superseded it. On main it means a deploy that was mutating a live host was killed
        # partway through, and prod may be half-applied. Treat it as a failure.
        [ "$conclusion" = "success" ] && { pass "    deploy succeeded"; return 0; }
        echo "DEPLOY_$conclusion"; return 1
      fi
    fi
    sleep 30; waited=$((waited + 30))
  done
  echo "DEPLOY_TIMEOUT"; return 1
}

# Do not trust the deploy workflow's conclusion. A backend deploy typically ends at
# `docker compose up -d`; a container that starts and then crashes on a bad migration still leaves
# a GREEN deploy run behind it. Ask production directly.
#
# Return codes: 0 live, 1 PROBE_FAILED, 2 PROBE_INCONCLUSIVE.
probe() {
  local repo="$1" sha="${2:-}" host code body
  host="$(host_for "$repo")"

  if [ -z "$host" ]; then
    warn "    probe skipped: no host configured for $repo"
    return 0
  fi

  if [ "${host#internal:}" != "$host" ]; then
    # No public host to curl. Liveness is proven by a named step of the deploy run (for example an
    # in-container smoke test) succeeding on the merged sha, asserted explicitly rather than inferred
    # from the run's overall colour.
    local step_name="${host#internal:}" run_id step_conclusion
    run_id="$(gh run list --repo "$OWNER/$repo" --workflow "$DEPLOY_WORKFLOW" --branch main \
              --json databaseId,headSha \
              -q "[.[] | select(.headSha == \"$sha\")] | first | .databaseId" 2>/dev/null || echo '')"
    if [ -z "$run_id" ] || [ "$run_id" = "null" ]; then
      echo "PROBE_INCONCLUSIVE no deploy run found for $repo@$sha"; return 2
    fi
    step_conclusion="$(gh api "repos/$OWNER/$repo/actions/runs/$run_id/jobs" \
        -q "[.jobs[].steps[] | select(.name == \"$step_name\")] | first | .conclusion" 2>/dev/null || echo '')"
    if [ -z "$step_conclusion" ] || [ "$step_conclusion" = "null" ]; then
      echo "PROBE_INCONCLUSIVE deploy run $run_id has no '$step_name' step conclusion"; return 2
    fi
    [ "$step_conclusion" = "success" ] \
      || { echo "PROBE_FAILED $repo deploy run $run_id step '$step_name': $step_conclusion"; return 1; }
    pass "    live: step '$step_name' succeeded in deploy run $run_id"
    return 0
  fi

  if ! is_backend "$repo"; then
    code="$(curl -s -o /dev/null -w '%{http_code}' --max-time 20 "https://$host/" || echo 000)"
    [ "$code" = "200" ] || { echo "PROBE_FAILED https://$host/ -> $code"; return 1; }
    pass "    live: https://$host/ 200"
    return 0
  fi

  # The backend may serve several vhosts and the default one may reject anonymous POSTs, so the
  # probe host must be one that answers unauthenticated GraphQL. probe.json may name it (the host
  # the feature's client actually queries); default is the PROBE_HOSTS entry.
  local phost
  phost="$(jq -r '.host // empty' "$PROBE" 2>/dev/null || echo '')"
  [ -n "$phost" ] && host="$phost"

  body="$(curl -s --max-time 20 -X POST "https://$host/graphql" \
          -H 'content-type: application/json' -d '{"query":"{__typename}"}' || echo '')"
  jq -e '.data.__typename' >/dev/null 2>&1 <<<"$body" \
    || { echo "PROBE_FAILED https://$host/graphql did not answer a trivial query"; return 1; }
  pass "    live: https://$host/graphql answers"

  # Production usually disables introspection, so type/field liveness is proven through the
  # validator instead: a document selecting the new fields either passes validation (any auth or
  # runtime error is fine, it proves the fields exist) or is rejected with an unknown-type or
  # unknown-field error, which is exactly the outage the client tier must be held back from.
  local vn vq i
  vn="$(jq '.validationQueries | length' "$PROBE" 2>/dev/null || echo 0)"
  if [ "$vn" -gt 0 ] 2>/dev/null; then
    for i in $(seq 0 $((vn - 1))); do
      vq="$(jq -c "{query: .validationQueries[$i]}" "$PROBE")"
      body="$(curl -s --max-time 20 -X POST "https://$host/graphql" \
              -H 'content-type: application/json' -d "$vq" || echo '')"
      jq -e 'has("data") or has("errors")' >/dev/null 2>&1 <<<"$body" \
        || { echo "PROBE_INCONCLUSIVE validation query $i got no GraphQL response from https://$host/graphql"; return 2; }
      if jq -e '[.errors // [] | .[] | .message | select(test("does not exist|Unknown type|is not defined"))] | length > 0' >/dev/null 2>&1 <<<"$body"; then
        echo "PROBE_FAILED new schema not live on https://$host/graphql: $(jq -c '[.errors[].message]' <<<"$body")"; return 1
      fi
      pass "    live: validation query $i accepted (new schema fields exist)"
    done
    return 0
  fi

  # Introspection-based assertion. Everything above only proves the server is up; this proves the
  # NEW SCHEMA is up. Until it passes, the client tier must not deploy.
  local n type field
  n="$(jq '.assertions | length' "$PROBE" 2>/dev/null || echo 0)"
  for i in $(seq 0 $((n - 1))); do
    type="$(jq -r ".assertions[$i].type" "$PROBE")"
    field="$(jq -r ".assertions[$i].field" "$PROBE")"
    body="$(curl -s --max-time 20 -X POST "https://$host/graphql" -H 'content-type: application/json' \
            -d "{\"query\":\"{__type(name:\\\"$type\\\"){fields{name}}}\"}" || echo '')"

    if jq -e '.data.__type == null' >/dev/null 2>&1 <<<"$body"; then
      # Introspection is disabled, or the type does not exist. We cannot tell which, and guessing
      # either way is wrong: prod is probably fine, but we cannot prove the schema is live, so we
      # must not deploy clients against it. Prefer validationQueries in probe.json.
      echo "PROBE_INCONCLUSIVE cannot introspect $type on https://$host/graphql (introspection disabled?)"
      return 2
    fi
    jq -e --arg f "$field" '.data.__type.fields | map(.name) | index($f)' >/dev/null 2>&1 <<<"$body" \
      || { echo "PROBE_FAILED $type.$field is not live on https://$host/graphql"; return 1; }
    pass "    live: $type.$field"
  done
  return 0
}

# ---------------------------------------------------------------------------------------------
# Merge, tier by tier
# ---------------------------------------------------------------------------------------------

set_state() {
  [ "$DRY_RUN" = 1 ] && return 0
  local tmp; tmp="$(mktemp)"
  jq "$1" "$STATE" > "$tmp" && mv "$tmp" "$STATE"
  orch_commit_push "chore($FID): $2" "tasks/$FID/state.json"
}

mapfile -t TIERS < <(jq -r '[.prs[].mergeOrder // 1] | unique | .[]' "$RESULT")

set_state '.phase = "MERGING"' "ship $FID: merging"

for tier in "${TIERS[@]}"; do
  step "Tier $tier"

  # Crash-safety invariant, and it is not a nicety.
  #
  # Dying after the backend merged but before the clients merged leaves prod with a NEW, ADDITIVE
  # backend and OLD clients. That is safe and can sit there indefinitely. The reverse (clients live
  # against a backend that does not serve their fields) is an outage. So a later tier never merges
  # while an earlier one is unmerged, no matter who asks.
  for s in "${SLICES[@]}"; do
    o="$(cut -f3 <<<"$s")"; r="$(cut -f1 <<<"$s")"; u="$(cut -f2 <<<"$s")"
    if [ "$o" -lt "$tier" ]; then
      st="$(gh pr view "${u##*/}" --repo "$OWNER/$r" --json state -q .state)"
      if [ "$st" != "MERGED" ]; then
        if [ "$DRY_RUN" = 1 ]; then
          # Nothing merges in a dry run, so an earlier tier being OPEN here is expected, not a guard failure.
          info "dry-run: tier $o slice $r is '$st'; a real run merges and probes it before this tier"
        else
          guard_fail "tier $tier cannot merge: tier $o slice $r is '$st', not MERGED"
        fi
      fi
    fi
  done

  for s in "${SLICES[@]}"; do
    [ "$(cut -f3 <<<"$s")" = "$tier" ] || continue
    repo="$(cut -f1 <<<"$s")"; url="$(cut -f2 <<<"$s")"; num="${url##*/}"

    echo
    info "$repo#$num: $url"

    # GitHub is the source of truth for anything GitHub knows. Never read merge state from state.json:
    # the session can die between `gh pr merge` returning and the state commit landing.
    pr="$(gh pr view "$num" --repo "$OWNER/$repo" --json state,isDraft,labels,mergeCommit)"
    st="$(jq -r .state <<<"$pr")"
    if [ "$st" = "MERGED" ]; then
      pass "    already merged. Resuming"
      sha="$(jq -r '.mergeCommit.oid' <<<"$pr")"
    else
      [ "$st" = "OPEN" ] || guard_fail "$repo#$num is $st, not OPEN"
      [ "$(jq -r .isDraft <<<"$pr")" = "false" ] || guard_fail "$repo#$num is a draft"

      # hold label: a human's emergency brake on one PR, checked immediately before its merge so it
      # can stop a ship that is already in flight.
      jq -e '.labels | map(.name) | index("hold")' >/dev/null <<<"$pr" \
        && guard_fail "$repo#$num carries the 'hold' label"

      # Re-check FREEZE before every single merge, not just at pre-flight.
      git -C "$ORCH" fetch --quiet origin main
      if git -C "$ORCH" cat-file -e origin/main:FREEZE 2>/dev/null || [ -f "$ORCH/FREEZE" ]; then
        guard_fail "FREEZE appeared mid-run. Stopping before $repo#$num."
      fi

      guard_mergeable   "$repo" "$num"
      guard_checks      "$repo" "$num"
      guard_fresh       "$repo" "$num"
      guard_migrations  "$repo" "$num"
      guard_deploy_idle "$repo"

      if [ "$DRY_RUN" = 1 ]; then
        info "dry-run: would merge $repo#$num (squash), wait for its deploy, then probe"
        continue
      fi

      # Breadcrumb before the side effect, so a crash between here and the merge is diagnosable.
      set_state "(.slices[] | select(.repo == \"$repo\")).mergeAttemptedAt = \"$(date -Is)\"" "ship $FID: merging $repo#$num"
      gh pr merge "$num" --repo "$OWNER/$repo" --squash --delete-branch
      sha="$(gh pr view "$num" --repo "$OWNER/$repo" --json mergeCommit -q .mergeCommit.oid)"
      pass "    merged as $sha"
    fi

    [ "$DRY_RUN" = 1 ] && continue
    set_state "(.slices[] | select(.repo == \"$repo\")).mergedSha = \"$sha\"" "ship $FID: $repo merged $sha"

    if ! err="$(wait_for_deploy "$repo" "$sha")"; then
      set_state ".phase = \"HALTED\" | .haltReason = \"$err on $repo\"" "ship $FID: HALTED, $err on $repo"
      abort "$repo deploy of $sha: $err
       Production may be half-applied. Rolling back.
       -> scripts/rollback-fleet.sh $FID"
    fi

    set +e; perr="$(probe "$repo" "$sha")"; prc=$?; set -e
    if [ "$prc" -eq 2 ]; then
      set_state ".phase = \"HALTED\" | .haltReason = \"$perr\"" "ship $FID: HALTED, probe inconclusive on $repo"
      abort "$perr
       Prod is probably fine, but the new schema cannot be PROVEN live, and the client tier selects
       those fields. Not merging further. This needs a human."
    elif [ "$prc" -ne 0 ]; then
      set_state ".phase = \"HALTED\" | .haltReason = \"$perr\"" "ship $FID: HALTED, probe failed on $repo"
      abort "$perr
       The deploy went green but production is not serving what it should.
       -> scripts/rollback-fleet.sh $FID"
    fi
  done
done

if [ "$DRY_RUN" = 1 ]; then
  step "Dry run complete"
  if [ "$DRY_FAILURES" -gt 0 ]; then
    printf '  \033[31m%d guard(s) would have blocked this merge.\033[0m\n\n' "$DRY_FAILURES"
    exit 1
  fi
  printf '  \033[32mEvery guard passes. A real run would merge %d PRs.\033[0m\n\n' "${#SLICES[@]}"
  exit 0
fi

set_state '.phase = "DONE"' "ship $FID: shipped"
step "Feature $FID is live"
for s in "${SLICES[@]}"; do info "$(cut -f1 <<<"$s")  $(cut -f2 <<<"$s")"; done
echo
