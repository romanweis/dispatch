Ticket {{ticket.id}} "{{ticket.title}}" is a **task**: the human marked it as clear enough to skip refinement, so there was no question round and nobody approved a spec. The plan step still happens, in this run, before the implementation cycle.

`{{project.workspace}}/orchestrator/features/{{ticket.id}}-{{ticket.slug}}.md` currently holds the human's request as written. Plan first:

1. `ticket show` for the full request.
2. Explore `{{project.workspace}}` read-only: `{{project.workspace}}/CLAUDE.md` (workspace map, which API each client talks to), `{{project.workspace}}/orchestrator/repos.yaml` (only those repos are valid slice targets), then the affected repos' `CLAUDE.md` and code. No builds, tests or dev stack for this step.
3. Rewrite the feature file in the shape of `{{project.workspace}}/orchestrator/docs/plan-template.md`: title, `**Goal**`, `## Affected repos` (one-line slice each), `## Acceptance criteria` (independently verifiable), `## Non-goals`. Keep the scope to exactly what the request asks, no extras. Under `## Optional appendix` put the original request verbatim and every default assumption you made.
   Conventions that shape acceptance criteria: no em dashes in user-facing text, Swiss German spelling, Relay fragment composition, backend changes additive and backwards-compatible, tests mandatory (integration tests for backend, E2E for user-visible flows).
4. `ticket spec {{project.workspace}}/orchestrator/features/{{ticket.id}}-{{ticket.slug}}.md`, commit (`feat({{ticket.id}}): plan {{ticket.slug}}`) and push it, then `ticket progress planned "<repo list>"`.
5. Do not wait for approval. Decide small open points yourself and record the assumption in the appendix. Only `ticket ask` (with your default) and stop if something material is genuinely ambiguous, or if the task is really a feature (more than 5 repos or two independent goals).

Then continue in this same run with the implement -> review -> fix cycle below, exactly as for a refined feature.
