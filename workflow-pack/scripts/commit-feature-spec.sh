#!/usr/bin/env bash
# commit-feature-spec.sh <plan-path> <slug>
#
# Copies an approved spec to features/<next-id>-<slug>.md, commits, pushes, prints the id.
# Idempotent by slug: re-running for an existing <slug> prints the existing id and exits 0.
#
# Under Dispatch the feature id is the ticket id and Dispatch writes features/<id>-<slug>.md
# itself when the human starts the ticket, so this script is only needed for hand-driven
# features outside the board (or to commit a feature file Dispatch left uncommitted; see
# CLAUDE.md "How features enter").

set -euo pipefail

WORKFLOW_ENV="$(dirname "$0")/../workflow.env"
[ -f "$WORKFLOW_ENV" ] || { echo "$(basename "$0"): missing $WORKFLOW_ENV. Copy workflow.env.example and fill it in (Dispatch writes it when rendering the pack)." >&2; exit 1; }
source "$WORKFLOW_ENV"
source "$(dirname "$0")/lib.sh"

PLAN="${1:?plan file required}"
SLUG="${2:?slug required}"

[ -f "$PLAN" ] || { echo "plan not found: $PLAN" >&2; exit 1; }

mkdir -p "$ORCH/features"

existing="$(ls "$ORCH"/features/*-"$SLUG".md 2>/dev/null | head -1 || true)"
if [ -n "$existing" ]; then
  basename "$existing" | sed -E 's/^([0-9]+)-.*/\1/'
  exit 0
fi

max=$(ls "$ORCH"/features/ 2>/dev/null \
      | grep -E '^[0-9]+-' | sed -E 's/^([0-9]+)-.*/\1/' \
      | sort -n | tail -1 || true)
next=$(printf '%03d' $(( 10#${max:-0} + 1 )))
dest="$ORCH/features/$next-$SLUG.md"

cp "$PLAN" "$dest"
git -C "$ORCH" add "features/$next-$SLUG.md"
git -C "$ORCH" commit -m "feat($next): $SLUG" >/dev/null
git -C "$ORCH" push --quiet

echo "$next"
