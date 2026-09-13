# infra/ — server setup and container images

Everything here runs **on the Linux server** (Ubuntu 24.04, user `roman`, repo cloned at `~/dispatch`). Scripts are re-runnable; each prints `==> step` / `ok` / `skip` / `WARN` lines.

| File | Purpose |
|---|---|
| `setup-server.sh` | One-time host setup: apt packages, Incus (ZFS loop pool + `incusbr0`), profile, secrets dirs, PostgreSQL, .NET 10 SDK, Node 22 + pnpm, ufw, user systemd unit |
| `build-agent-base.sh` | Builds the Incus image alias `agent-base` (Ubuntu 24.04 + user `agent` uid 1000 + Node 22 + Claude Code + gh + Docker CE) |
| `build-project-base.sh <project>` | Builds the stopped container `<project>-base` from `agent-base` + `projects/<project>/project.yaml` (repos cloned, orchestrator repo created/rendered, provision scripts run) |
| `deploy.sh` | `dotnet publish` the API, `pnpm build` the UI, swap into `~/.local/opt/dispatch`, restart the service, check `/healthz` |
| `agent.profile.yaml` | Incus profile `dispatch-agent` (nesting, limits, shifted bind mounts for Claude + gh credentials) |
| `dispatch.service` | systemd **user** unit for the API (`systemctl --user ... dispatch`) |

## Prerequisites

- Tailscale up on the server (`tailscale status`); the UI is reached over `tailscale0` on port 9300.
- `gh auth login` done as roman (HTTPS; needs `repo` + `workflow` scopes, and access to the GitHub orgs named in `projects/*/project.yaml`). `setup-server.sh` copies `~/.config/gh/{hosts.yml,config.yml}` into the shared secrets dir and `build-project-base.sh` uses `gh auth token` for cloning and for creating orchestrator repos.
- Claude Code logged in on the host (`~/.claude/.credentials.json`, `~/.claude.json` exist).
- Password sudo for roman. Scripts never assume passwordless sudo; run them in an interactive terminal.

## Order of operations

```bash
cd ~/dispatch
infra/setup-server.sh                 # 1. host setup; prompts for sudo
# log out / log in (activates incus-admin for new shells AND for the user systemd manager)
gh auth login && infra/setup-server.sh   # only if gh was not logged in the first time
infra/build-agent-base.sh             # 2. image alias agent-base (10-15 min, downloads a lot)
infra/build-project-base.sh bognerchess   # 3. once per projects/<name>/
infra/deploy.sh                       # 4. build + start the API/UI
```

Then open `http://<server-tailscale-ip>:9300/`.

`setup-server.sh` re-executes itself under `sg incus-admin` the first time so it can finish in one go, but the `dispatch` service (a user unit) only sees the new group after roman's user session is restarted: log out/in or `sudo systemctl restart user@1000.service` (this kills roman's running user services) or reboot.

### Configuration written by setup

- `/srv/dispatch/.env` (mode 600) — `DISPATCH_DB`, `DISPATCH_DB_PASSWORD`, `DISPATCH_BIND_URLS`, `DISPATCH_PROJECTS_DIR`, `DISPATCH_REPO_DIR`, `DISPATCH_INCUS_BRIDGE`, `DISPATCH_INCUS_PROFILE`, `Dispatch__ProjectsDir`. Existing keys are never overwritten; edit by hand and `systemctl --user restart dispatch`.
- `/srv/dispatch/secrets/claude/` — mounted at `/home/agent/.claude` in every agent container (credentials **and** Claude's session state, shared by all containers).
- `/srv/dispatch/secrets/claude-json/.claude.json` — pushed to `/home/agent/.claude.json` by `build-project-base.sh`.
- `/srv/dispatch/secrets/gh/` — mounted at `/home/agent/.config/gh`.
- Pool size: `INCUS_POOL_SIZE=600GiB infra/setup-server.sh` (default 400GiB; only used on first init).

## Rebuilding

- **Agent image**: `infra/build-agent-base.sh` replaces the `agent-base` alias. Existing `<p>-base` containers keep the old rootfs; rebuild them to pick up the new image.
- **Project base**: `infra/build-project-base.sh <project>` deletes and recreates `<project>-base`. Running ticket containers (`t-<id>`) are independent copies and are not touched. The orchestrator repo is only rendered from `workflow-pack/` when it does not exist on GitHub or has no commits; otherwise it is just cloned.
- **API/UI**: `infra/deploy.sh` (safe to run while the service is up; it swaps directories and rolls back if the new build fails to start).

## Inspecting a ticket container

```bash
incus list                                                    # t-12 etc.
incus exec t-12 --user 1000 --group 1000 --env HOME=/home/agent --cwd /home/agent -- bash
incus exec t-12 -- bash                                       # as root
incus file pull t-12/home/agent/<workspace>/orchestrator/tasks/12/state.json -
```

### Attaching to the ticket's Claude session

The API runs `claude` non-interactively (stream-json) with a fixed session id; the ticket detail (`attachCommand` in `GET /api/tickets/{id}`) shows the exact command. Manually:

```bash
incus exec t-12 --user 1000 --group 1000 --env HOME=/home/agent --cwd <workspace> -- \
  claude --resume <claudeSessionId>
```

Do this only while no run is active on the ticket (the board shows `activeRunId`); two processes on one session id corrupt the transcript. A read-only alternative is the run event stream in the UI (`/api/runs/{id}/stream`).

Ad-hoc test container from a base: `incus copy bognerchess-base t-test && incus start t-test`, remove with `incus delete -f t-test`.

## Troubleshooting

- **`incus: permission denied` / "not authorized"** — the shell does not have the `incus-admin` group yet. `id -nG` should list it; if not, log out and in (or `newgrp incus-admin` for one shell). The user systemd manager needs a full re-login or `sudo systemctl restart user@1000.service`.
- **ZFS pool / disk space** — the pool is a loop file at `/var/lib/incus/disks/default.img` (sparse, grows up to `INCUS_POOL_SIZE`). `incus storage info default` shows usage; `sudo zpool list default`. Growing later: `incus storage set default size=600GiB` then `sudo zpool online -e default /var/lib/incus/disks/default.img`.
- **`modprobe zfs` fails** — install `linux-modules-extra-$(uname -r)` (the desktop kernel normally has it) and reboot.
- **Docker inside a container fails to start** — needs `security.nesting=true` plus the two `security.syscalls.intercept.*` keys; both come from the `dispatch-agent` profile (`incus config show t-12 --expanded | grep security`). Overlay2 on the ZFS-backed rootfs requires OpenZFS >= 2.2 (24.04 has 2.2.x); the image builder falls back to `fuse-overlayfs` in `/etc/docker/daemon.json` when the default driver will not start. Check with `incus exec t-12 -- docker info --format '{{.Driver}}'`.
- **`/home/agent/.claude` shows `nobody` ownership in a container** — `shift: "true"` on the profile disk devices did not take effect. Requires idmapped-mount support (kernel >= 5.12, ext4/xfs source, which the target has) and that `/srv/dispatch/secrets/*` is owned by uid 1000 on the host. Verify with `incus exec t-12 -- stat -c '%U' /home/agent/.claude`.
- **Claude in a container asks to log in / onboard** — `/srv/dispatch/secrets/claude/.credentials.json` missing or expired: log in with `claude` on the host and re-run `infra/setup-server.sh` (copies with `cp -u`, so a newer file written by a container is kept). `~/.claude.json` in the container comes from `/srv/dispatch/secrets/claude-json/`.
- **`gh` in a container is not authenticated** — `gh auth status` on the host, then re-run `setup-server.sh`; the token in `hosts.yml` is shared with all containers via the bind mount.
- **Containers cannot reach the API** — `DISPATCH_URL` uses the `incusbr0` gateway address (`incus network get incusbr0 ipv4.address`). If ufw is active, `setup-server.sh` added an allow rule for port 9300 on `incusbr0`; check `sudo ufw status`.
- **`dispatch` service will not start** — `journalctl --user -u dispatch -n 100`. Common causes: `.env` DB password out of sync (re-run `setup-server.sh`, it re-applies the password from `.env` to the role), `~/.dotnet` missing (`DOTNET_ROOT`), or the incus-admin group not yet visible to the user manager (see above).
- **Postgres** — `sudo -u postgres psql -c '\du'`, `psql "postgresql://dispatch:<pw>@127.0.0.1:5432/dispatch"` with the password from `/srv/dispatch/.env`.
