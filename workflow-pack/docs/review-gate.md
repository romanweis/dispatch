# Review gate

The single source of truth for how a finding is classified and what blocks a merge. `slice-reviewer`, `cross-cutting-reviewer`, and `finding-adjudicator` all read this file. Do not restate the rubric in their prompts; point at this file.

## Why the gate exists

Assume there is no branch protection on this org. `gh pr merge` will merge a PR with red CI and a PR that is stale against main. Merging to `main` deploys to production. The review gate and `scripts/merge-fleet.sh` are the only things standing between an agent's diff and prod.

## Severities

**BLOCKER**: a checkable runtime or deploy break. Examples:

- a client selects a field, argument, or enum value the server does not serve
- a mutation, route, or argument name does not match between the slice that produces it and the slice that consumes it
- a mutation or endpoint reachable unauthenticated, or by a caller from another tenant/user, that should not be
- destructive or non-backwards-compatible DDL (see `deploy-safety` below)
- a slice's `Depends on` contract is not satisfied by the slice it depends on

**MAJOR**: violates a stated acceptance criterion in the feature spec, or a documented repo rule that CI does not enforce.

**MINOR**: a real defect that does not break behaviour and does not violate an acceptance criterion.

**NIT**: style, naming, taste.

BLOCKER and MAJOR block the merge. MINOR and NIT are recorded in `tasks/<id>/review.md` and merged anyway.

## Falsifiable failure statement

Every BLOCKER and MAJOR must state, concretely, what breaks and how you would observe it.

- Good: "`LessonSessionPage.graphql` selects `agendaItems { question }`; the server type `GroupLessonAgendaItemType` exposes no `question` field, so the query 400s at runtime."
- Not acceptable: "possible schema mismatch", "this could be a problem", "consider whether authorization is handled".

A finding you cannot state this way is at most a MINOR.

## Per-lens severity ceilings

| Lens | May emit |
| --- | --- |
| `correctness` (correctness + spec compliance) | BLOCKER, MAJOR, MINOR, NIT |
| `security` (authorization, tenancy, data exposure) | BLOCKER, MAJOR, MINOR, NIT |
| `deploy-safety` (migrations, schema back-compat; backend slices only) | BLOCKER, MAJOR, MINOR, NIT |
| `tests` (test coverage) | **MINOR, NIT only** |
| `conventions` (repo rules, role-template rules) | **MINOR, NIT only** |
| `cross-cutting` (fleet-level consistency) | BLOCKER, MAJOR, MINOR, NIT |

The ceilings on `tests` and `conventions` are deliberate. Both lenses can find something on any diff, forever. If they could block, the round cap would become the normal exit and the loop would churn without converging. CI already enforces what it enforces (coverage thresholds, lint, convention tests); re-enforcing a CI gate inside an LLM buys nothing. The blocking lenses are the ones whose output is falsifiable.

If a `tests` or `conventions` reviewer believes it has found something that genuinely breaks at runtime, it should say so plainly in the finding text. It still emits MINOR. The `correctness` lens is looking at the same diff and is the one authorised to call it.

## The `deploy-safety` lens

The backend deploys before its clients, and a rollback is a revert, so **old clients run against the new backend** for the whole deploy window, and **new clients may briefly run against the old backend** during a rollback. Every backend change must be compatible in both directions:

- no `DROP TABLE`, `DROP COLUMN`, rename, or type narrowing in the migration scripts (the directory named by `MIGRATIONS_DIR` in `workflow.env`, if any)
- no new `NOT NULL` column without a default
- no removed or narrowed GraphQL (or REST) field, argument, or enum value
- no change that invalidates an existing persisted operation

Violations are BLOCKERs. They are also the reason `rollback-fleet.sh` can safely revert the backend at all.

If the project has an automated schema-compatibility check on the backend (listed in `REQUIRED_CHECKS`), this lens should predict what it will say. If the lens and the check disagree, the lens is wrong.

## Monotonicity

Severity escalates. It never de-escalates.

A finding leaves the blocking set in exactly two ways:

1. **The code changes** and re-review of the new head no longer reports it.
2. **`finding-adjudicator` returns `OVERTURNED`.** That agent gets a fresh context and sees only the finding, the diff hunk, and the spec. It is not told the round number, that a gate exists, or that a merge depends on the answer.

Nothing else may lower a severity, and the orchestrator session may never lower one itself. Every change is logged in `review.md`:

```
F-134-R1-03: BLOCKER -> OVERTURNED by adjudicator (server does serve `question`; reviewer read a stale schema.graphql)
```

Nobody is watching this run live. The audit trail in `review.md` is the safety property.

## The gate

```
GATE: PASSED             zero open BLOCKER, zero open MAJOR
GATE: FAILED_MAX_ROUNDS  round cap reached with a BLOCKER or MAJOR still open
```

`FAILED_MAX_ROUNDS` is sticky. Write `tasks/<id>/HANDOFF.md`, post `ticket progress review-failed "FAILED_MAX_ROUNDS"` and a `ticket comment` naming the open findings, stop, and hand back to the human. Re-running `/review-feature` on that id must not launder the verdict: only a human editing `state.json` clears it.
