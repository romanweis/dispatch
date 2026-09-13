#!/usr/bin/env bash
# fleet-status.sh <feature-id>
#
# Read-only report on one feature: its PRs, their CI, the review gate, and the deploy state of
# every repo it touches.
#
# The fleet comes from tasks/<id>/result.json, not from `gh search prs`. The search index is
# eventually consistent and can return FEWER PRs than exist. Believing it could mean shipping a
# backend and never shipping the client that calls its new fields. The search is kept only as a
# cross-check, and a mismatch is reported loudly rather than silently reconciled.

set -euo pipefail

WORKFLOW_ENV="$(dirname "$0")/../workflow.env"
[ -f "$WORKFLOW_ENV" ] || { echo "$(basename "$0"): missing $WORKFLOW_ENV. Copy workflow.env.example and fill it in (Dispatch writes it when rendering the pack)." >&2; exit 1; }
source "$WORKFLOW_ENV"
source "$(dirname "$0")/lib.sh"

FID="${1:?feature-id required}"
RESULT="$ORCH/tasks/$FID/result.json"
STATE="$ORCH/tasks/$FID/state.json"

[ -f "$RESULT" ] || { echo "no $RESULT. Feature $FID has not been implemented yet"; exit 1; }

echo "[fleet] feature:$FID  ($PROJECT / $OWNER)"
echo

if [ -f "$STATE" ]; then
  jq -r '"  phase:  \(.phase // "?")\n  round:  \(.round // 0)\n  gate:   \(.gate // "PENDING")\(if .frozen then "\n  FROZEN" else "" end)\(if .haltReason then "\n  halted: \(.haltReason)" else "" end)"' "$STATE"
else
  echo "  phase:  IMPLEMENTED (not yet reviewed. Run /review-feature $FID)"
fi

if [ -f "$ORCH/FREEZE" ] || git -C "$ORCH" cat-file -e origin/main:FREEZE 2>/dev/null; then
  echo
  echo "  *** FREEZE IS SET. Nothing will merge ***"
fi

echo
echo "[fleet] PRs (authoritative: result.json)"
echo

printf "  %-28s %-6s %-8s %-9s %s\n" REPO TIER STATE CI URL
jq -r '.prs | sort_by(.mergeOrder // 1) | .[] | "\(.repo)\t\(.mergeOrder // 1)\t\(.url)"' "$RESULT" \
| while IFS=$'\t' read -r repo tier url; do
  num="${url##*/}"
  state="$(gh pr view "$num" --repo "$OWNER/$repo" --json state -q .state 2>/dev/null || echo "?")"
  if [ "$state" = "MERGED" ]; then
    ci="merged"
  else
    ci="$(gh pr checks "$num" --repo "$OWNER/$repo" --json state -q '[.[].state] | unique | join(",")' 2>/dev/null || echo "n/a")"
  fi
  printf "  %-28s %-6s %-8s %-9s %s\n" "$repo" "$tier" "$state" "$ci" "$url"
done

# Cross-check the search index. A mismatch does not change the fleet; it is a signal that a PR was
# opened outside result.json, or that the index is lagging.
SEARCHED="$(gh search prs --label "feature:$FID" --owner "$OWNER" --json url -q 'length' 2>/dev/null || echo 0)"
LISTED="$(jq '.prs | length' "$RESULT")"
if [ "$SEARCHED" -ne "$LISTED" ]; then
  echo
  echo "  note: label search finds $SEARCHED PRs, result.json lists $LISTED."
  echo "        result.json wins. If a PR is genuinely missing from it, fix that before shipping."
fi

echo
echo "[fleet] deploy state on main ($DEPLOY_WORKFLOW)"
echo

jq -r '.prs | sort_by(.mergeOrder // 1) | .[] | .repo' "$RESULT" | sort -u | while read -r repo; do
  run="$(gh run list --repo "$OWNER/$repo" --workflow "$DEPLOY_WORKFLOW" --branch main \
         --json status,conclusion,headSha,createdAt -q '.[0]' 2>/dev/null || echo null)"
  if [ "$run" = "null" ] || [ -z "$run" ]; then
    printf "  %-28s %s\n" "$repo" "no deploy runs"
  else
    printf "  %-28s %-12s %-10s %s\n" "$repo" \
      "$(jq -r '.status' <<<"$run")" "$(jq -r '.conclusion // "-"' <<<"$run")" "$(jq -r '.headSha[0:8]' <<<"$run")"
  fi
done

echo
echo "[fleet] review"
echo
if [ -f "$ORCH/tasks/$FID/review.md" ]; then
  grep -E '^\| F-|^Open after' "$ORCH/tasks/$FID/review.md" | tail -20 | sed 's/^/  /'
else
  echo "  no review.md yet"
fi
echo
