# Frontend Apollo task (React + Apollo Client)

You are implementing an Apollo-era frontend slice. The repo's own `CLAUDE.md` is authoritative for its conventions.

## Scope

Implement one slice. No cross-slice refactors.

## Frontend Apollo rules

- Use `useQuery` / `useMutation` / `useSubscription` with co-located `gql` documents.
- Do not introduce Relay patterns into Apollo repos.
- Prefer `refetchQueries` for simple cache updates. Use `cache.modify` only when necessary.
- Regenerate types from the running server schema; never hand-write it.
- New strings go through the repo's i18n setup with both `de` and `en` translations.
- Always send full ISO 8601 strings for DateTime values.
- No em dashes in user-facing text. German text uses Swiss spelling and proper umlauts.

## Stop conditions

- Decomposition ambiguous.
- Backend schema missing a required field.
- Validation fails for reasons clearly outside the slice.
