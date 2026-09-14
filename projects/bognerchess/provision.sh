#!/usr/bin/env bash
# provision.sh - runs as root inside the fresh bognerchess-base container, after the repos are
# cloned and before provision-user.sh. Installs the toolchain the bognerchess repos need and the
# bc-dev helper. Idempotent: safe to re-run on a rebuilt base.

set -euo pipefail

export DEBIAN_FRONTEND=noninteractive
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_NOLOGO=1

WORKSPACE="${WORKSPACE:-/home/agent/bognerchess}"
DOTNET_CHANNEL="${DOTNET_CHANNEL:-10.0}"
DOTNET_DIR=/usr/share/dotnet
PNPM_VERSION="${PNPM_VERSION:-10.13.1}"   # matches "packageManager" in academy/student package.json

log()  { printf '\n\033[1m[provision] %s\033[0m\n' "$*"; }
warn() { printf '\033[33m[provision] WARN: %s\033[0m\n' "$*" >&2; }

# ---------------------------------------------------------------------------------------------
log "apt packages"
apt-get update -qq
apt-get install -y -qq --no-install-recommends \
  ca-certificates curl wget gnupg jq git unzip xz-utils \
  python3 python3-venv python3-pip python3-dev build-essential \
  libssl3 zlib1g libgcc-s1 libstdc++6 libgssapi-krb5-2 libkrb5-3 \
  postgresql-client >/dev/null

# .NET needs libicu; the package name carries the ICU version, which differs per Ubuntu release.
icu_pkg="$(apt-cache search --names-only '^libicu[0-9]+$' | awk '{print $1}' | sort -V | tail -1 || true)"
if [ -n "$icu_pkg" ]; then
  apt-get install -y -qq --no-install-recommends "$icu_pkg" >/dev/null
else
  warn "no libicu package found; setting DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1 system-wide"
  echo 'DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1' >> /etc/environment
fi

# ---------------------------------------------------------------------------------------------
log ".NET SDK (channel $DOTNET_CHANNEL) -> $DOTNET_DIR"
if [ -x "$DOTNET_DIR/dotnet" ] && "$DOTNET_DIR/dotnet" --list-sdks 2>/dev/null | grep -q "^${DOTNET_CHANNEL%%.*}\."; then
  echo "  already installed: $("$DOTNET_DIR/dotnet" --list-sdks | grep "^${DOTNET_CHANNEL%%.*}\." | tail -1)"
else
  tmp="$(mktemp -d)"
  curl -fsSL https://dot.net/v1/dotnet-install.sh -o "$tmp/dotnet-install.sh"
  bash "$tmp/dotnet-install.sh" --channel "$DOTNET_CHANNEL" --install-dir "$DOTNET_DIR" --no-path
  rm -rf "$tmp"
fi
ln -sfn "$DOTNET_DIR/dotnet" /usr/bin/dotnet
cat > /etc/profile.d/dotnet.sh <<'EOF'
export DOTNET_ROOT=/usr/share/dotnet
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_NOLOGO=1
export PATH="$PATH:$HOME/.dotnet/tools"
EOF
chmod 0644 /etc/profile.d/dotnet.sh
grep -q '^DOTNET_ROOT=' /etc/environment || echo "DOTNET_ROOT=$DOTNET_DIR" >> /etc/environment
echo "  dotnet $(dotnet --version)"

# ---------------------------------------------------------------------------------------------
log "node / corepack / pnpm"
if ! command -v node >/dev/null; then
  warn "node is not installed. agent-base should provide it; installing Node 22 from NodeSource as a fallback"
  curl -fsSL https://deb.nodesource.com/setup_22.x | bash - >/dev/null
  apt-get install -y -qq nodejs >/dev/null
fi
echo "  node $(node --version)"
if command -v corepack >/dev/null; then
  corepack enable >/dev/null 2>&1 || true
  corepack prepare "pnpm@${PNPM_VERSION}" --activate >/dev/null 2>&1 \
    || warn "corepack prepare pnpm@${PNPM_VERSION} failed; pnpm will be resolved per repo from packageManager"
else
  warn "corepack missing; installing pnpm globally with npm"
  npm install -g "pnpm@${PNPM_VERSION}" >/dev/null
fi
if command -v pnpm >/dev/null; then echo "  pnpm $(pnpm --version)"; else warn "pnpm still not on PATH"; fi

# ---------------------------------------------------------------------------------------------
log "Playwright system dependencies (chromium)"
# Installs the shared libraries Chromium needs. The browser binary itself is installed per user
# in provision-user.sh so it lands in uid 1000's cache.
npx -y playwright install-deps chromium >/dev/null 2>&1 \
  || warn "playwright install-deps failed; E2E tests may not run until the libs are installed"

# ---------------------------------------------------------------------------------------------
log "python tooling (venv, pip, uv, ruff)"
python3 -m pip install --quiet --upgrade --break-system-packages pip 2>/dev/null \
  || python3 -m pip install --quiet --upgrade pip 2>/dev/null || true
if ! command -v uv >/dev/null; then
  curl -LsSf https://astral.sh/uv/install.sh | env UV_INSTALL_DIR=/usr/local/bin UV_NO_MODIFY_PATH=1 sh >/dev/null 2>&1 \
    || warn "uv install failed; chess-ai (uv.lock) will fall back to pip"
fi
command -v uv >/dev/null && echo "  uv $(uv --version)"
if ! command -v ruff >/dev/null; then
  if command -v uv >/dev/null; then
    uv tool install ruff >/dev/null 2>&1 && ln -sfn "$(uv tool dir)/ruff/bin/ruff" /usr/local/bin/ruff 2>/dev/null || true
  fi
  command -v ruff >/dev/null || python3 -m pip install --quiet --break-system-packages ruff 2>/dev/null \
    || python3 -m pip install --quiet ruff 2>/dev/null || warn "ruff not installed globally; repos install it in their venv"
fi

# ---------------------------------------------------------------------------------------------
log "docker check"
if command -v docker >/dev/null; then
  echo "  $(docker --version)"
  docker compose version >/dev/null 2>&1 || warn "docker compose plugin missing; bc-dev needs it"
  if id -u agent >/dev/null 2>&1 && getent group docker >/dev/null; then
    usermod -aG docker agent || true
  fi
else
  warn "docker not found. bc-dev (dev Postgres/Redis) and Testcontainers-based backend tests need it."
fi

# ---------------------------------------------------------------------------------------------
log "bc-dev helper -> /usr/local/bin/bc-dev"
cat > /usr/local/bin/bc-dev <<'BCDEV'
#!/usr/bin/env bash
# bc-dev - local dev stack for the bognerchess workspace.
#
#   bc-dev up              start Postgres 16 (127.0.0.1:5431, postgres/1234) and Redis 7 (127.0.0.1:6379)
#   bc-dev migrate [args]  run Stamy.Migrations against the local Postgres (args pass through, e.g. --seed)
#   bc-dev psql [db]       psql as postgres on <db> (default: bognerchess)
#   bc-dev status          docker compose ps
#   bc-dev down            stop the services (data persists in named volumes)
#   bc-dev reset [--yes]   destroy the volumes and start fresh (asks unless --yes)
#
# Files live under $BC_WORKSPACE/.dev (default ~/bognerchess/.dev). Stamy.Migrations reads its
# connection string from $SECRETS_PATH/postgres.txt, so `up` also writes a dev secrets dir.

set -euo pipefail

BC_WORKSPACE="${BC_WORKSPACE:-$HOME/bognerchess}"
DEV_DIR="$BC_WORKSPACE/.dev"
COMPOSE_FILE="$DEV_DIR/docker-compose.yml"
SECRETS_DIR="$DEV_DIR/secrets"
MIGRATIONS_PROJECT="${BC_MIGRATIONS_PROJECT:-$BC_WORKSPACE/backend/Stamy.Migrations}"
PG_PORT=5431
PG_USER=postgres
PG_PASSWORD=1234
PG_DB=bognerchess
REDIS_PORT=6379
CONN="Server=localhost;Database=$PG_DB;Port=$PG_PORT;User Id=$PG_USER;Password=$PG_PASSWORD;Trust Server Certificate=true;Include Error Detail=true"

usage() { sed -n '2,12p' "$0" | sed 's/^# \{0,1\}//'; }
die() { printf 'bc-dev: %s\n' "$*" >&2; exit 1; }
compose() { docker compose -p bognerchess-dev -f "$COMPOSE_FILE" "$@"; }

write_files() {
  mkdir -p "$DEV_DIR" "$SECRETS_DIR"
  chmod 0700 "$SECRETS_DIR"
  cat > "$COMPOSE_FILE" <<YML
services:
  postgres:
    image: postgres:16
    container_name: bognerchess-dev-postgres
    restart: unless-stopped
    environment:
      POSTGRES_USER: $PG_USER
      POSTGRES_PASSWORD: "$PG_PASSWORD"
      POSTGRES_DB: $PG_DB
    ports:
      - "127.0.0.1:$PG_PORT:5432"
    volumes:
      - pgdata:/var/lib/postgresql/data
    healthcheck:
      test: ["CMD-SHELL", "pg_isready -U $PG_USER"]
      interval: 2s
      timeout: 3s
      retries: 30
  redis:
    image: redis:7
    container_name: bognerchess-dev-redis
    restart: unless-stopped
    ports:
      - "127.0.0.1:$REDIS_PORT:6379"
    volumes:
      - redisdata:/data
volumes:
  pgdata:
  redisdata:
YML
  printf '%s' "$CONN" > "$SECRETS_DIR/postgres.txt"
  printf 'dev-only-placeholder-key-not-a-secret' > "$SECRETS_DIR/stamy_symmetric_private_key.txt"
  chmod 0600 "$SECRETS_DIR"/*.txt
}

need_docker() {
  command -v docker >/dev/null || die "docker not found"
  docker compose version >/dev/null 2>&1 || die "docker compose plugin not found"
}

wait_pg() {
  local i
  for i in $(seq 1 60); do
    if compose exec -T postgres pg_isready -U "$PG_USER" >/dev/null 2>&1; then return 0; fi
    sleep 1
  done
  die "postgres did not become ready on 127.0.0.1:$PG_PORT"
}

cmd_up() {
  need_docker
  write_files
  compose up -d
  wait_pg
  echo "bc-dev: postgres 127.0.0.1:$PG_PORT ($PG_USER/$PG_PASSWORD, db $PG_DB), redis 127.0.0.1:$REDIS_PORT"
  echo "bc-dev: secrets for Stamy.Migrations in $SECRETS_DIR (SECRETS_PATH)"
}

cmd_migrate() {
  command -v dotnet >/dev/null || die "dotnet not found"
  [ -d "$MIGRATIONS_PROJECT" ] || die "migrations project not found: $MIGRATIONS_PROJECT"
  [ -f "$SECRETS_DIR/postgres.txt" ] || write_files
  if command -v docker >/dev/null && docker compose version >/dev/null 2>&1 && [ -f "$COMPOSE_FILE" ]; then
    compose exec -T postgres pg_isready -U "$PG_USER" >/dev/null 2>&1 || { echo "bc-dev: postgres not running, starting it"; cmd_up; }
  fi
  echo "bc-dev: running Stamy.Migrations against $PG_DB@127.0.0.1:$PG_PORT $*"
  SECRETS_PATH="$SECRETS_DIR" ASPNETCORE_ENVIRONMENT="${ASPNETCORE_ENVIRONMENT:-Development}" \
    dotnet run --no-launch-profile --project "$MIGRATIONS_PROJECT" -- --connection "$CONN" "$@"
}

cmd_psql() {
  need_docker
  local db="${1:-$PG_DB}"
  compose exec -e PGPASSWORD="$PG_PASSWORD" postgres psql -U "$PG_USER" -d "$db"
}

cmd_status() { need_docker; [ -f "$COMPOSE_FILE" ] || die "not set up; run bc-dev up"; compose ps; }

cmd_down() { need_docker; [ -f "$COMPOSE_FILE" ] || die "not set up; run bc-dev up"; compose down; }

cmd_reset() {
  need_docker
  [ -f "$COMPOSE_FILE" ] || write_files
  if [ "${1:-}" != "--yes" ] && [ "${1:-}" != "-y" ]; then
    if [ -t 0 ]; then
      read -r -p "bc-dev: destroy dev Postgres and Redis volumes? [y/N] " ans
      [ "$ans" = "y" ] || [ "$ans" = "Y" ] || { echo "aborted"; exit 1; }
    else
      die "refusing to reset without --yes in a non-interactive shell"
    fi
  fi
  compose down -v
  cmd_up
}

case "${1:-}" in
  up)       shift; cmd_up "$@" ;;
  migrate)  shift; cmd_migrate "$@" ;;
  psql)     shift; cmd_psql "$@" ;;
  status)   shift; cmd_status "$@" ;;
  down)     shift; cmd_down "$@" ;;
  reset)    shift; cmd_reset "$@" ;;
  -h|--help|help|"") usage ;;
  *) printf 'bc-dev: unknown command: %s\n\n' "$1" >&2; usage >&2; exit 1 ;;
esac
BCDEV
chmod 0755 /usr/local/bin/bc-dev
bash -n /usr/local/bin/bc-dev
echo "  installed"

# ---------------------------------------------------------------------------------------------
log "workspace ownership"
if [ -d "$WORKSPACE" ]; then
  chown -R 1000:1000 "$WORKSPACE" || warn "chown of $WORKSPACE failed"
fi

log "provision.sh done"
printf '  dotnet   %s\n' "$(dotnet --version 2>/dev/null || echo missing)"
printf '  node     %s\n' "$(node --version 2>/dev/null || echo missing)"
printf '  pnpm     %s\n' "$(pnpm --version 2>/dev/null || echo missing)"
printf '  python   %s\n' "$(python3 --version 2>/dev/null || echo missing)"
printf '  uv       %s\n' "$(uv --version 2>/dev/null || echo missing)"
printf '  docker   %s\n' "$(docker --version 2>/dev/null || echo missing)"
printf '  bc-dev   %s\n' "$(command -v bc-dev || echo missing)"
