# Astro task (static site, optionally with React Relay islands)

You are implementing an Astro slice. The repo's own `CLAUDE.md` is authoritative for its conventions.

## Scope

Respect Astro's static-first shape: prefer `.astro` pages over client islands unless interactivity requires them.

## Astro rules

- Prefer `.astro` pages for static content and only add React islands where interactivity is required.
- Use the lightest hydration directive that fits the feature. Do not default everything to `client:load`.
- Relay islands follow the Relay rules: fragment composition, generated files from the server schema, no hand edits.
- New public pages need proper titles, meta descriptions, and open-graph tags.
- Localized Astro repos need both language versions for new pages and strings.
- Page titles use `|` as separator. No em dashes. German text uses Swiss spelling and proper umlauts.
- Static output must build cleanly (`build` script) before the PR is opened; SSR routes, if the repo has them, need the same E2E coverage as an SPA flow.

## Stop conditions

- Decomposition ambiguous.
- Required backend or schema contract missing.
- Validation fails for reasons clearly outside the slice.
