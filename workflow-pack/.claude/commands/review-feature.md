---
description: Review a feature's PR fleet with sub-agents, fix what they find, repeat until the gate passes
argument-hint: <feature-id>
---

Review feature `$1` until it is fit to ship, or until the round cap says it is not. Follow `CLAUDE.md` and `docs/review-gate.md`.

You do not merge anything in this command. `/ship-feature` does that, only if you write `GATE: PASSED`, and only when the human asks for it.

## Setup

1. Read `features/$1-*.md`, `tasks/$1/decomposition.md`, and `tasks/$1/result.json`. If any is missing, stop and `ticket comment` what is missing.
2. Every PR in the fleet must be CI-green before the first round. If one is not, run `pr-fleet-tracker` and fix the red before reviewing. Reviewing a diff that does not build wastes a round.
3. Create `tasks/$1/state.json` if it does not exist (full field reference: `docs/state-files.md`):

```json
{ "feature": <id>, "slug": "<slug>", "phase": "REVIEW", "round": 0, "gate": "PENDING",
  "haltReason": null, "frozen": false,
  "slices": [ { "repo": "<repo>", "tier": <mergeOrder>, "pr": "<pr-url>", "reviewedHeadSha": null,
                "openBlockers": 0, "openMajors": 0, "mergedSha": null } ] }
```

If it exists and `gate` is already `FAILED_MAX_ROUNDS`, stop. That verdict is sticky and only a human clears it. Re-running this command must not launder it.

## The round (repeat, max 3)

**1. Pick what to review.** A slice needs review iff its current `headRefOid` (from `gh pr view`) differs from its `reviewedHeadSha` in `state.json`. In round 1 that is all of them. Later rounds review only what changed.

**2. Fan out.** Launch `slice-reviewer` in parallel, once per (slice x lens), all in a single message:

- `correctness`: every slice
- `security`: every slice
- `conventions`: every slice
- `tests`: every slice
- `deploy-safety`: slices whose repo is the `BACKEND_REPO` from `workflow.env` (and any slice with a `backend` role in `repos.yaml`)

Give each one only: the lens name, the feature id, the repo, the PR number, the role, and that slice's block from the decomposition. Nothing else.

**Do not tell a reviewer the round number, that a gate exists, that a merge depends on the answer, or what any other reviewer found.** A reviewer that knows you need it to pass will find a way to let you pass. Its job is an honest read of the diff; yours is what to do about it.

**3. Cross-cutting.** Run `cross-cutting-reviewer` whenever any slice changed this round. It is the only agent that sees between the slices.

**4. Record.** Give every finding an id: `F-$1-R<round>-<nn>`. Append this round to `tasks/$1/review.md` (never rewrite an earlier round), then commit and push it.

```markdown
## Round <n> (<iso timestamp>)

Reviewed: backend@<sha>, trainer@<sha>

| id | severity | lens | slice | file:line | finding |
|---|---|---|---|---|---|
| F-$1-R1-01 | BLOCKER | correctness | backend | Foo.cs:42 | ... |

Open after this round: 1 BLOCKER, 0 MAJOR
```

**5. Disputes.** If you believe a BLOCKER or MAJOR is a false positive, you may not downgrade it yourself. Launch `finding-adjudicator` on that finding alone. Only `OVERTURNED` removes it from the blocking set, and you log the verdict in `review.md`:

```
F-$1-R1-01: BLOCKER -> OVERTURNED by adjudicator (the field does exist; the reviewer read a stale schema.graphql)
```

Severity escalates. It never de-escalates by any other route.

**6. Fix.** Fix every open BLOCKER and MAJOR, in-session, on the same feature branches. Each fix commit names the finding it closes:

```
fix($1): reject over-length question server-side [F-$1-R1-01]
```

**Nothing else changes in a fix commit.** No refactors, no tidying, no improvements you happen to notice. A diff hunk in a review round that closes no finding is out of scope, and it is what makes the loop diverge instead of converge: your fix invalidates CI and re-triggers review of a bigger diff, which finds more, forever.

MINOR and NIT findings are recorded and left alone.

**7. Re-green.** Push, then run `pr-fleet-tracker` until the changed PRs are green again. Update each reviewed slice's `reviewedHeadSha`, `openBlockers`, `openMajors`, and bump `round`. Commit and push `state.json`.

**8. Loop or exit.**

- Zero open BLOCKER and zero open MAJOR -> write `"gate": "PASSED"`, commit, push. `ticket progress review-passed "round <n>, 0 open"`. Done; continue below.
- Any still open, and this was round 3 -> write `"gate": "FAILED_MAX_ROUNDS"`, write `tasks/$1/HANDOFF.md` naming every unresolved finding and what you tried, commit, push. `ticket progress review-failed "FAILED_MAX_ROUNDS"`, `ticket comment` with the open findings, and stop. **Do not merge. Do not soften a severity to get out of the loop.** Handing an honest failure to a human is a correct outcome of this command; a laundered pass is not.
- Otherwise, `ticket progress review-failed "round <n>: <b> BLOCKER, <m> MAJOR open, fixing"` and next round.

## When the gate passes

If the fleet includes a backend slice and `PROBE_HOSTS` is set in `workflow.env`, write `tasks/$1/probe.json` before finishing. `merge-fleet.sh` uses it to prove the new schema is actually live in production before it deploys any client that selects those fields, and it refuses to ship a backend fleet without it.

Prefer `validationQueries` (documents that select the new fields; they work with introspection disabled) and list `assertions` as a fallback. Shape and field meanings: `docs/state-files.md`.

```json
{ "host": "<public graphql host the clients query>",
  "validationQueries": ["{ someRoot { newField } }"],
  "assertions": [ { "type": "GroupLessonAgendaItem", "field": "question" } ] }
```

Commit and push it, then `ticket progress ready-to-ship "<n> PRs, gate PASSED"`, print the review summary, and stop. Dispatch moves the ticket to `review`; the human decides whether to ship and will resume this session with a message asking for `/ship-feature $1`. Do not run it yourself.
