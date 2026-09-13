# Project configuration (`projects/<name>/`)

A project is a folder. The API loads every `projects/*/project.yaml` at startup (path from `Dispatch:ProjectsDir`, default `../../projects` relative to the binary, overridable via env `Dispatch__ProjectsDir`) and upserts a `projects` row by `name`.

```
projects/<name>/
  project.yaml       # required, schema below
  CLAUDE.md          # workspace-level rules, placed at <workspace>/CLAUDE.md in the base container
  prompts/refine.md  # refinement prompt template
  prompts/work.md    # hand-over prompt template
  prompts/answer.md  # optional; default: "The user answered your questions:\n{{answers}}\n..." 
  provision.sh       # runs as root inside the fresh base container, after repos are cloned (optional)
  provision-user.sh  # runs as uid 1000 inside the base container after provision.sh (optional)
```

## project.yaml

```yaml
name: bognerchess              # slug, also the base container name prefix (<name>-base)
displayName: Bogner Chess
org: bognerchess               # GitHub org; repos are cloned from github.com/<org>/<repo>
workspace: /home/agent/bognerchess
maxParallel: 3                 # concurrent claude runs for this project
repos:                         # cloned into <workspace>/<repo>
  - name: backend
    role: backend              # role-templates/<role>-task.md in the workflow pack
  - name: academy
    role: astro
  - name: trainer
    role: frontend-relay
orchestrator:
  repo: orchestrator           # <workspace>/orchestrator, created from workflow-pack if missing on GitHub
  deployWorkflow: deploy.yml   # name of the deploy workflow guarded by merge-fleet.sh
  requiredChecks: []           # extra check names merge-fleet must see green
  probeHosts: {}               # repo -> public host for the post-deploy liveness probe, e.g. backend: api.example.com
  schemaFile: ""               # optional, path inside the backend repo of the committed GraphQL schema
  migrationsDir: ""            # optional, enables the migration-number race guard
  extraMainWorkflows: []       # optional, workflows on main that must be idle before merging
claude:
  permissionMode: bypassPermissions   # containers are disposable
  allowedTools: []                    # empty = default
  model: null                         # null = account default
  maxTurnsRefine: 60
  maxTurnsWork: 400
env:                             # extra environment in every ticket container
  ASPNETCORE_ENVIRONMENT: Development
```

## Prompt templates

Simple `{{placeholder}}` replacement. Available: `{{ticket.id}}`, `{{ticket.title}}`, `{{ticket.body}}`, `{{ticket.spec}}`, `{{ticket.slug}}`, `{{project.name}}`, `{{project.workspace}}`, `{{project.org}}`, `{{answers}}` (answer prompt only: `Q: ...\nA: ...` blocks), `{{questions}}`.

## Base container build (`infra/build-project-base.sh <name>`)

1. `incus copy agent-base <name>-base` (agent-base is an image produced by `infra/build-agent-base.sh`), start, wait for network.
2. Clone every repo in `repos` plus `orchestrator.repo` into `workspace` as uid 1000 using the host's `gh auth token`.
3. If the orchestrator repo does not exist on GitHub or is empty: render `workflow-pack/` with the project's values and push it.
4. Push `CLAUDE.md` to `<workspace>/CLAUDE.md`; push `cli/ticket` to `/usr/local/bin/ticket`.
5. Run `provision.sh` as root, then `provision-user.sh` as uid 1000.
6. `incus stop <name>-base`. Ticket containers are `incus copy <name>-base t-<id>`.

Rebuild any time; running ticket containers are unaffected.
