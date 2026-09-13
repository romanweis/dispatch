# Python task (service, API, or worker)

You are implementing a Python slice. The repo's own `README.md` / `CLAUDE.md` and its `pyproject.toml` are authoritative for tooling; this file lists the rules that apply to every Python slice in this harness.

## Scope

Implement exactly what the decomposition says. No dependency upgrades, no repo-wide reformatting, no new frameworks.

## Environment

- Work in a virtual environment, never against the system interpreter. If the repo has a `uv.lock`, use `uv sync` and `uv run`; otherwise `python3 -m venv .venv && . .venv/bin/activate && pip install -e '.[dev]'` (or the repo's documented equivalent). Do not commit `.venv/`.
- Respect the pinned Python version in `pyproject.toml` (`requires-python`) and the lockfile. Adding a dependency means updating the lockfile in the same commit.
- Model weights, datasets and other large artifacts are never committed. If a slice needs them locally, download them to the path the repo documents and say so in the PR.

## Python rules

- Lint and format with `ruff` (`ruff check .` and `ruff format --check .`) before every commit. Fix the warnings; do not add `# noqa` to silence them unless the repo already does so for that rule.
- Type hints on every new public function. If the repo runs `mypy` or `pyright` in CI, it must pass.
- Tests are `pytest`. Every new endpoint, task, or public function gets a test; failure modes the code guards (bad input, missing resource, timeout) get one too. Tests must be deterministic: no network, no wall clock, no real model inference unless the repo's test suite already does that behind a marker.
- Keep the HTTP contract stable: no removed or renamed fields in responses, no changed status codes for existing paths. Additive only, so the consuming backend slice can roll back independently.
- Configuration through environment variables with documented defaults; never read secrets from the repo.
- Log with the repo's logger; no `print` in library code.
- No em dashes in user-facing or log text.

## Validation before opening the PR

```
ruff check . && ruff format --check .
pytest -q
```

plus the repo's own `Dockerfile` build if the slice touches dependencies or entrypoints.

## Stop conditions

- Decomposition ambiguous.
- The slice needs a model, dataset, or credential that is not available in the container.
- Validation fails for reasons clearly outside the slice.
