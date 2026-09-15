Dispatch is asking you to ship ticket {{ticket.id}} "{{ticket.title}}" (feature {{ticket.id}}, slug `{{ticket.slug}}`). The review gate is PASSED and the human authorised the merge for this ticket on the board (Ship button or auto-merge).

cd into `{{project.workspace}}/orchestrator` and run `/ship-feature {{ticket.id}}` exactly as that repo's `CLAUDE.md` and the command describe: read `tasks/{{ticket.id}}/state.json`, `scripts/merge-fleet.sh {{ticket.id}} --dry-run` first, then the real run. Expect it to take a while; do not shorten timeouts and do not push to any fleet repo's `main` meanwhile.

Report on the board:
- `ticket progress shipping "<n> PRs, <t> tiers"` before the real merge starts.
- `ticket progress shipped "<repos>"` only when every slice is merged and its deploy is green. This note closes the ticket and deletes this container.
- On a halt or rollback: `ticket progress halted|rolled-back "<reason>"`, `ticket comment` with the incident summary (contents of `tasks/{{ticket.id}}/INCIDENT.md` if it exists), and stop. Never hotfix production.
- If you need a decision from the human (for example `PROBE_INCONCLUSIVE`), `ticket ask "<question with your default assumption>"` and stop.
