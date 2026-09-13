#!/usr/bin/env bash
# lib.sh - shared helpers for the orchestrator scripts. Source it; do not run it.
#
# Loads workflow.env (written by Dispatch when the repo was generated) and exposes small
# readers for repos.yaml. Every value the scripts used to hardcode (org, workspace path,
# deploy workflow, migration dir) comes from here.

set -euo pipefail

ORCH="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
WORKFLOW_ENV="$ORCH/workflow.env"
REPOS_YAML="$ORCH/repos.yaml"

[ -f "$WORKFLOW_ENV" ] || { echo "missing $WORKFLOW_ENV (copy workflow.env.example and fill it in)" >&2; exit 1; }
# shellcheck disable=SC1090
source "$WORKFLOW_ENV"

: "${ORG:?ORG must be set in workflow.env}"
: "${WORKSPACE:?WORKSPACE must be set in workflow.env}"
: "${PROJECT:=$(basename "$WORKSPACE")}"
: "${DEPLOY_WORKFLOW:=deploy.yml}"
: "${ORCHESTRATOR_REPO:=orchestrator}"
: "${BACKEND_REPO:=}"
: "${SCHEMA_FILE:=}"
: "${MIGRATIONS_DIR:=}"
: "${REQUIRED_CHECKS:=}"
: "${PROBE_HOSTS:=}"
: "${EXTRA_MAIN_WORKFLOWS:=}"
: "${DEPLOY_TIMEOUT:=2400}"
: "${IDLE_TIMEOUT:=1800}"
: "${CI_TIMEOUT:=2400}"

OWNER="$ORG"

# Local checkout of a repo.
repo_dir() { printf '%s/%s\n' "$WORKSPACE" "$1"; }

# All repo names from repos.yaml, one per line.
repos_list() {
  [ -f "$REPOS_YAML" ] || return 0
  awk '/^[[:space:]]*-[[:space:]]*name:/ { sub(/.*name:[[:space:]]*/, ""); gsub(/["'"'"']/, ""); print }' "$REPOS_YAML"
}

# Role of one repo from repos.yaml, or empty.
role_for() {
  local want="$1"
  [ -f "$REPOS_YAML" ] || return 0
  awk -v want="$want" '
    /^[[:space:]]*-[[:space:]]*name:/ { n=$0; sub(/.*name:[[:space:]]*/, "", n); gsub(/["'"'"']/, "", n); cur=n; next }
    /^[[:space:]]*role:/ && cur==want { r=$0; sub(/.*role:[[:space:]]*/, "", r); gsub(/["'"'"']/, "", r); print r; exit }
  ' "$REPOS_YAML"
}

# Public host for a repo from PROBE_HOSTS (repo=host,repo=host), or empty.
host_for() {
  local want="$1" pair
  [ -n "$PROBE_HOSTS" ] || return 0
  IFS=',' read -ra pairs <<<"$PROBE_HOSTS"
  for pair in "${pairs[@]}"; do
    pair="${pair## }"; pair="${pair%% }"
    [ "${pair%%=*}" = "$want" ] && { printf '%s\n' "${pair#*=}"; return 0; }
  done
  return 0
}

is_backend() { [ -n "$BACKEND_REPO" ] && [ "$1" = "$BACKEND_REPO" ]; }

pass()  { printf '  \033[32mPASS\033[0m  %s\n' "$*"; }
warn()  { printf '  \033[33mWARN\033[0m  %s\n' "$*"; }
info()  { printf '        %s\n' "$*"; }
step()  { printf '\n\033[1m%s\033[0m\n' "$*"; }
abort() { printf '\n\033[31mABORT\033[0m  %s\n\n' "$*" >&2; exit 1; }

# Commit and push a path in the orchestrator repo, rebasing once on rejection. Never force.
orch_commit_push() {
  local msg="$1"; shift
  git -C "$ORCH" add "$@"
  git -C "$ORCH" commit -qm "$msg" || true
  git -C "$ORCH" push -q origin main || { git -C "$ORCH" pull --rebase -q origin main && git -C "$ORCH" push -q origin main; }
}
