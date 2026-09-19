#!/usr/bin/env bash
# Store a container-registry login in the shared secrets dir so every agent container can
# pull private images. The profile mounts /srv/dispatch/secrets/docker at /home/agent/.docker,
# so the credential is picked up by the docker CLI *and* by Testcontainers (it reads
# ~/.docker/config.json itself) in every base and ticket container.
#
#   infra/registry-login.sh --ghcr                       # ghcr.io with the host's gh token
#   infra/registry-login.sh --check --ghcr               # show what is stored and verify it
#   SCW_SECRET_KEY=scwsk... infra/registry-login.sh --registry rg.fr-par.scw.cloud
#   infra/registry-login.sh --registry <host> --user <u>  # prompts for the secret (no echo)
#
# --ghcr covers the common case: GitHub Container Registry packages are private by default,
# so a bare `docker pull ghcr.io/<org>/<image>` in a ticket container gets "denied" until a
# login is stored here. The gh token needs the read:packages scope (gh auth refresh -s
# read:packages on the host); this script checks that before storing anything.
#
# The mount is live: rewriting the file fixes running ticket containers too, no rebuild.
set -euo pipefail

step()  { printf '\n==> %s\n' "$*"; }
ok()    { printf '    ok    %s\n' "$*"; }
warn()  { printf '    WARN  %s\n' "$*" >&2; }
die()   { printf 'ERROR: %s\n' "$*" >&2; exit 1; }

SECRETS="${DISPATCH_SECRETS:-/srv/dispatch/secrets}"
DOCKER_DIR="$SECRETS/docker"
CONFIG="$DOCKER_DIR/config.json"
REGISTRY="${REGISTRY:-ghcr.io}"
# Registries that authenticate with a token alone ignore the username; Scaleway's docs use
# "nologin". GHCR wants the GitHub account name, which --ghcr fills in.
USER_NAME="${REGISTRY_USER:-nologin}"
CHECK_ONLY=0
FORCE=0
GHCR=0

while [ $# -gt 0 ]; do
  case "$1" in
    --ghcr)     GHCR=1; REGISTRY=ghcr.io; shift ;;
    --registry) REGISTRY="${2:?--registry needs a value}"; shift 2 ;;
    --user)     USER_NAME="${2:?--user needs a value}"; shift 2 ;;
    --check)    CHECK_ONLY=1; shift ;;
    --force)    FORCE=1; shift ;;
    -h|--help)  sed -n '2,18p' "$0" | sed 's/^# \{0,1\}//'; exit 0 ;;
    *)          die "unknown argument: $1" ;;
  esac
done

command -v jq >/dev/null 2>&1 || die "jq required (infra/setup-server.sh installs it)"

# ---------------------------------------------------------------------------
if [ "$CHECK_ONLY" -eq 1 ]; then
  step "stored logins in $CONFIG"
  [ -f "$CONFIG" ] || die "$CONFIG does not exist; run this script without --check"
  jq -r '.auths // {} | to_entries[] | "    " + .key + "   user=" + ((.value.auth // "" | @base64d | split(":")[0]) // "?")' "$CONFIG"
  stored="$(jq -r --arg r "$REGISTRY" '.auths[$r].auth // ""' "$CONFIG")"
  [ -n "$stored" ] || die "no entry for $REGISTRY"
  step "verify $REGISTRY"
  if curl -fsS -o /dev/null -u "$(printf '%s' "$stored" | base64 -d)" "https://$REGISTRY/v2/"; then
    ok "credential accepted by $REGISTRY"
  else
    die "credential rejected by $REGISTRY (expired or revoked token?)"
  fi
  exit 0
fi

# ---------------------------------------------------------------------------
SECRET="${SCW_SECRET_KEY:-${REGISTRY_PASSWORD:-}}"
if [ "$GHCR" -eq 1 ] && [ -z "$SECRET" ]; then
  step "gh token for ghcr.io"
  command -v gh >/dev/null 2>&1 || die "gh not on PATH"
  gh auth status >/dev/null 2>&1 || die "gh is not logged in (gh auth login)"
  # GHCR rejects a token without read:packages; say so here instead of letting the pull fail
  # inside a container hours later.
  if ! gh auth status 2>&1 | grep -q 'read:packages'; then
    die "the gh token has no read:packages scope. Run:  gh auth refresh -s read:packages   then re-run this script"
  fi
  USER_NAME="$(gh api user -q .login 2>/dev/null || true)"
  [ -n "$USER_NAME" ] || die "cannot read the GitHub login (gh api user)"
  SECRET="$(gh auth token 2>/dev/null || true)"
  [ -n "$SECRET" ] || die "gh auth token returned nothing"
  ok "user $USER_NAME, token from gh (read:packages present)"
fi

step "secret for $REGISTRY (user $USER_NAME)"
if [ -z "$SECRET" ]; then
  [ -t 0 ] || die "no SCW_SECRET_KEY/REGISTRY_PASSWORD in the environment and stdin is not a terminal"
  printf '    paste the registry secret key (not echoed): '
  read -rs SECRET
  printf '\n'
fi
[ -n "$SECRET" ] || die "empty secret"
ok "${#SECRET} characters"

step "verify against https://$REGISTRY/v2/"
if curl -fsS -o /dev/null -u "$USER_NAME:$SECRET" "https://$REGISTRY/v2/"; then
  ok "accepted"
elif [ "$FORCE" -eq 1 ]; then
  warn "registry rejected the credential; --force given, storing it anyway"
else
  die "registry rejected the credential; check the token (or pass --force to store it regardless)"
fi

# ---------------------------------------------------------------------------
step "write $CONFIG"
install -d -m 0700 "$SECRETS" "$DOCKER_DIR"
auth="$(printf '%s:%s' "$USER_NAME" "$SECRET" | base64 -w0)"
tmp="$(mktemp)"
trap 'rm -f "$tmp"' EXIT
# Merge into whatever is already there (other registries, buildx/plugin settings).
if [ -s "$CONFIG" ] && jq -e . "$CONFIG" >/dev/null 2>&1; then
  jq --arg r "$REGISTRY" --arg a "$auth" '.auths = ((.auths // {}) + {($r): {auth: $a}})' "$CONFIG" > "$tmp"
else
  jq -n --arg r "$REGISTRY" --arg a "$auth" '{auths: {($r): {auth: $a}}}' > "$tmp"
fi
install -m 0600 "$tmp" "$CONFIG"
ok "$REGISTRY stored ($(jq -r '.auths | keys | join(", ")' "$CONFIG"))"

cat <<EOF

Done. Containers read this through the dispatch-agent profile mount
(/srv/dispatch/secrets/docker -> /home/agent/.docker), so:

  running ticket containers   already see it (live bind mount)
  containers built earlier    nothing to do; no rebuild needed
  new profile not applied yet infra/setup-server.sh   (applies agent.profile.yaml)

  verify in a container:  incus exec t-<id> --user 1000 --group 1000 --env HOME=/home/agent \
                            -- docker pull $REGISTRY/<namespace>/<image>:<tag>
EOF
