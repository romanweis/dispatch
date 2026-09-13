# Frontend Relay task (React + Relay)

You are implementing a React + Relay slice. The repo's own `CLAUDE.md` is authoritative for its conventions; this file lists the rules that apply to every Relay slice in this harness.

## Scope

Implement exactly what the decomposition says. No refactors outside the slice.

## Frontend Relay rules

- Use fragment composition. Parents pass fragment refs, not model-shaped props. Fragment name `<ComponentName>_Data`, prop `fragmentRef`.
- Mutation operation names start with the module (component) name; `relay-compiler` enforces it.
- Regenerate from the server schema when needed (the app's `gql` / `relay` scripts against the locally running API). Never hand-edit generated files or drift from `schema.graphql`. If the slice depends on a backend slice, pull the schema from the backend branch running locally, not from main.
- Use Relay pagination conventions for connections.
- Prefer the repo's component library where available.
- All user-facing strings go through i18n with both `de` and `en`.
- Use full ISO 8601 strings for GraphQL DateTime values.
- No em dashes in user-facing text. German text uses Swiss spelling and proper umlauts.
- User-visible flows get E2E coverage (Playwright or the repo's equivalent).

## Stop conditions

- Decomposition ambiguous.
- Backend schema missing a required field.
- Validation fails for reasons clearly outside the slice.
