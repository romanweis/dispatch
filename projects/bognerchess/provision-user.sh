#!/usr/bin/env bash
# provision-user.sh - runs as uid 1000 inside the bognerchess-base container after provision.sh.
# Warms every repo so ticket containers (copies of this base) start with dependencies restored,
# a running dev database, and the schema migrated. Every step is tolerant: a failure is reported
# in the summary, not fatal, so one flaky download does not leave the base half-built.

set -euo pipefail

export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_NOLOGO=1
export DOTNET_ROOT="${DOTNET_ROOT:-/usr/share/dotnet}"
export PATH="$PATH:$DOTNET_ROOT:$HOME/.dotnet/tools"
export CI=1   # pnpm/playwright: no prompts, no progress bars

WORKSPACE="${WORKSPACE:-$HOME/bognerchess}"
FRONTENDS=(academy student trainer tournaments)
declare -A RESULT=()

log()  { printf '\n\033[1m[provision-user] %s\033[0m\n' "$*"; }
ok()   { RESULT["$1"]="ok${2:+ ($2)}"; }
fail() { RESULT["$1"]="FAILED${2:+: $2}"; printf '\033[33m[provision-user] WARN: %s %s\033[0m\n' "$1" "${2:-}" >&2; }

[ -d "$WORKSPACE" ] || { echo "workspace $WORKSPACE not found" >&2; exit 1; }

# ---------------------------------------------------------------------------------------------
log "bc-dev up (postgres 5431, redis 6379)"
if command -v bc-dev >/dev/null; then
  if bc-dev up; then ok "bc-dev up"; else fail "bc-dev up" "docker unavailable or compose failed"; fi
else
  fail "bc-dev up" "bc-dev not installed (provision.sh did not run?)"
fi

# ---------------------------------------------------------------------------------------------
log "pnpm install in frontends"
for app in "${FRONTENDS[@]}"; do
  dir="$WORKSPACE/$app"
  if [ ! -d "$dir" ]; then fail "pnpm $app" "directory missing"; continue; fi
  if [ ! -f "$dir/pnpm-lock.yaml" ]; then fail "pnpm $app" "no pnpm-lock.yaml, skipped"; continue; fi
  if ! command -v pnpm >/dev/null && ! command -v corepack >/dev/null; then fail "pnpm $app" "pnpm missing"; continue; fi
  # corepack honours the repo's "packageManager" field when present, so the pinned pnpm is used.
  if (cd "$dir" && pnpm install --frozen-lockfile --prefer-offline >/dev/null 2>&1); then
    ok "pnpm $app" "frozen lockfile"
  elif (cd "$dir" && pnpm install >/dev/null 2>&1); then
    ok "pnpm $app" "lockfile not frozen; check pnpm-lock.yaml drift"
  else
    fail "pnpm $app" "pnpm install failed"
  fi
done

# ---------------------------------------------------------------------------------------------
log "dotnet restore backend"
SLN="$WORKSPACE/backend/BognerChess.sln"
if [ -f "$SLN" ]; then
  if (cd "$WORKSPACE/backend" && dotnet restore "$SLN" >/dev/null); then ok "dotnet restore"; else fail "dotnet restore" "see output above"; fi
else
  fail "dotnet restore" "$SLN not found"
fi

# ---------------------------------------------------------------------------------------------
log "playwright chromium"
# Prefer the Playwright version the repos pin so the browser build matches @playwright/test.
pw_dir=""
for app in "${FRONTENDS[@]}"; do
  if [ -d "$WORKSPACE/$app/node_modules/@playwright/test" ]; then pw_dir="$WORKSPACE/$app"; break; fi
done
if [ -n "$pw_dir" ] && (cd "$pw_dir" && pnpm exec playwright install chromium >/dev/null 2>&1); then
  ok "playwright chromium" "via $(basename "$pw_dir")'s pinned version"
elif npx -y playwright install chromium >/dev/null 2>&1; then
  ok "playwright chromium" "via npx (latest)"
else
  fail "playwright chromium" "install failed"
fi

# ---------------------------------------------------------------------------------------------
log "python venvs (best effort)"
for app in chess-ai opening-trainer; do
  dir="$WORKSPACE/$app"
  [ -f "$dir/pyproject.toml" ] || { fail "python $app" "no pyproject.toml"; continue; }
  if [ -f "$dir/uv.lock" ] && command -v uv >/dev/null; then
    if (cd "$dir" && uv sync --frozen >/dev/null 2>&1); then ok "python $app" "uv sync"; else fail "python $app" "uv sync failed"; fi
  else
    if (cd "$dir" && python3 -m venv .venv >/dev/null 2>&1 && . .venv/bin/activate && pip install --quiet -e '.[dev]' >/dev/null 2>&1); then
      ok "python $app" "venv + pip -e .[dev]"
    elif (cd "$dir" && . .venv/bin/activate 2>/dev/null && pip install --quiet -e . >/dev/null 2>&1); then
      ok "python $app" "venv + pip -e . (no dev extra)"
    else
      fail "python $app" "venv setup failed"
    fi
  fi
done

# ---------------------------------------------------------------------------------------------
log "bc-dev migrate"
if [ "${RESULT["bc-dev up"]:-}" = "ok" ] && [ "${RESULT["dotnet restore"]:-}" = "ok" ]; then
  if bc-dev migrate; then ok "bc-dev migrate"; else fail "bc-dev migrate" "run 'bc-dev migrate' by hand in the ticket container"; fi
else
  fail "bc-dev migrate" "skipped (dev stack or restore not ok)"
fi

# ---------------------------------------------------------------------------------------------
log "summary"
printf '  %-22s %s\n' "dotnet"   "$(dotnet --version 2>/dev/null || echo missing)"
printf '  %-22s %s\n' "node"     "$(node --version 2>/dev/null || echo missing)"
printf '  %-22s %s\n' "pnpm"     "$(pnpm --version 2>/dev/null || echo missing)"
for k in "bc-dev up" "pnpm academy" "pnpm student" "pnpm trainer" "pnpm tournaments" "dotnet restore" \
         "playwright chromium" "python chess-ai" "python opening-trainer" "bc-dev migrate"; do
  printf '  %-22s %s\n' "$k" "${RESULT[$k]:-not run}"
done

failed=0
for v in "${RESULT[@]}"; do case "$v" in FAILED*) failed=$((failed + 1));; esac; done
if [ "$failed" -gt 0 ]; then
  printf '\n  %d step(s) did not complete. The base container is usable; fix them on first use or rebuild.\n' "$failed"
fi
exit 0
