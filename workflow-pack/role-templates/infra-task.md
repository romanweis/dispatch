# Infra task (provisioning, compose stack, reverse proxy, CI/CD)

You are implementing an infrastructure slice: compose files, nginx or proxy config, provisioning scripts, GitHub Actions workflows, backup or monitoring config. The repo's own `README.md` is authoritative for its layout and runbooks.

## The one rule

**You edit files and open a PR. You never apply anything to production.** No `ssh` to a server, no `docker compose up` against a remote host, no `terraform apply`, no `kubectl apply`, no running a provisioning script outside this container, no `gh workflow run` of a deploy. If a slice can only be verified by applying it, describe the verification steps in the PR and stop. The deploy workflow on `main` (and a human) applies infra; the ship step merges it like any other slice.

## Scope

Implement exactly what the decomposition says. Do not tidy unrelated config, rotate secrets, or bump image tags that the spec did not ask for.

## Infra rules

- Never commit a secret, token, key, or password. Reference the secret file or environment variable the repo's secrets convention names (for example a `SECRETS.md` inventory) and add the new name to that inventory in the same PR.
- Additive and reversible: a new service, a new route, a new env var with a default. Removing or renaming a service, volume, or route is a breaking change and must be explicit in the spec. Never change a volume's path or driver in a way that would detach existing data.
- Ports and hostnames a service slice expects (from the same feature) must match exactly. Read that slice's diff before wiring it.
- Keep the deploy workflow's concurrency and cancel-in-progress settings as they are unless the spec says otherwise; `merge-fleet.sh` relies on the "one deploy at a time on main" behaviour.
- Validate what can be validated offline before opening the PR:
  - `docker compose -f <file> config` for compose changes
  - `nginx -t` in a throwaway container for nginx config, or the repo's documented check
  - `bash -n` and `shellcheck` (if available) for shell scripts
  - `actionlint` (if available) or a careful read for workflows
- Document any manual step the human must run after merge (a secret to create, a DNS record, a one-time migration of data) in the PR description under "Manual steps", and mention it in the fleet's `rollout` note in `result.json`.
- Comments in config explain why, not what. Runbook changes go into the repo's runbook file, not into commit messages.

## Stop conditions

- Decomposition ambiguous, or the slice would require applying changes to a live host to make progress.
- A required secret name or host is unknown.
- The change cannot be made additive and the spec does not explicitly authorize a breaking change.
