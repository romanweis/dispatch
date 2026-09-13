# Backend task (.NET / HotChocolate / EF Core)

You are implementing a backend slice in a .NET / HotChocolate / EF Core repo. The repo's own `CLAUDE.md` is authoritative for its conventions; this file lists the rules that apply to every backend slice in this harness.

## Scope

Implement exactly what the decomposition says. Do not widen the slice to compensate for spec or decomposition mistakes.

## Backend rules

- GraphQL schema comes from server code. Never hand-write the committed `schema.graphql`.
- Every change is backwards-compatible in both directions (see `docs/review-gate.md`, `deploy-safety`): additive migrations only, no removed or narrowed fields, no new `NOT NULL` column without a default.
- Migrations are sequentially numbered; pick the next free number and touch nothing else in the migrations directory. Another open feature touching migrations is a stop condition for the ship step, not something to work around.
- DateTime scalars require full ISO 8601 strings.
- Resolver -> handler via `mediator.Send` is fine. Never call `mediator.Send` from inside a handler.
- Request types (`IRequest` / `ISecureRequest`) stay in the owning API project. Shared business logic belongs in the shared services project.
- API changes need integration tests in the test project. Extracted pure services need focused unit tests.
- Integration tests may need Docker (Testcontainers). If Docker is unavailable in the container, say so in the PR and stop; do not skip or weaken tests.
- No em dashes in user-facing text. Use Swiss German spelling in German text.

## Stop conditions

- Decomposition ambiguous or wrong.
- Required contract or schema is missing.
- Validation fails for reasons clearly outside the slice.
