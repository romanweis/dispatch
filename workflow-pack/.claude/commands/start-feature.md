---
description: Implement an orchestrated feature in this session from features/<id>-*.md
argument-hint: <feature-id>
---

Implement feature `$1` end-to-end in this session. Follow `CLAUDE.md`, including "Talking to the human": progress goes to the board via `ticket progress`, blockers via `ticket ask` followed by a stopped turn.

1. Read `features/$1-*.md`. If none exists, `ticket ask` where the spec is and stop. If it exists but is uncommitted, commit and push it first (`feat($1): <slug>`).
2. Prefer the short core spec. Only consult `## Optional appendix` when the core spec is not enough to determine slice roles or dependencies.
3. Launch `feature-decomposer` (it reads `repos.yaml` for roles) -> write `tasks/$1/decomposition.md`, commit, push. `ticket progress decomposed "<repo list>"`. If it returns `BLOCKED: ...`, `ticket ask` with the reason and stop.
4. Refresh `main` on every repo named in `tasks/$1/decomposition.md`:
   - `cd {{WORKSPACE}}/<repo> && git fetch origin && git checkout main && git pull --ff-only`
   - If any pull is not fast-forward, `ticket ask` which repo and why, and stop.
5. For each slice in dependency order:
   - `cd {{WORKSPACE}}/<repo>`
   - `git fetch origin && git checkout -B feat/$1-<slug> origin/main`
   - read `role-templates/<role>-task.md` and the repo's own `CLAUDE.md`
   - implement, validate, commit, push, open or update the PR with `[feat-$1]` and label `feature:$1`
   - append `[<iso-timestamp>] <repo> -> <pr-url>` to `tasks/$1/log.md`, commit, push
   - `ticket progress pr-opened "<repo> <pr-url>"`
6. Launch `pr-fleet-tracker` for CI resolution. Fix reds that are clearly in scope. When green: `ticket progress ci-green`.
7. Write `tasks/$1/result.json` (shape in `docs/state-files.md`) with PR URLs, states, head branches, and `mergeOrder` (the dependency tier from the decomposition: producers before consumers). `merge-fleet.sh` reads `mergeOrder` to decide what may deploy before what, so it is not decoration. Commit and push.
8. Print one line per PR with repo, URL, and current CI state.
9. Run `/review-feature $1`.

Do not add new slices, new repos, or out-of-scope compensating changes. If the decomposition looks wrong, `ticket ask` with what you would change and why, and stop.

Never run `/ship-feature` from here. Shipping is gated on the review verdict and on the human asking for it, and this command has no opinion about either.
