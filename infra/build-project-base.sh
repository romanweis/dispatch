#!/usr/bin/env bash
# Build (or rebuild) the stopped base container "<project>-base" for one
# project. Ticket containers are later created with `incus copy <p>-base t-<id>`.
#
#   infra/build-project-base.sh <project>     # reads projects/<project>/project.yaml
#
# Requires on the host: incus (image alias agent-base), yq (mikefarah), gh
# logged in (its token is passed into the container as GH_TOKEN for cloning).
# Running ticket containers are not affected by a rebuild.
set -euo pipefail

step()  { printf '\n==> %s\n' "$*"; }
ok()    { printf '    ok    %s\n' "$*"; }
skip()  { printf '    skip  %s\n' "$*"; }
warn()  { printf '    WARN  %s\n' "$*" >&2; }
die()   { printf 'ERROR: %s\n' "$*" >&2; exit 1; }

[ $# -eq 1 ] || { echo "usage: build-project-base.sh <project>" >&2; exit 2; }
PROJECT="$1"

INFRA_DIR="$(cd "$(dirname "$(readlink -f "$0")")" && pwd)"
REPO_DIR="$(cd "$INFRA_DIR/.." && pwd)"
PROJ_DIR="$REPO_DIR/projects/$PROJECT"
YAML="$PROJ_DIR/project.yaml"
PACK_DIR="$REPO_DIR/workflow-pack"
TICKET_CLI="$REPO_DIR/cli/ticket"
PROFILE="${DISPATCH_INCUS_PROFILE:-dispatch-agent}"
SECRETS_CLAUDE_JSON=/srv/dispatch/secrets/claude-json/.claude.json

[ -f "$YAML" ] || die "missing $YAML"
command -v incus >/dev/null 2>&1 || die "incus not on PATH"
incus info >/dev/null 2>&1 || die "cannot talk to incus (incus-admin group not active? log out/in)"
command -v yq >/dev/null 2>&1 && yq --version 2>&1 | grep -qi mikefarah \
  || die "mikefarah yq required (infra/setup-server.sh installs it to /usr/local/bin/yq)"
incus image info agent-base >/dev/null 2>&1 || die "image alias 'agent-base' missing; run infra/build-agent-base.sh"
incus profile show "$PROFILE" >/dev/null 2>&1 || die "profile '$PROFILE' missing; run infra/setup-server.sh"
[ -d "$PACK_DIR" ] || die "missing $PACK_DIR"
[ -f "$TICKET_CLI" ] || die "missing $TICKET_CLI"

GH_TOKEN_VALUE="$(gh auth token 2>/dev/null || true)"
[ -n "$GH_TOKEN_VALUE" ] || die "gh is not logged in on the host (gh auth login)"

# ---------------------------------------------------------------------------
step "read $YAML"
y() { yq -r "$1" "$YAML"; }
NAME="$(y '.name')"
ORG="$(y '.org')"
WORKSPACE="$(y '.workspace')"
ORCH_REPO="$(y '.orchestrator.repo // "orchestrator"')"
DEPLOY_WORKFLOW="$(y '.orchestrator.deployWorkflow // "deploy.yml"')"
mapfile -t REPOS < <(y '.repos[] | .name + "=" + (.role // "")')
[ "$NAME" = "$PROJECT" ] || warn "project.yaml name '$NAME' differs from folder '$PROJECT'; container is named after the folder"
[ -n "$ORG" ] && [ "$ORG" != null ] || die "project.yaml: org missing"
[ -n "$WORKSPACE" ] && [ "$WORKSPACE" != null ] || die "project.yaml: workspace missing"
C="$PROJECT-base"
printf '    org=%s workspace=%s orchestrator=%s repos=%s\n' "$ORG" "$WORKSPACE" "$ORCH_REPO" "${#REPOS[@]}"

GIT_NAME="$(git config --get user.name 2>/dev/null || true)";  GIT_NAME="${GIT_NAME:-Dispatch Agent}"
GIT_EMAIL="$(git config --get user.email 2>/dev/null || true)"; GIT_EMAIL="${GIT_EMAIL:-dispatch-agent@localhost}"

# exec helpers ---------------------------------------------------------------
as_root()  { incus exec "$C" --env DEBIAN_FRONTEND=noninteractive "$@"; }
as_agent() {  # as_agent [--cwd DIR] -- cmd...
  incus exec "$C" --user 1000 --group 1000 \
    --env HOME=/home/agent --env USER=agent --env LOGNAME=agent \
    --env GH_TOKEN="$GH_TOKEN_VALUE" --env WORKSPACE="$WORKSPACE" --env PROJECT="$PROJECT" \
    --env XDG_RUNTIME_DIR=/run/user/1000 "$@"
}
wait_for_ipv4() {
  local ip=""
  for _ in $(seq 1 60); do
    ip="$(incus exec "$C" -- ip -4 -o addr show eth0 2>/dev/null | awk '{print $4}' | cut -d/ -f1 | head -1 || true)"
    [ -n "$ip" ] && { printf '%s' "$ip"; return 0; }
    sleep 1
  done
  return 1
}

# ---------------------------------------------------------------------------
step "fresh container $C"
if incus info "$C" >/dev/null 2>&1; then
  incus delete -f "$C"
  ok "deleted previous $C"
fi
incus launch agent-base "$C" --profile default --profile "$PROFILE"
ip="$(wait_for_ipv4)" || die "no IPv4 on eth0 after 60s"
ok "up at $ip"
as_root -- systemctl is-system-running --wait >/dev/null 2>&1 || true
# Make sure the host's shared dirs landed with the right ownership for uid 1000.
as_agent -- test -w /home/agent/.claude || die "/home/agent/.claude not writable by uid 1000; check shift on $PROFILE and /srv/dispatch/secrets ownership"
as_agent -- test -r /home/agent/.config/gh/hosts.yml || warn "/home/agent/.config/gh/hosts.yml not readable; gh in containers will rely on GH_TOKEN"

# ---------------------------------------------------------------------------
step "environment from project.yaml .env"
n=0
while IFS= read -r k; do
  [ -n "$k" ] || continue
  v="$(yq -r ".env.[\"$k\"]" "$YAML")"
  incus config set "$C" "environment.$k=$v"
  n=$((n+1))
done < <(yq -r '.env // {} | keys | .[]' "$YAML")
incus config set "$C" environment.DISPATCH_PROJECT="$PROJECT" environment.WORKSPACE="$WORKSPACE"
ok "$n project env var(s) + DISPATCH_PROJECT, WORKSPACE"

# ---------------------------------------------------------------------------
step "git identity + gh credential helper (as agent)"
as_agent --env GIT_NAME="$GIT_NAME" --env GIT_EMAIL="$GIT_EMAIL" -- bash -c '
  set -e
  git config --global user.name "$GIT_NAME"
  git config --global user.email "$GIT_EMAIL"
  git config --global init.defaultBranch main
  git config --global pull.rebase false
  gh auth setup-git
'
ok "$GIT_NAME <$GIT_EMAIL>, git uses gh as credential helper"

# ---------------------------------------------------------------------------
step "clone repos into $WORKSPACE"
as_agent -- mkdir -p "$WORKSPACE"
clone_repo() {  # clone_repo <repo>
  local r="$1"
  if as_agent -- test -d "$WORKSPACE/$r/.git"; then
    skip "$r already present"; return 0
  fi
  as_agent --cwd "$WORKSPACE" -- gh repo clone "$ORG/$r" "$r" -- --quiet
  ok "$ORG/$r -> $WORKSPACE/$r"
}
for entry in "${REPOS[@]}"; do
  clone_repo "${entry%%=*}"
done

# ---------------------------------------------------------------------------
step "orchestrator repo $ORG/$ORCH_REPO"
if ! gh repo view "$ORG/$ORCH_REPO" >/dev/null 2>&1; then
  gh repo create "$ORG/$ORCH_REPO" --private --description "Dispatch workflow orchestrator for $PROJECT" >/dev/null
  ok "created private repo $ORG/$ORCH_REPO"
else
  skip "repo exists"
fi
clone_repo "$ORCH_REPO"

if as_agent --cwd "$WORKSPACE/$ORCH_REPO" -- git rev-parse --verify -q HEAD >/dev/null 2>&1; then
  skip "orchestrator repo already has commits; not rendering workflow-pack"
else
  printf '    empty repo: rendering %s\n' "$PACK_DIR"
  render="$(mktemp -d)"
  trap 'rm -rf "$render"' EXIT
  cp -a "$PACK_DIR/." "$render/"
  rm -rf "$render/.git" "$render/TEMPLATE.md"
  esc() { printf '%s' "$1" | sed -e 's/[\/&|\\]/\\&/g'; }
  e_org="$(esc "$ORG")"; e_ws="$(esc "$WORKSPACE")"; e_proj="$(esc "$PROJECT")"; e_dw="$(esc "$DEPLOY_WORKFLOW")"
  # text files only (grep -I skips binaries)
  while IFS= read -r -d '' f; do
    sed -i \
      -e "s|{{ORG}}|$e_org|g" \
      -e "s|{{WORKSPACE}}|$e_ws|g" \
      -e "s|{{PROJECT}}|$e_proj|g" \
      -e "s|{{DEPLOY_WORKFLOW}}|$e_dw|g" "$f"
  done < <(grep -rIl --null -e '{{ORG}}' -e '{{WORKSPACE}}' -e '{{PROJECT}}' -e '{{DEPLOY_WORKFLOW}}' "$render" || true)
  # workflow.env is sourced by scripts/lib.sh, so every value is double-quoted.
  backend_repo="$(y '[.repos[] | select(.role == "backend") | .name][0] // ""')"
  required_checks="$(y '(.orchestrator.requiredChecks // []) | join(",")')"
  probe_hosts="$(y '(.orchestrator.probeHosts // {}) | to_entries | map(.key + "=" + .value) | join(",")')"
  {
    printf '# generated by dispatch infra/build-project-base.sh from projects/%s/project.yaml\n' "$PROJECT"
    printf 'ORG="%s"\n' "$ORG"
    printf 'WORKSPACE="%s"\n' "$WORKSPACE"
    printf 'PROJECT="%s"\n' "$PROJECT"
    printf 'DEPLOY_WORKFLOW="%s"\n' "$DEPLOY_WORKFLOW"
    printf 'ORCHESTRATOR_REPO="%s"\n' "$ORCH_REPO"
    printf 'BACKEND_REPO="%s"\n' "$backend_repo"
    printf 'SCHEMA_FILE="%s"\n' "$(y '.orchestrator.schemaFile // ""')"
    printf 'MIGRATIONS_DIR="%s"\n' "$(y '.orchestrator.migrationsDir // ""')"
    printf 'REQUIRED_CHECKS="%s"\n' "$required_checks"
    printf 'PROBE_HOSTS="%s"\n' "$probe_hosts"
    printf 'EXTRA_MAIN_WORKFLOWS="%s"\n' "$(y '(.orchestrator.extraMainWorkflows // []) | join(",")')"
  } > "$render/workflow.env"
  {
    printf '# repo -> role (role-templates/<role>-task.md); generated from projects/%s/project.yaml\n' "$PROJECT"
    printf 'repos:\n'
    for entry in "${REPOS[@]}"; do
      printf '  - name: %s\n    role: %s\n' "${entry%%=*}" "${entry#*=}"
    done
  } > "$render/repos.yaml"
  # Stream as a tarball so everything is owned by uid 1000 (file push -r cannot set uid).
  tar -C "$render" -cf - . | as_agent -- tar -C "$WORKSPACE/$ORCH_REPO" -xf -
  as_agent --cwd "$WORKSPACE/$ORCH_REPO" -- bash -c '
    set -e
    git add -A
    git commit -q -m "Initial workflow pack (rendered by dispatch build-project-base)"
    git branch -M main
    git push -q -u origin main
  '
  ok "workflow-pack rendered, committed and pushed to $ORG/$ORCH_REPO (main)"
fi

# ---------------------------------------------------------------------------
step "project files"
if [ -f "$PROJ_DIR/CLAUDE.md" ]; then
  incus file push "$PROJ_DIR/CLAUDE.md" "$C$WORKSPACE/CLAUDE.md" --mode 0644 --uid 1000 --gid 1000
  ok "CLAUDE.md -> $WORKSPACE/CLAUDE.md"
else
  warn "no $PROJ_DIR/CLAUDE.md"
fi
incus file push "$TICKET_CLI" "$C/usr/local/bin/ticket" --mode 0755 --uid 0 --gid 0
ok "cli/ticket -> /usr/local/bin/ticket"
if [ -f "$SECRETS_CLAUDE_JSON" ]; then
  incus file push "$SECRETS_CLAUDE_JSON" "$C/home/agent/.claude.json" --mode 0600 --uid 1000 --gid 1000
  ok "$SECRETS_CLAUDE_JSON -> /home/agent/.claude.json"
else
  warn "no $SECRETS_CLAUDE_JSON (setup-server.sh copies it); keeping the minimal one from the image"
fi

# ---------------------------------------------------------------------------
step "provision scripts"
if [ -f "$PROJ_DIR/provision.sh" ]; then
  incus file push "$PROJ_DIR/provision.sh" "$C/tmp/provision.sh" --mode 0755
  printf '    running provision.sh as root (cwd %s)\n' "$WORKSPACE"
  as_root --cwd "$WORKSPACE" --env WORKSPACE="$WORKSPACE" --env PROJECT="$PROJECT" --env ORG="$ORG" \
    -- bash /tmp/provision.sh
  as_root -- rm -f /tmp/provision.sh
  ok "provision.sh done"
else
  skip "no provision.sh"
fi
if [ -f "$PROJ_DIR/provision-user.sh" ]; then
  incus file push "$PROJ_DIR/provision-user.sh" "$C/tmp/provision-user.sh" --mode 0755 --uid 1000 --gid 1000
  printf '    running provision-user.sh as agent (cwd %s)\n' "$WORKSPACE"
  as_agent --cwd "$WORKSPACE" --env ORG="$ORG" -- bash /tmp/provision-user.sh
  as_agent -- rm -f /tmp/provision-user.sh
  ok "provision-user.sh done"
else
  skip "no provision-user.sh"
fi

# ---------------------------------------------------------------------------
step "stop $C"
incus stop "$C"
ok "stopped"

cat <<EOF

Base container '$C' ready (stopped).
  test:     incus copy $C t-test && incus start t-test
            incus exec t-test --user 1000 --group 1000 --env HOME=/home/agent --cwd $WORKSPACE -- bash
            incus delete -f t-test
  rebuild:  $INFRA_DIR/build-project-base.sh $PROJECT
EOF
