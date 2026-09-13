---
name: decompose-feature
description: Break a feature spec into per-repo slices. Use when orchestrating a new feature.
---

Use the `feature-decomposer` agent for this work.

- Prefer the short core spec: `Goal`, `Affected repos`, `Acceptance criteria`, `Non-goals`.
- Roles come from `repos.yaml` at the orchestrator root, never from guesswork.
- Ignore `## Optional appendix` unless repo ownership, role, or dependency order is unclear from the core spec.
- If the decomposer returns `BLOCKED: ...`, the orchestrator session asks the human with `ticket ask` and stops. Do not fill the gap yourself.
- Keep the output strict: one slice per repo, dependency-ordered, implementation-agnostic.

See `.claude/agents/feature-decomposer.md` for the exact output contract.
