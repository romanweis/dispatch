# BognerChess workspace

Standalone BognerChess platform, extracted from the Stamy platform. Every repo of the `bognerchess` GitHub org is cloned as a direct child of this directory (`~/bognerchess/<repo>`). The master plan (context, decisions, migration path) is `infra/PLAN.md`.

## Workspace map

| Dir | What | Serves |
|---|---|---|
| `backend` | .NET 10 backend: Manage API, Web API, JobHost, Migrations, EmailDefaultsSync, Tests, email-renderer, vosk image (`BognerChess.sln`) | api. / web.bognerchess.com |
| `academy` | Astro public site + /account portal (React Relay islands) | www.bognerchess.com |
| `student` | Student training SPA (React + Relay) | student.bognerchess.com |
| `trainer` | Trainer SPA incl. the owner back-office at `/admin` (OWNER role) | trainer.bognerchess.com |
| `tournaments` | Tournament organiser SPA (React + Relay) | tournaments.bognerchess.com |
| `chess-ai` | Coaching analysis API (Python, uv) | internal :8000 |
| `opening-trainer` | Opening puzzle API + RQ workers (Python) | openings.bognerchess.com |
| `infra` | Server provisioning, compose stack, nginx, Keycloak, backups, cutover runbook, SECRETS.md | the Hetzner server |
| `orchestrator` | Feature orchestration: specs, per-ticket state, `/start-feature`, `/review-feature`, `/ship-feature` | nothing; it is the harness |

`manage` was retired 2026-08-29 (features merged into `trainer` `/admin`). It is not cloned here and not a valid slice target.

Which API a client talks to: `trainer` and `tournaments` use the Manage API (`api.bognerchess.com`, local :5000); `academy` and `student` use the Web API (`web.bognerchess.com`, local :5200). The `gql` script in each app's `package.json` pulls the matching schema from the local API.

## Where the orchestrator lives

`~/bognerchess/orchestrator`. Its `CLAUDE.md` is the session contract for feature work (decomposition, PR conventions, review gate, shipping, and how to talk to the human through the `ticket` CLI). Feature specs live in `orchestrator/features/<ticket-id>-<slug>.md`, per-feature state in `orchestrator/tasks/<ticket-id>/`. Read it before running any `/start-feature`, `/review-feature` or `/ship-feature` command; those commands only exist inside that directory.

## Conventions carried over from the Stamy platform

Namespaces stay `Stamy.*` in the backend (a deliberate no-churn decision, PLAN.md §2). Do not rename them.

### GraphQL / HotChocolate

- DateTime scalars require full ISO 8601 strings (`new Date(...).toISOString()`), never partial formats.
- Never invent schema. Generate `schema.graphql` from the running server (`pnpm gql` in the app, with the API running locally).
- Pull the latest schema from the local API before client work that involves backend changes. Stale schemas let Relay codegen succeed against shapes the server no longer has, and the error only surfaces at runtime.
- Mutations return `IQueryable<TEntity>` with `[UseProjection]` and `[UseFirstOrDefault]`, never bare primitives. GraphQL field names are descriptive (`consultant`, `booking`), never `id`, `result`, `value`.
- Read resolvers never inject the request-scoped `StamyDbContext`; use `IDbContextFactory` + `resolverContext.CreateResolverDbContext(dbFactory)`. Mutation resolvers and MediatR handlers keep the request-scoped context. Enforced by `Stamy.Tests/ResolverDbContextConventionTests.cs`.
- Resolvers that gate on a `[Parent]` field need `[IsProjected(true)]` on that field.
- `mediator.Send` only from resolvers, never from inside a handler. Shared logic goes into `Stamy.Common.Modules.*.Services`. `IRequest` / `ISecureRequest` types live in the owning API project, never in `Stamy.Common`.

### Relay (academy islands, student, trainer, tournaments)

- Fragment composition. Every data-reading component declares `fragment <ComponentName>_Data on <Type>` and reads it with `useFragment`. Parents pass `fragmentRef` (`<ComponentName>_Data$key`), never model-typed props.
- Mutation operation names start with the module (component) name; `relay-compiler` enforces it.
- Page queries spread child fragments; they never re-select fields the children own.
- Mocking a mutation with `... on Error { message }` in tests needs `__isError: '<TypeName>'` next to `__typename`.

### Writing style

- No em dashes in user-facing text (translations, component text, titles, page metadata). Use periods, commas, colons, or parentheses. Page titles use `|` as separator.
- Swiss German spelling: `ss` instead of `ß`, always proper umlauts (ä, ö, ü), never `ae`/`oe`/`ue`.

### Testing policy

Always be honest about tests. Never cheat to make a test pass.

- Backend: integration tests in `Stamy.Tests` are mandatory for every new mutation, query, or handler; cover the happy path and the failure modes the handler guards (auth, validation, not-found, conflict). Pure services under `Stamy.Common.Modules.*.Services` get focused unit tests. No new `CS86xx` nullable warnings in `Stamy.Tests`; use Shouldly's `ShouldNotBeNull()` return value instead of `!`.
- Backend coverage gates are tiered and enforced in CI (`scripts/check-coverage-thresholds.sh`). Never lower a gate to make CI green.
- Frontend: Playwright E2E coverage for every user-visible flow you add or change. Component tests are optional and complement E2E.
- Python (`chess-ai`, `opening-trainer`): `ruff` clean, `pytest` green, deterministic tests.

## Dev stack (`bc-dev`)

`/usr/local/bin/bc-dev` manages a small Compose stack under `~/bognerchess/.dev/`:

| Command | What it does |
|---|---|
| `bc-dev up` | Start Postgres 16 on `127.0.0.1:5431` (user `postgres`, password `1234`, database `bognerchess`) and Redis 7 on `127.0.0.1:6379`. Writes the compose file and a dev secrets dir (`~/bognerchess/.dev/secrets/postgres.txt`) on first run. Already run when the base container was built. |
| `bc-dev migrate [--seed]` | Run `dotnet run --project ~/bognerchess/backend/Stamy.Migrations` against the local Postgres with `SECRETS_PATH` pointing at the dev secrets. Creates the database if missing and validates the schema afterwards. Run again after pulling backend changes that add migrations. `--seed` loads demo data. |
| `bc-dev psql [db]` | `psql` as postgres on the given database (default `bognerchess`). |
| `bc-dev status` | `docker compose ps`. |
| `bc-dev down` | Stop the services; data persists in named volumes. |
| `bc-dev reset --yes` | Destroy the volumes and start fresh. Use when migrations diverged. |

Booting an API for schema pulls or manual testing: `cd ~/bognerchess/backend && dotnet run --project Stamy.Manage.Api` (:5000) or `dotnet run --project Stamy.Web.Api` (:5200), with `ASPNETCORE_ENVIRONMENT=Development` (set in every ticket container). `appsettings.Development.json` already points at `localhost:5431`.

Integration tests (`dotnet test BognerChess.sln`) use Testcontainers and spin up their own ephemeral Postgres, Keycloak, and RabbitMQ. They need Docker in the container and do not touch the `bc-dev` database.

Frontends: `pnpm install` is already done in `academy`, `student`, `trainer`, `tournaments`. `pnpm dev`, `pnpm build`, `pnpm test:e2e` (Playwright, Chromium installed), `pnpm gql` to refresh `schema.graphql` from the local API, `pnpm relay` to regenerate artifacts.

## Test accounts

Placeholder. When Dispatch provisions test accounts for this project they will be listed at `~/bognerchess/.dev/test-accounts.txt` (mode 0600, never committed) as `email: / password: / description:` blocks per role (trainer, student, tournament organiser, owner, customer). Until that file exists, `bc-dev migrate --seed` creates the single demo account described in `backend/README.md`; use it for manual verification and E2E logins against the local stack.

## Deployment

Server, compose stack, nginx, and secrets inventory live in `infra/` (`infra/README.md`, `infra/SECRETS.md`, `infra/cutover/RUNBOOK.md`). Merging to `main` in any repo runs its `deploy.yml`, which deploys to production. Never apply infra changes from a ticket container; open a PR (see `orchestrator/role-templates/infra-task.md`).
