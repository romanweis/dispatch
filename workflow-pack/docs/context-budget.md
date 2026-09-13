# Context budget

Repo-local prompt bloat matters because this harness fans one feature into multiple downstream tasks. The cheapest fix is not more tooling. It is less repeated context.

## Model defaults

- `feature-decomposer` -> `haiku`
- `pr-fleet-tracker` -> `haiku`
- `cross-cutting-reviewer` -> `sonnet`
- `slice-reviewer` -> `sonnet`
- `finding-adjudicator` -> `sonnet`
- Main slice implementation stays on the stronger default session model.

Escalate a bounded task only when ambiguity or repeated failure justifies the extra cost.

## Review fan-out is the expensive part

`slice-reviewer` runs once per (slice x lens), so a 3-slice fleet with a backend in it is ~13 invocations per round, and the loop runs up to 3 rounds. That is the single biggest cost in the harness. Two things keep it bounded, and both are load-bearing:

- **Only changed slices are re-reviewed.** A slice whose `headRefOid` still matches its `reviewedHeadSha` is not looked at again. Round 2 is usually one slice, not all of them.
- **Fix commits close findings and nothing else.** A fix that also tidies enlarges the diff, invalidates CI, and re-triggers review of *more* code, which finds more to fix. That is how a 2-round feature becomes a 3-round failure.

Give each reviewer only its lens, its slice, and the core spec. A reviewer handed the whole fleet's context costs more and reviews worse.

## Prompt layering rules

- `CLAUDE.md` owns the shared workflow: spec handoff, decomposition, slice execution, PR conventions, logging, stop conditions, and how to talk to the human through `ticket`.
- Commands, skills, agents, and role templates should keep only task-specific rules and strict output contracts.
- Role templates should focus on repo-specific conventions. They should not restate generic branch, PR, or log boilerplate that already lives in `CLAUDE.md`.
- The target repo's own `CLAUDE.md` (under `{{WORKSPACE}}/<repo>`) carries that repo's rules. Do not copy them into the orchestrator.

## Spec budget

- The default feature spec is the short core:
  - `Goal`
  - `Affected repos`
  - `Acceptance criteria`
  - `Non-goals`
- Keep the core spec short. Aim for roughly 40 lines or fewer before any appendix.
- Use `## Optional appendix` only for material that helps humans or escalated implementation work: background, file lists, implementation sketches, rollout notes, migration caveats.
- Bounded subagents should ignore the appendix unless the core spec is not enough to determine repo ownership, role, or dependency order.

## Practical heuristics

- If a spec needs more than five repos, split it before dispatch.
- If a decomposer or reviewer needs pages of implementation notes to do its job, the core spec is probably too vague. Under Dispatch that is a refinement problem: ask with `ticket ask` before starting, not after.
- If a task only classifies, watches, or summarizes, keep it on the cheapest model that can reliably follow the output contract.
