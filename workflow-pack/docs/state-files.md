# State files under `tasks/<id>/`

Everything the orchestrator knows about a feature lives in `tasks/<id>/` in this repo, where `<id>` is the feature id (under Dispatch: the ticket id). Two of these files are machine-read: **Dispatch pulls `state.json` and `result.json` from the ticket container** and shows them on the board as `workflowState` and `result`, and it moves a ticket to `review` when `state.json.gate` is `"PASSED"`. Their shapes below are a contract. Add fields if you must; never rename or remove one.

`state.json` is a cursor, not a record. Anything GitHub knows (PR state, merge shas, CI and deploy conclusions) is re-queried from GitHub every time; the file only says where the loop was when it last wrote.

## `state.json`

Created by `/review-feature`, updated by `/review-feature`, `merge-fleet.sh` and `rollback-fleet.sh`.

```json
{
  "feature": 155,
  "slug": "bot-difficulty-cards-bu0",
  "phase": "DONE",
  "round": 1,
  "gate": "PASSED",
  "haltReason": null,
  "frozen": false,
  "slices": [
    {
      "repo": "academy",
      "tier": 1,
      "pr": "https://github.com/{{ORG}}/academy/pull/152",
      "reviewedHeadSha": "631208e83677eb183066f57c3bf4f7ba87b58fe7",
      "openBlockers": 0,
      "openMajors": 0,
      "mergedSha": "869a4e8289609910a1a7374e860e9d25ea5fd7ec",
      "mergeAttemptedAt": "2026-08-21T16:22:34+00:00"
    }
  ]
}
```

| Field | Type | Meaning |
|---|---|---|
| `feature` | number | Feature id. Equals the directory name and, under Dispatch, the ticket id. |
| `slug` | string | The salted slug used in `feat/<id>-<slug>` and `features/<id>-<slug>.md`. |
| `phase` | string | Where the loop is. Vocabulary below. |
| `round` | number | Review rounds completed so far (0 before the first round finishes; max 3). |
| `gate` | string | Review verdict. Vocabulary below. `merge-fleet.sh` refuses anything but `PASSED`. |
| `haltReason` | string or null | Set by `merge-fleet.sh` when it stops (`DEPLOY_failure on backend`, `PROBE_FAILED ...`, `PROBE_INCONCLUSIVE ...`). Copied into `INCIDENT.md` by `rollback-fleet.sh`. |
| `frozen` | boolean | Set to `true` by `rollback-fleet.sh` together with the `FREEZE` file. `merge-fleet.sh` refuses while true. Only a human clears it. |
| `slices[]` | array | One entry per PR in the fleet, same order as `result.json.prs`. |
| `slices[].repo` | string | Repo name (directory under the workspace, repo under the org). |
| `slices[].tier` | number | Merge tier, copied from `result.json.prs[].mergeOrder`. |
| `slices[].pr` | string | PR URL. Optional; `result.json` is authoritative for URLs. |
| `slices[].reviewedHeadSha` | string or null | Head sha the last review round looked at. A slice is re-reviewed only when its current head differs. |
| `slices[].openBlockers` | number | Open BLOCKER findings against this slice after the last round. |
| `slices[].openMajors` | number | Open MAJOR findings against this slice after the last round. |
| `slices[].mergedSha` | string or null | Squash-merge commit on main, written by `merge-fleet.sh` after the merge. Informational; the script re-queries GitHub. |
| `slices[].mergeAttemptedAt` | string (ISO 8601) | Breadcrumb written immediately before `gh pr merge`, so a crash between the merge and the state commit is diagnosable. |

### `phase` vocabulary

| Value | Written by | Meaning |
|---|---|---|
| `REVIEW` | `/review-feature` | The review loop is running or has finished (see `gate`). |
| `MERGING` | `merge-fleet.sh` | A ship is in progress. Tiers merge one after another. |
| `HALTED` | `merge-fleet.sh`, `rollback-fleet.sh` | The ship stopped on a red or cancelled deploy, or a failed or inconclusive probe. `haltReason` says why. `rollback-fleet.sh` also sets this first thing, together with `frozen`. |
| `DONE` | `merge-fleet.sh` | Every tier merged, deployed and (if configured) probed live. The feature is in production. |
| `ROLLED_BACK` | `rollback-fleet.sh` | Every merged slice was reverted and the reverts deployed. `INCIDENT.md` exists, `FREEZE` is set. |

Before `state.json` exists (during `/start-feature`), `fleet-status.sh` reports the phase as `IMPLEMENTED`.

### `gate` vocabulary

| Value | Meaning |
|---|---|
| `PENDING` | No verdict yet. Initial value. |
| `PASSED` | Zero open BLOCKER and zero open MAJOR after a review round. The only value `merge-fleet.sh` accepts. Dispatch moves the ticket to `review` on the board. |
| `FAILED_MAX_ROUNDS` | Round cap (3) reached with a BLOCKER or MAJOR still open. Sticky: re-running `/review-feature` must not clear it; only a human editing the file does. `HANDOFF.md` lists what is open. |

## `result.json`

Written by `/start-feature` once the PRs exist and CI has resolved. `merge-fleet.sh`, `rollback-fleet.sh` and `fleet-status.sh` treat it as the authoritative fleet list (never `gh search prs`, whose index lags).

```json
{
  "featureId": 139,
  "slug": "game-precision-tab-j4d",
  "status": "fleet-green",
  "prs": [
    {
      "repo": "chess-ai",
      "url": "https://github.com/{{ORG}}/chess-ai/pull/17",
      "headBranch": "feat/139-game-precision-tab-j4d",
      "state": "OPEN",
      "ci": "SUCCESS",
      "mergeOrder": 1
    },
    {
      "repo": "backend",
      "url": "https://github.com/{{ORG}}/backend/pull/183",
      "headBranch": "feat/139-game-precision-tab-j4d",
      "state": "OPEN",
      "ci": "SUCCESS",
      "mergeStateStatus": "CLEAN",
      "mergeOrder": 2
    }
  ],
  "mergeOrder": [["chess-ai"], ["backend"]],
  "crossCuttingReview": { "verdict": "CLEAN", "blockers": 0, "notes": [] },
  "rollout": "Deploy chess-ai before backend."
}
```

| Field | Type | Required | Meaning |
|---|---|---|---|
| `featureId` | number | yes | Feature id. |
| `slug` | string | yes | Salted slug. |
| `status` | string | no | Free-form summary at write time (`fleet-green`, `fleet-red`, `partial`). |
| `prs[]` | array | yes | One entry per slice PR. The scripts iterate this list. |
| `prs[].repo` | string | yes | Repo name; must exist under the workspace and in `repos.yaml`. |
| `prs[].url` | string | yes | PR URL. The scripts take the PR number from its last path segment. |
| `prs[].headBranch` | string | yes | `feat/<id>-<slug>`. |
| `prs[].state` | string | yes | `OPEN`, `MERGED`, `CLOSED` at write time. Informational; re-queried live. |
| `prs[].ci` | string | yes | Rollup at write time (`SUCCESS`, `FAILURE`, `PENDING`). Informational. |
| `prs[].mergeStateStatus` | string | no | GitHub's `mergeStateStatus` at write time. |
| `prs[].mergeOrder` | number | yes | Dependency tier, 1 = merge first. `merge-fleet.sh` merges tier by tier and holds tier n+1 until tier n is merged, deployed and probed. Defaults to 1 if missing, which is almost never what you want for a multi-slice fleet. |
| `mergeOrder` | array of arrays | no | The same tiers as a list of repo lists, for humans. |
| `crossCuttingReview` | object | no | Verdict of the last `cross-cutting-reviewer` run: `verdict` (`CLEAN` or `NEEDS_FIXES`), `blockers`, `notes[]`. |
| `rollout` | string | no | One-line human note about deploy ordering. |

## `probe.json`

Written by `/review-feature` when the fleet contains a slice for `BACKEND_REPO` and `PROBE_HOSTS` is configured. `merge-fleet.sh` refuses to ship such a fleet without it.

```json
{
  "host": "web.example.com",
  "validationQueries": ["{ groupLesson(id: \"00000000-0000-0000-0000-000000000000\") { agendaItems { question } } }"],
  "assertions": [{ "type": "GroupLessonAgendaItem", "field": "question" }]
}
```

| Field | Meaning |
|---|---|
| `host` | Optional. Public GraphQL host to probe instead of the `PROBE_HOSTS` entry, for backends that serve several vhosts and reject anonymous POSTs on the default one. |
| `validationQueries[]` | Preferred. Documents that select the new fields. Any response with `data` or `errors` proves the fields exist; an unknown-field or unknown-type error fails the probe. Works with introspection disabled. |
| `assertions[]` | Fallback. `{type, field}` pairs checked through `__type` introspection. Inconclusive when production disables introspection. |

An empty `assertions` list with no `validationQueries` only proves the server answers, not that the new schema is live.

## Other files in `tasks/<id>/`

| File | Written by | Purpose |
|---|---|---|
| `decomposition.md` | `feature-decomposer` via `/start-feature` | One `## Slice: <repo>` block per repo with role, goal, dependencies, acceptance. |
| `log.md` | `/start-feature`, `pr-fleet-tracker` | Append-only timeline: `[<iso>] <repo> -> <pr-url>`, CI resolutions. |
| `review.md` | `/review-feature` | Append-only findings table per round with ids `F-<id>-R<round>-<nn>`, plus adjudication lines. The audit trail. |
| `HANDOFF.md` | `/review-feature` | Written on `FAILED_MAX_ROUNDS`: every open finding and what was tried. |
| `INCIDENT.md` | `rollback-fleet.sh` | Why the ship halted, what was reverted, what happens next. |

## Progress phases posted to Dispatch

Independent of the files above, the session posts milestones to the board with `ticket progress <phase> [note]`. Free-form by contract, but use this vocabulary so cards read consistently:

`decomposed`, `pr-opened`, `ci-green`, `review-passed`, `review-failed`, `ready-to-ship`, `shipping`, `shipped`, `halted`, `rolled-back`.
