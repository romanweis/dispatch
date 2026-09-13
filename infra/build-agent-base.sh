#!/usr/bin/env bash
# Build the Incus image alias "agent-base" (Ubuntu 24.04 cloud + tooling for
# Claude Code agents). Re-runnable: replaces the alias, cleans up leftovers.
#
#   infra/build-agent-base.sh
#
# The build container uses only the "default" profile plus the same nesting /
# syscall-intercept keys the dispatch-agent profile carries (see
# infra/agent.profile.yaml). It deliberately does NOT use dispatch-agent, so
# no credentials are mounted while the image is produced.
set -euo pipefail

step()  { printf '\n==> %s\n' "$*"; }
ok()    { printf '    ok    %s\n' "$*"; }
skip()  { printf '    skip  %s\n' "$*"; }
die()   { printf 'ERROR: %s\n' "$*" >&2; exit 1; }

BUILD=agent-base-build
ALIAS=agent-base
SRC="${AGENT_BASE_SOURCE:-images:ubuntu/24.04/cloud}"

command -v incus >/dev/null 2>&1 || die "incus not on PATH (run infra/setup-server.sh, then re-login)"
incus info >/dev/null 2>&1 || die "cannot talk to incus (not in incus-admin group yet? log out/in)"

wait_for_ipv4() {  # wait_for_ipv4 <container>
  local c="$1" ip=""
  for _ in $(seq 1 60); do
    ip="$(incus exec "$c" -- ip -4 -o addr show eth0 2>/dev/null | awk '{print $4}' | cut -d/ -f1 | head -1 || true)"
    [ -n "$ip" ] && { printf '%s' "$ip"; return 0; }
    sleep 1
  done
  return 1
}

# ---------------------------------------------------------------------------
step "cleanup leftovers"
if incus info "$BUILD" >/dev/null 2>&1; then
  incus delete -f "$BUILD"
  ok "deleted stale $BUILD"
else
  skip "no stale $BUILD"
fi

# ---------------------------------------------------------------------------
step "launch $BUILD from $SRC"
incus launch "$SRC" "$BUILD" --profile default \
  -c security.nesting=true \
  -c security.syscalls.intercept.mknod=true \
  -c security.syscalls.intercept.setxattr=true
ip="$(wait_for_ipv4 "$BUILD")" || die "no IPv4 on eth0 after 60s"
ok "up at $ip"
incus exec "$BUILD" -- cloud-init status --wait >/dev/null 2>&1 || true
ok "cloud-init finished"

# ---------------------------------------------------------------------------
step "provision (root, streaming)"
incus exec "$BUILD" --env DEBIAN_FRONTEND=noninteractive -- bash -s <<'INSIDE'
set -euo pipefail
log() { printf '    [c] %s\n' "$*"; }

log "apt base packages"
apt-get update -qq
apt-get install -y -qq --no-install-recommends \
  git curl jq unzip zsh tmux build-essential python3 python3-venv ca-certificates gnupg \
  ripgrep fd-find sudo openssh-server locales less vim-tiny fuse-overlayfs \
  apt-transport-https software-properties-common iproute2 dbus-user-session
ln -sf "$(command -v fdfind)" /usr/local/bin/fd
locale-gen en_US.UTF-8 >/dev/null 2>&1 || true

log "user agent (uid/gid 1000)"
# The cloud image ships an "ubuntu" user on uid 1000; replace it.
if id -u ubuntu >/dev/null 2>&1; then
  loginctl terminate-user ubuntu 2>/dev/null || true
  userdel -r ubuntu 2>/dev/null || userdel ubuntu
  rm -rf /home/ubuntu
fi
# userdel leaves the "ubuntu" group behind when the image pre-seeded it; free GID 1000.
if getent group 1000 >/dev/null && [ "$(getent group 1000 | cut -d: -f1)" != "agent" ]; then
  groupdel "$(getent group 1000 | cut -d: -f1)"
fi
if ! getent group agent >/dev/null; then groupadd -g 1000 agent; fi
if ! id -u agent >/dev/null 2>&1; then
  useradd -m -u 1000 -g 1000 -s /bin/bash -d /home/agent agent
fi
printf 'agent ALL=(ALL) NOPASSWD:ALL\n' > /etc/sudoers.d/agent
chmod 0440 /etc/sudoers.d/agent
# Stop cloud-init from re-creating a default "ubuntu" user in copies of this image.
mkdir -p /etc/cloud/cloud.cfg.d
printf 'users: []\n' > /etc/cloud/cloud.cfg.d/99-dispatch-no-default-user.cfg
# Mount points for the dispatch-agent profile disks, owned by agent so the
# parent dirs are not created root:root at first start.
install -d -o agent -g agent -m 700 /home/agent/.claude /home/agent/.config /home/agent/.config/gh
install -d -o agent -g agent -m 755 /home/agent/.local /home/agent/.local/bin

log "node 22 (nodesource) + pnpm"
if ! command -v node >/dev/null 2>&1 || ! node -v | grep -q '^v22\.'; then
  curl -fsSL https://deb.nodesource.com/setup_22.x | bash - >/dev/null
  apt-get install -y -qq nodejs
fi
export COREPACK_ENABLE_DOWNLOAD_PROMPT=0
corepack enable pnpm
sudo -u agent -H env COREPACK_ENABLE_DOWNLOAD_PROMPT=0 pnpm --version >/dev/null

log "claude code"
npm install -g @anthropic-ai/claude-code >/dev/null
chown -R agent:agent /usr/lib/node_modules/@anthropic-ai
claude --version || true

log "gh"
install -d -m 0755 /etc/apt/keyrings
curl -fsSL https://cli.github.com/packages/githubcli-archive-keyring.gpg -o /etc/apt/keyrings/githubcli-archive-keyring.gpg
chmod go+r /etc/apt/keyrings/githubcli-archive-keyring.gpg
printf 'deb [arch=%s signed-by=/etc/apt/keyrings/githubcli-archive-keyring.gpg] https://cli.github.com/packages stable main\n' \
  "$(dpkg --print-architecture)" > /etc/apt/sources.list.d/github-cli.list
apt-get update -qq
apt-get install -y -qq gh

log "docker ce (get.docker.com)"
if ! command -v docker >/dev/null 2>&1; then
  curl -fsSL https://get.docker.com | sh >/dev/null
fi
usermod -aG docker agent
systemctl enable docker >/dev/null 2>&1 || true
systemctl restart docker || true
sleep 2
# overlay2 on top of the ZFS-backed rootfs needs OpenZFS >= 2.2 (Ubuntu 24.04
# has it). If the daemon still cannot start, fall back to fuse-overlayfs.
if ! docker info --format '{{.Driver}}' >/dev/null 2>&1; then
  log "docker: default storage driver failed, switching to fuse-overlayfs"
  printf '{ "storage-driver": "fuse-overlayfs" }\n' > /etc/docker/daemon.json
  systemctl restart docker
  sleep 2
fi
log "docker storage driver: $(docker info --format '{{.Driver}}' 2>/dev/null || echo 'NOT RUNNING')"

log "linger for agent (user systemd units survive logout)"
loginctl enable-linger agent || true

log "cleanup"
apt-get clean
rm -rf /var/lib/apt/lists/* /tmp/* /root/.npm /home/agent/.npm 2>/dev/null || true
cloud-init clean --logs >/dev/null 2>&1 || true
INSIDE
ok "provisioned"

# ---------------------------------------------------------------------------
step "minimal /home/agent/.claude.json (no onboarding prompt)"
# Sits at ~/.claude.json, i.e. OUTSIDE the bind-mounted ~/.claude dir. The
# real one from /srv/dispatch/secrets/claude-json is pushed by
# build-project-base.sh and overrides this.
tmp="$(mktemp)"
printf '{"hasCompletedOnboarding": true, "theme": "dark"}\n' > "$tmp"
incus file push "$tmp" "$BUILD/home/agent/.claude.json" --mode 0600 --uid 1000 --gid 1000
rm -f "$tmp"
ok "pushed"

# ---------------------------------------------------------------------------
step "publish image $ALIAS"
incus stop "$BUILD"
incus publish "$BUILD" --alias "$ALIAS" --reuse --compression none \
  description="Dispatch agent base ($(date -u +%Y-%m-%dT%H:%MZ))"
incus delete "$BUILD"
ok "$(incus image list "$ALIAS" -c lfs --format csv | head -1 | tr ',' ' ')"

cat <<EOF

Image '$ALIAS' ready.
  inspect:  incus image info $ALIAS
  test:     incus launch $ALIAS scratch --profile default --profile dispatch-agent
            incus exec scratch --user 1000 --group 1000 --env HOME=/home/agent -- claude --version
            incus delete -f scratch
  next:     infra/build-project-base.sh <project>
EOF
