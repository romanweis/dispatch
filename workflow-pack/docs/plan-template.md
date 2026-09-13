# <short title in sentence case>

**Goal**: one sentence stating what the user gets when this ships.

## Affected repos

- `<repo-name>` - one-line description of this slice.
- `<repo-name>` - ...

## Acceptance criteria

- Independently verifiable bullet (CI check, manual verification step, or user-visible behaviour).
- ...

## Non-goals

- What this feature explicitly does NOT do. Keep scope tight.
- ...

## Optional appendix

Use this only when the core spec would otherwise be ambiguous for decomposition or implementation handoff.

- Put long background, file lists, implementation sketches, rollout caveats, or migration notes here.
- The decomposer should ignore this section unless the core spec is not enough to determine repo ownership, role, or dependency order.

---

This is the shape a refinement run should produce (and submit with `ticket spec <file>`) so `feature-decomposer` can consume the resulting spec without transformation. When the human starts the ticket, Dispatch writes the approved spec to `features/<ticket-id>-<slug>.md` and `/start-feature <ticket-id>` does the rest.

Guidelines:
- Keep the core spec short. Target roughly 40 lines or fewer before the optional appendix.
- The core spec is the default input for decomposition.
- Put implementation details in the appendix, not in the core spec.
- Keep Affected repos to 5 or fewer, and only repos listed in `repos.yaml`. Beyond that the feature is probably two features.
- Each acceptance bullet must be testable without reading the implementation.
- Non-goals exist so the cross-cutting reviewer knows what not to flag as missing.
