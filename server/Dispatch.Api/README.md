# Dispatch.Api

ASP.NET Core minimal API (.NET 10) implementing `docs/api.md`: REST + SSE, the ticket state machine,
the run queue that spawns `claude` inside per-ticket Incus containers, and the incus driver.

## Configuration

Section `Dispatch` in `appsettings*.json`, overridable by `--Dispatch:Key=value` on the command line,
`Dispatch__Key` environment variables, or these plain environment variables:

| Key | Env | Default | Meaning |
|---|---|---|---|
| `ConnectionString` | `DISPATCH_DB` | – | Npgsql connection string (required) |
| `BindUrls` | `DISPATCH_BIND_URLS` | `http://0.0.0.0:9300` | Kestrel URLs, `;` separated |
| `ProjectsDir` | `DISPATCH_PROJECTS_DIR` | `../../projects` | folder with `<name>/project.yaml` |
| `RepoDir` | `DISPATCH_REPO_DIR` | `../..` | repo root; `cli/ticket` is pushed from here |
| `IncusBridge` | `DISPATCH_INCUS_BRIDGE` | `incusbr0` | interface whose IPv4 becomes `DISPATCH_URL` |
| `IncusProfile` | `DISPATCH_INCUS_PROFILE` | `dispatch-agent` | profile added to ticket containers |
| `IncusExecutable` | `DISPATCH_INCUS_EXECUTABLE` | `incus` | |
| `PublicUrlForContainers` | `DISPATCH_PUBLIC_URL` | computed | override for `DISPATCH_URL` |
| `FakeIncus` | `DISPATCH_FAKE_INCUS` | `false` | in-memory incus + simulated claude |
| `FakeClaude` | `DISPATCH_FAKE_CLAUDE` | – | path to `fake-claude.dll` to run instead of simulating |

Relative paths resolve against the process working directory (`dotnet run` uses the project directory,
so the defaults point at the repo root).

## Local development (Windows, no incus)

The API needs PostgreSQL; run one in Docker:

```sh
docker run -d --name dispatch-pg -e POSTGRES_PASSWORD=dispatch -e POSTGRES_USER=dispatch -e POSTGRES_DB=dispatch -p 5433:5432 postgres:16
```

`appsettings.Development.json` already carries the matching connection string
(`Host=localhost;Port=5433;Database=dispatch;Username=dispatch;Password=dispatch`). Migrations are
applied automatically at startup. Then, from the repo root:

```sh
dotnet run --project server/Dispatch.Api -- --Dispatch:FakeIncus=true --Dispatch:ProjectsDir=../../projects
```

`FakeIncus` keeps container state in memory, `PullFile` returns null and every run is simulated:
a `system/init` event with a random `session_id`, one `assistant` text event ("fake run") and a
`result` event, after which the run completes and the outcome rule is applied. To exercise the real
process plumbing instead, build `server/Dispatch.Claude.Tests/FakeClaude` and point at it:

```sh
dotnet build server/Dispatch.Claude.Tests/FakeClaude
dotnet run --project server/Dispatch.Api -- --Dispatch:FakeIncus=true --Dispatch:FakeClaude=../Dispatch.Claude.Tests/FakeClaude/bin/Debug/net10.0/fake-claude.dll
```

The UI dev server can proxy to `http://localhost:9300`. The production build is copied to `wwwroot/`
by `infra/deploy.sh`; the API serves it with an SPA fallback for every non-`/api` path.

Quick smoke test:

```sh
curl localhost:9300/healthz
curl localhost:9300/api/projects
curl -X POST localhost:9300/api/tickets -H 'content-type: application/json' -d '{"projectId":1,"title":"Hello","body":"world"}'
curl -X POST localhost:9300/api/tickets/1/refine
curl -N localhost:9300/api/runs/1/stream
```

## Migrations

`dotnet-ef` is pinned in the repo tool manifest (`.config/dotnet-tools.json`):

```sh
dotnet tool restore
dotnet ef migrations add <Name> --project server/Dispatch.Api
```

No database is needed to add a migration (`DesignTimeDbContextFactory` builds an Npgsql context).

## Tests

```sh
dotnet test Dispatch.slnx
```

`Dispatch.Api.Tests` runs the state machine, prompt renderer, slug, run-outcome and CLI auth tests on
Sqlite in-memory with `FakeIncusService`; `jsonb` columns are plain TEXT there (the column type is only
set to `jsonb` when the provider is Npgsql).

## Layout

- `Data/` entities, `DispatchDbContext` (snake_case names, string enums, jsonb via JsonDocument converter), design-time factory
- `Migrations/` EF Core migrations (applied at startup)
- `Config/` `project.yaml` model, loader (hosted service), in-memory registry
- `Incus/` `IIncusService`, `IncusService` (real, `ProcessStartInfo`), `FakeIncusService`, `PublicUrlResolver`
- `Services/` `TicketService` (state machine), `RunQueue`, `ClaudeLauncher`, `RunOutcome`, `PromptRenderer`, `SlugGenerator`, `EventBus`, DTOs
- `Endpoints/` route groups, SSE writer, bearer auth filter, error mapping
