#!/usr/bin/env bash
# Build the Dispatch API (.NET) and UI (Vite) and (re)start the user service.
#
#   infra/deploy.sh
#
# Publishes into a staging dir and swaps it into ~/.local/opt/dispatch only
# after both builds succeeded, so a failed build never takes the service down.
set -euo pipefail

step()  { printf '\n==> %s\n' "$*"; }
ok()    { printf '    ok    %s\n' "$*"; }
warn()  { printf '    WARN  %s\n' "$*" >&2; }
die()   { printf 'ERROR: %s\n' "$*" >&2; exit 1; }

INFRA_DIR="$(cd "$(dirname "$(readlink -f "$0")")" && pwd)"
REPO_DIR="$(cd "$INFRA_DIR/.." && pwd)"
API_PROJ="$REPO_DIR/server/Dispatch.Api"
UI_DIR="$REPO_DIR/ui"
TARGET="$HOME/.local/opt/dispatch"
STAGE="$TARGET.new"
OLD="$TARGET.old"
ENV_FILE=/srv/dispatch/.env

export DOTNET_ROOT="${DOTNET_ROOT:-$HOME/.dotnet}"
export PATH="$DOTNET_ROOT:$DOTNET_ROOT/tools:$HOME/.local/bin:$PATH"
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 COREPACK_ENABLE_DOWNLOAD_PROMPT=0
export XDG_RUNTIME_DIR="${XDG_RUNTIME_DIR:-/run/user/$(id -u)}"

[ -d "$API_PROJ" ] || die "missing $API_PROJ"
[ -f "$UI_DIR/package.json" ] || die "missing $UI_DIR/package.json"
command -v dotnet >/dev/null 2>&1 || die "dotnet not found (infra/setup-server.sh installs it to ~/.dotnet)"
command -v pnpm  >/dev/null 2>&1 || die "pnpm not found (corepack enable pnpm)"
[ -f "$ENV_FILE" ] || die "missing $ENV_FILE (run infra/setup-server.sh)"

port="$(sed -n 's/^DISPATCH_BIND_URLS=//p' "$ENV_FILE" | tr -d '"' | sed -n 's/.*:\([0-9]\+\).*/\1/p' | head -1)"
port="${port:-9300}"

# ---------------------------------------------------------------------------
step "ui: pnpm install + build ($UI_DIR)"
if [ -f "$UI_DIR/pnpm-lock.yaml" ]; then
  pnpm --dir "$UI_DIR" install --frozen-lockfile
else
  warn "no pnpm-lock.yaml; installing without --frozen-lockfile"
  pnpm --dir "$UI_DIR" install
fi
rm -rf "$UI_DIR/dist"
pnpm --dir "$UI_DIR" build
[ -f "$UI_DIR/dist/index.html" ] || die "ui build produced no dist/index.html; aborting before touching the service"
ok "ui/dist built"

# ---------------------------------------------------------------------------
step "api: dotnet publish -> $STAGE"
rm -rf "$STAGE"
dotnet publish "$API_PROJ" -c Release -o "$STAGE"
[ -x "$STAGE/Dispatch.Api" ] || chmod +x "$STAGE/Dispatch.Api" 2>/dev/null || die "publish did not produce $STAGE/Dispatch.Api"
rm -rf "$STAGE/wwwroot"
mkdir -p "$STAGE/wwwroot"
cp -a "$UI_DIR/dist/." "$STAGE/wwwroot/"
ok "published + wwwroot copied"

# ---------------------------------------------------------------------------
step "swap into $TARGET and restart dispatch"
systemctl --user stop dispatch 2>/dev/null || true
rm -rf "$OLD"
[ -d "$TARGET" ] && mv "$TARGET" "$OLD"
mkdir -p "$(dirname "$TARGET")"
mv "$STAGE" "$TARGET"
systemctl --user daemon-reload
systemctl --user enable dispatch >/dev/null 2>&1 || true
if ! systemctl --user restart dispatch; then
  warn "restart failed; rolling back to previous build"
  rm -rf "$TARGET"; [ -d "$OLD" ] && mv "$OLD" "$TARGET"
  systemctl --user restart dispatch || true
  die "dispatch failed to start (journalctl --user -u dispatch -n 50)"
fi
ok "service restarted"

# ---------------------------------------------------------------------------
step "health check http://127.0.0.1:$port/healthz"
for _ in $(seq 1 30); do
  if out="$(curl -fsS --max-time 2 "http://127.0.0.1:$port/healthz" 2>/dev/null)"; then
    ok "$out"
    rm -rf "$OLD"
    printf '\nDeployed. UI: http://%s:%s/  logs: journalctl --user -u dispatch -f\n' "$(hostname)" "$port"
    exit 0
  fi
  sleep 1
done
warn "no healthy response after 30s; previous build kept at $OLD"
systemctl --user status dispatch --no-pager || true
exit 1
