The approved spec for ticket {{ticket.id}} "{{ticket.title}}" is committed at `{{project.workspace}}/orchestrator/features/{{ticket.id}}-{{ticket.slug}}.md`.

cd into `{{project.workspace}}/orchestrator` and run `/start-feature {{ticket.id}}`, following that repo's `CLAUDE.md` exactly. The ticket id {{ticket.id}} is the feature id; the slug is `{{ticket.slug}}`, so branches are `feat/{{ticket.id}}-{{ticket.slug}}`, PR titles start with `[feat-{{ticket.id}}]`, and every PR carries the label `feature:{{ticket.id}}`.

This session is headless: background tasks (CI watches, reviewer agents) are killed when your turn ends, so run every wait in the foreground with a long timeout and finish the step inside the turn. Only `ticket ask` or a completed step ends a turn.

Before starting, check `git status --short features/` in the orchestrator repo; if the feature file is untracked or modified, commit and push it first.

Report progress on the board with `ticket progress <phase> [note]` at least at: decomposition done, each PR opened, CI green, each review round passed or failed, ready to ship. Post anything the human must know (a draft PR left behind, a stop condition) with `ticket comment`.

If you are blocked, if a repo cannot be fast-forwarded, or if the decomposition looks wrong, run `ticket ask "<precise question with your default assumption>"` and stop the turn. Do not compensate in code and do not guess.

Never run `/ship-feature` from this run. When the review gate passes, post `ticket progress ready-to-ship` and stop; Dispatch decides what happens next (the human presses Ship, or auto-merge is on for this ticket) and sends a separate ship prompt into this session.
