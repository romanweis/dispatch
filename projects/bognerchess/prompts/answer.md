The user answered:

{{answers}}

Continue refinement of ticket {{ticket.id}} "{{ticket.title}}" with these answers taken as decisions. Run `ticket show` if you need the full ticket again.

Either:

- ask the remaining open questions with one `ticket ask` call (max 5, each answerable in one sentence, each stating your default assumption) and STOP, or
- write the spec in the shape of `{{project.workspace}}/orchestrator/docs/plan-template.md` (Goal / Affected repos / Acceptance criteria / Non-goals, at or under 40 lines, optional appendix) to a temp file and submit it with `ticket spec <file>`, then STOP.

Same rules as before: read-only exploration of `{{project.workspace}}` only, no implementation, no branches, no commits, no orchestrator commands. Prefer submitting the spec over another round of questions unless something material is still unresolved.
