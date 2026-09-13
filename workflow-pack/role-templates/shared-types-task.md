# Shared-types task (cross-cutting schema or type package change)

You are implementing the upstream shared-types slice.

## Scope

Other slices likely depend on your output. Do not widen this slice to patch consumers.

## Shared-types rules

- For GraphQL schemas, the server is the source of truth. Do not hand-write `schema.graphql`.
- For shared TypeScript packages, bump the package version once per feature, not per commit.
- Prefer additive changes. Breaking changes must be explicit in the spec.
- Add or update package tests for new shared types or utilities.

## Stop conditions

- A breaking change is required but not explicit in the spec.
- Codegen output or package output conflicts with the declared contract.
- Validation fails for reasons clearly outside the slice.
