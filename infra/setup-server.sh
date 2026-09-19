#!/usr/bin/env bash
# One-shot (re-runnable) server setup for Dispatch on Ubuntu 24.04.
#
# Run interactively as roman (NOT as root); sudo will prompt for a password.
#   ~/dispatch/infra/setup-server.sh
#
# Tunables (env):
#   INCUS_POOL_SIZE   ZFS loop-file size for the "default" pool (default 400GiB)
#   DISPATCH_PORT     API port written into /srv/dispatch/.env (default 9300)
#
# Every step is idempotent; re-run after `gh auth login` or after a re-login
# that activates the incus-admin group.
set -euo pipefail

# ---------------------------------------------------------------------------
# helpers
# ---------------------------------------------------------------------------
step()  { printf '\n==> %s\n' "$*"; }
ok()    { printf '    ok    %s\n' "$*"; }
skip()  { printf '    skip  %s\n' "$*"; }
warn()  { printf '    WARN  %s\n' "$*" >&2; }
die()   { printf 'ERROR: %s\n' "$*" >&2; exit 1; }

if [ "$(id -u)" -eq 0 ]; then
  die "run this as your normal user (roman), not as root; it calls sudo where needed"
fi

SCRIPT_PATH="$(readlink -f "$0")"
INFRA_DIR="$(cd "$(dirname "$SCRIPT_PATH")" && pwd)"
REPO_DIR="$(cd "$INFRA_DIR/.." && pwd)"
ME="$(id -un)"
INCUS_POOL_SIZE="${INCUS_POOL_SIZE:-400GiB}"
DISPATCH_PORT="${DISPATCH_PORT:-9300}"
SRV=/srv/dispatch
ENV_FILE="$SRV/.env"
SECRETS="$SRV/secrets"
PROFILE_NAME=dispatch-agent
BRIDGE=incusbr0
export DEBIAN_FRONTEND=noninteractive

printf 'Dispatch server setup\n  repo:  %s\n  user:  %s\n' "$REPO_DIR" "$ME"

step "sudo credentials"
sudo -v
ok "sudo works"

# ---------------------------------------------------------------------------
# 1. apt packages
# ---------------------------------------------------------------------------
step "apt packages"
# Ubuntu 24.04 package names: `incus` (universe, 6.0.x) depends on
# incus-client; postgresql-16 is the 24.04 default major.
PKGS=(incus zfsutils-linux postgresql-16 postgresql-client-16 jq curl git unzip
      ca-certificates gnupg openssl uidmap)
missing=()
for p in "${PKGS[@]}"; do
  dpkg -s "$p" >/dev/null 2>&1 || missing+=("$p")
done
if [ "${#missing[@]}" -gt 0 ]; then
  sudo apt-get update -qq
  sudo apt-get install -y -qq "${missing[@]}"
  ok "installed: ${missing[*]}"
else
  skip "all packages present"
fi

if ! lsmod | grep -q '^zfs'; then
  sudo modprobe zfs || die "could not load zfs module (kernel modules-extra missing?)"
  ok "zfs kernel module loaded"
else
  skip "zfs module already loaded"
fi

# ---------------------------------------------------------------------------
# 2. yq (mikefarah) for build-project-base.sh
# ---------------------------------------------------------------------------
step "yq (mikefarah/yq)"
if command -v yq >/dev/null 2>&1 && yq --version 2>&1 | grep -qi mikefarah; then
  skip "$(yq --version 2>&1)"
else
  arch="$(dpkg --print-architecture)"   # amd64 / arm64
  tmp="$(mktemp)"
  curl -fsSL "https://github.com/mikefarah/yq/releases/latest/download/yq_linux_${arch}" -o "$tmp"
  sudo install -m 0755 "$tmp" /usr/local/bin/yq
  rm -f "$tmp"
  ok "$(yq --version 2>&1) -> /usr/local/bin/yq"
fi

# ---------------------------------------------------------------------------
# 3. incus-admin group membership (may re-exec via sg)
# ---------------------------------------------------------------------------
step "incus-admin group"
sudo systemctl enable --now incus.socket incus >/dev/null 2>&1 || sudo systemctl enable --now incus >/dev/null
if ! getent group incus-admin >/dev/null; then
  die "group incus-admin does not exist; is the incus package installed?"
fi
if ! getent group incus-admin | awk -F: '{print $4}' | tr ',' '\n' | grep -qx "$ME"; then
  sudo usermod -aG incus-admin "$ME"
  ok "added $ME to incus-admin"
else
  skip "$ME already in incus-admin (account)"
fi
if ! id -nG | tr ' ' '\n' | grep -qx incus-admin; then
  if [ "${DISPATCH_SETUP_REEXEC:-}" = "1" ]; then
    die "still not in incus-admin after re-exec; log out and back in, then re-run this script"
  fi
  printf '    group not active in this shell; re-executing under: sg incus-admin\n'
  printf '    (a full logout/login is needed before `incus` works in NEW shells and for the dispatch service)\n'
  cmd="$(printf '%q ' bash "$SCRIPT_PATH" "$@")"
  export DISPATCH_SETUP_REEXEC=1 INCUS_POOL_SIZE DISPATCH_PORT
  exec sg incus-admin -c "$cmd"
fi
ok "incus-admin active in this process"
incus info >/dev/null || die "incus daemon not reachable"

# ---------------------------------------------------------------------------
# 4. incus admin init (ZFS loop pool + NAT bridge)
# ---------------------------------------------------------------------------
step "incus admin init"
if incus storage show default >/dev/null 2>&1 && incus network show "$BRIDGE" >/dev/null 2>&1; then
  skip "storage pool 'default' and network '$BRIDGE' already exist"
else
  if incus storage show default >/dev/null 2>&1 || incus network show "$BRIDGE" >/dev/null 2>&1; then
    die "incus is partially initialised (only one of pool 'default' / network '$BRIDGE' exists); fix manually"
  fi
  printf '    creating ZFS loop pool default (%s) at /var/lib/incus/disks/default.img and bridge %s\n' "$INCUS_POOL_SIZE" "$BRIDGE"
  incus admin init --preseed <<EOF
config: {}
networks:
- name: $BRIDGE
  type: bridge
  config:
    ipv4.address: auto
    ipv4.nat: "true"
    ipv6.address: none
storage_pools:
- name: default
  driver: zfs
  config:
    size: $INCUS_POOL_SIZE
profiles:
- name: default
  devices:
    eth0:
      name: eth0
      network: $BRIDGE
      type: nic
    root:
      path: /
      pool: default
      type: disk
EOF
  ok "initialised"
fi

# ---------------------------------------------------------------------------
# 5. secrets dirs + claude / gh credentials
# ---------------------------------------------------------------------------
step "secrets in $SECRETS"
sudo install -d -m 0755 -o "$ME" -g "$ME" "$SRV"
install -d -m 0700 "$SECRETS" "$SECRETS/claude" "$SECRETS/gh" "$SECRETS/claude-json" "$SECRETS/docker"

# cp -u: only refresh when the host copy is newer, so a token refreshed by a
# container (which writes into the bind-mounted dir) is not clobbered.
if [ -f "$HOME/.claude/.credentials.json" ]; then
  cp -u "$HOME/.claude/.credentials.json" "$SECRETS/claude/.credentials.json"
  chmod 600 "$SECRETS/claude/.credentials.json"
  ok "claude/.credentials.json"
else
  warn "~/.claude/.credentials.json not found; run 'claude' once on the host and log in, then re-run"
fi
if [ -f "$HOME/.claude.json" ]; then
  cp -u "$HOME/.claude.json" "$SECRETS/claude/.claude.json"
  cp -u "$HOME/.claude.json" "$SECRETS/claude-json/.claude.json"
  chmod 600 "$SECRETS/claude/.claude.json" "$SECRETS/claude-json/.claude.json"
  ok "claude/.claude.json + claude-json/.claude.json"
else
  warn "~/.claude.json not found"
fi

gh_ok=0
if gh auth status >/dev/null 2>&1; then
  for f in hosts.yml config.yml; do
    if [ -f "$HOME/.config/gh/$f" ]; then
      cp -u "$HOME/.config/gh/$f" "$SECRETS/gh/$f"
      chmod 600 "$SECRETS/gh/$f"
    fi
  done
  gh_ok=1
  ok "gh/hosts.yml + config.yml"
else
  warn "gh is not logged in. Run:  gh auth login   (HTTPS, scopes incl. repo, workflow) and re-run this script"
fi

# $SECRETS/docker is mounted at /home/agent/.docker by the profile, so every container
# inherits the registry logins stored there (private images pulled by Testcontainers, e.g.
# ghcr.io packages, private by default). Written by infra/registry-login.sh, or seeded here from
# the host's own docker login when there is one. The directory has to exist before a
# container starts or incus refuses the mount.
registry_ok=0
if [ -f "$SECRETS/docker/config.json" ]; then
  registry_ok=1
  ok "docker/config.json ($(jq -r '.auths // {} | keys | join(", ")' "$SECRETS/docker/config.json" 2>/dev/null || echo unreadable))"
elif [ -f "$HOME/.docker/config.json" ] && jq -e '(.auths // {}) | length > 0' "$HOME/.docker/config.json" >/dev/null 2>&1; then
  cp -u "$HOME/.docker/config.json" "$SECRETS/docker/config.json"
  chmod 600 "$SECRETS/docker/config.json"
  registry_ok=1
  ok "docker/config.json copied from ~/.docker"
else
  warn "no private-registry login yet; pulls of private images in containers will fail. Run: $INFRA_DIR/registry-login.sh"
fi

# ---------------------------------------------------------------------------
# 6. /srv/dispatch/.env (never overwrite existing keys)
# ---------------------------------------------------------------------------
step "$ENV_FILE"
[ -f "$ENV_FILE" ] || { : > "$ENV_FILE"; chmod 600 "$ENV_FILE"; }
env_get() {  # env_get KEY -> value (quotes stripped), empty if absent
  sed -n "s/^$1=//p" "$ENV_FILE" | head -1 | sed -e 's/^"//' -e 's/"$//'
}
env_default() {  # env_default KEY VALUE ; appends only when KEY is absent
  if grep -q "^$1=" "$ENV_FILE"; then
    skip "$1 already set"
  else
    printf '%s=%s\n' "$1" "$2" >> "$ENV_FILE"
    ok "$1 written"
  fi
}
db_pw="$(env_get DISPATCH_DB_PASSWORD)"
if [ -z "$db_pw" ]; then
  existing="$(env_get DISPATCH_DB)"
  if [ -n "$existing" ]; then
    db_pw="$(printf '%s' "$existing" | sed -n 's/.*Password=\([^;]*\).*/\1/p')"
  fi
fi
if [ -z "$db_pw" ]; then
  db_pw="$(openssl rand -hex 24)"
fi
env_default DISPATCH_DB_PASSWORD "$db_pw"
env_default DISPATCH_DB "\"Host=127.0.0.1;Port=5432;Database=dispatch;Username=dispatch;Password=$db_pw\""
env_default DISPATCH_BIND_URLS "\"http://0.0.0.0:$DISPATCH_PORT\""
env_default DISPATCH_PROJECTS_DIR "$REPO_DIR/projects"
env_default DISPATCH_REPO_DIR "$REPO_DIR"
env_default DISPATCH_INCUS_BRIDGE "$BRIDGE"
env_default DISPATCH_INCUS_PROFILE "$PROFILE_NAME"
# ASP.NET-style alias for the same setting (docs/project-config.md).
env_default Dispatch__ProjectsDir "$REPO_DIR/projects"
chmod 600 "$ENV_FILE"

# ---------------------------------------------------------------------------
# 7. Incus profile dispatch-agent
# ---------------------------------------------------------------------------
step "subuid/subgid hole for raw.idmap (host uid 1000 <-> container uid 1000)"
idmap_changed=0
for f in /etc/subuid /etc/subgid; do
  if ! grep -qx 'root:1000:1' "$f" 2>/dev/null; then
    echo 'root:1000:1' | sudo tee -a "$f" >/dev/null
    ok "added root:1000:1 to $f"
    idmap_changed=1
  else
    skip "$f already has root:1000:1"
  fi
done
if [ "$idmap_changed" = 1 ]; then sudo systemctl restart incus; ok "incus restarted"; fi

step "incus profile $PROFILE_NAME"
if ! incus profile show "$PROFILE_NAME" >/dev/null 2>&1; then
  incus profile create "$PROFILE_NAME" >/dev/null
  ok "created"
fi
incus profile edit "$PROFILE_NAME" < "$INFRA_DIR/agent.profile.yaml"
ok "applied $INFRA_DIR/agent.profile.yaml"

# ---------------------------------------------------------------------------
# 8. PostgreSQL role + database
# ---------------------------------------------------------------------------
step "postgresql"
sudo systemctl enable --now postgresql >/dev/null
# The password reaches psql via \getenv (psql >= 15) so it never appears on a
# command line. sudo with ALL privileges permits --preserve-env=VAR.
DISPATCH_PW="$db_pw" sudo --preserve-env=DISPATCH_PW -u postgres psql -qtA -v ON_ERROR_STOP=1 <<'SQL'
\getenv pw DISPATCH_PW
SELECT format('CREATE ROLE dispatch LOGIN PASSWORD %L', :'pw')
  WHERE NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'dispatch') \gexec
ALTER ROLE dispatch WITH LOGIN PASSWORD :'pw';
SELECT 'CREATE DATABASE dispatch OWNER dispatch'
  WHERE NOT EXISTS (SELECT 1 FROM pg_database WHERE datname = 'dispatch') \gexec
SQL
ok "role 'dispatch' + database 'dispatch' (password synced from .env)"

# ---------------------------------------------------------------------------
# 9. .NET 10 SDK (user-local) + Node 22 + pnpm
# ---------------------------------------------------------------------------
step ".NET 10 SDK -> ~/.dotnet"
export DOTNET_ROOT="$HOME/.dotnet"
export PATH="$DOTNET_ROOT:$DOTNET_ROOT/tools:$PATH"
if [ -x "$DOTNET_ROOT/dotnet" ] && "$DOTNET_ROOT/dotnet" --list-sdks 2>/dev/null | grep -q '^10\.'; then
  skip "$("$DOTNET_ROOT/dotnet" --list-sdks | grep '^10\.' | tail -1)"
else
  tmp="$(mktemp)"
  curl -fsSL https://dot.net/v1/dotnet-install.sh -o "$tmp"
  bash "$tmp" --channel 10.0 --install-dir "$DOTNET_ROOT"
  rm -f "$tmp"
  ok "$("$DOTNET_ROOT/dotnet" --list-sdks | grep '^10\.' | tail -1)"
fi
if ! grep -q 'DOTNET_ROOT' "$HOME/.profile" 2>/dev/null; then
  cat >> "$HOME/.profile" <<'EOF'

# dispatch: user-local .NET SDK
export DOTNET_ROOT="$HOME/.dotnet"
export PATH="$PATH:$HOME/.dotnet:$HOME/.dotnet/tools"
export DOTNET_CLI_TELEMETRY_OPTOUT=1
EOF
  ok "PATH/DOTNET_ROOT added to ~/.profile"
else
  skip "~/.profile already exports DOTNET_ROOT"
fi

step "Node 22 (nodesource) + pnpm (corepack)"
if command -v node >/dev/null 2>&1 && node -v | grep -q '^v22\.'; then
  skip "node $(node -v)"
else
  tmp="$(mktemp)"
  curl -fsSL https://deb.nodesource.com/setup_22.x -o "$tmp"
  sudo -E bash "$tmp"
  rm -f "$tmp"
  sudo apt-get install -y -qq nodejs
  ok "node $(node -v)"
fi
export COREPACK_ENABLE_DOWNLOAD_PROMPT=0
if command -v pnpm >/dev/null 2>&1; then
  skip "pnpm $(pnpm --version 2>/dev/null || echo '(shim present)')"
else
  sudo corepack enable pnpm
  pnpm --version >/dev/null
  ok "pnpm $(pnpm --version)"
fi

# ---------------------------------------------------------------------------
# 10. ufw
# ---------------------------------------------------------------------------
step "ufw"
if command -v ufw >/dev/null 2>&1 && sudo ufw status 2>/dev/null | head -1 | grep -q 'Status: active'; then
  sudo ufw allow in on tailscale0 to any port "$DISPATCH_PORT" proto tcp comment 'dispatch ui (tailscale)' >/dev/null
  sudo ufw allow in on "$BRIDGE"  to any port "$DISPATCH_PORT" proto tcp comment 'dispatch api (agent containers)' >/dev/null
  ok "allowed tcp/$DISPATCH_PORT on tailscale0 and $BRIDGE"
else
  skip "ufw not active; nothing to do (port $DISPATCH_PORT stays reachable on all interfaces)"
fi

# ---------------------------------------------------------------------------
# 11. user systemd unit
# ---------------------------------------------------------------------------
step "systemd user unit dispatch.service"
install -Dm644 "$INFRA_DIR/dispatch.service" "$HOME/.config/systemd/user/dispatch.service"
sudo loginctl enable-linger "$ME"
export XDG_RUNTIME_DIR="${XDG_RUNTIME_DIR:-/run/user/$(id -u)}"
systemctl --user daemon-reload
systemctl --user enable dispatch >/dev/null 2>&1
ok "installed + enabled (not started: binary not built yet)"

# ---------------------------------------------------------------------------
# summary
# ---------------------------------------------------------------------------
cat <<EOF

Setup complete.

  env file:        $ENV_FILE
  secrets:         $SECRETS/{claude,gh,claude-json,docker}
  incus:           pool default (zfs, /var/lib/incus/disks/default.img), bridge $BRIDGE, profile $PROFILE_NAME
  postgres:        dispatch@127.0.0.1:5432/dispatch
  service:         systemctl --user {start,status,restart} dispatch

Next steps:
  1. Log out and back in (or reboot) so the incus-admin group applies to new shells
     AND to the user systemd manager (needed before 'systemctl --user start dispatch').
EOF
if [ "$gh_ok" -eq 0 ]; then
  printf '  2. gh auth login          # then re-run infra/setup-server.sh to copy the gh credentials\n'
else
  printf '  2. (gh credentials copied)\n'
fi
if [ "$registry_ok" -eq 0 ]; then
  printf '  3. %s/registry-login.sh   # private-registry login for the containers\n' "$INFRA_DIR"
else
  printf '  3. (registry login in place)\n'
fi
cat <<EOF
  4. $INFRA_DIR/build-agent-base.sh              # Incus image 'agent-base'
  5. $INFRA_DIR/build-project-base.sh <project>  # for each projects/<project>/project.yaml
  6. $INFRA_DIR/deploy.sh                        # build API + UI, start the service
EOF
