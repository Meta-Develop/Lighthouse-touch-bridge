---
name: core-skill-baseline
description: "Use as the fallback for common project work when a more specific project-local skill for git workflow, local agent-file privacy, risk review, interface review, code/content review, hygiene, debugging, testing, or completion verification is missing."
---

# Core Skill Baseline

Use this skill only as a fallback. If the project has a more specific skill for
the task, use that local skill first.

## Git Workflow

- Run `git status --short` before tracked-file edits.
- Create or switch to a task branch before tracked-file edits. Use a feature
  branch or isolated worktree, not `main` or `master`.
- If local edits already exist on `main` or `master`, create a branch before any
  further edits.
- Separate independent features, fixes, and cleanup into separate branches or
  worktrees when their scopes do not need to ship together.
- Preserve development history. Completion into `main`, `master`, or another
  default branch must use `git merge --no-ff`; PR completion must use a
  merge-commit mode that preserves the branch's logical commits. Fast-forward
  merges, `--ff-only` merges, squash merges, rebases, and any other
  history-flattening completion path are prohibited.
- Commit each logical unit separately unless the user explicitly requests no
  commits or the repository policy forbids committing.
- Do not mix unrelated changes in one commit, branch, worktree, or final
  handoff.
- Merge to the default branch only after relevant validation is complete. If
  validation is blocked, do not merge automatically; report the blocker instead.
- Agents may push scoped commits on non-protected task branches without asking
  when the remote/upstream is clear or can be set safely, relevant verification
  passed or failures are clearly unrelated, staged and committed paths have been
  reviewed for secrets, scratch files, and private/local-only agent files, and
  project policy does not forbid publishing those paths.
- Keep task branches synchronized with their remote: when the scoped
  task-branch push conditions above are met, push after each logical-unit
  commit or small coherent group of commits, and always before a handoff or
  session end. Do not accumulate local-only commit history when a safe
  remote/upstream exists; if pushing is blocked, report the blocker instead
  of silently deferring.
- Pushing a task branch is not approval to merge, release, publish packages, or
  publicize sensitive owner or agent context.
- If validation is blocked by unrelated existing issues, agents may still push a
  scoped task branch when the commit itself passed the smallest relevant checks
  and the blocker is reported.
- Ask before pushing directly to `main`/`master`, merging to a default branch,
  force-pushing, rewriting history, deleting remote branches, publishing
  releases or packages, or pushing private/local-only agent files when project
  policy forbids them.
- Do not rewrite, reset, or clean user changes unless explicitly requested.

## Repository Mutation Boundary

- Treat the current Git repository as the hands-on mutation boundary.
- Do not directly mutate a different Git-backed repository just because it is
  adjacent in the filesystem, discoverable from a parent directory, or relevant
  to a finding.
- For another repository, stop before mutation and preserve the finding as a
  maintainer-facing GitHub issue, local issue draft, or handoff with
  repo-relative evidence. Use autonomous issue feedback guidance when issue or
  draft details are needed.
- Direct mutation in another Git-backed repository is allowed only after the
  user explicitly changes scope or a delegated worker is launched in that target
  repository context.
- Before any such target-repo mutation, load that repository's local
  instructions, check its Git state, and use its branch, validation, and handoff
  workflow.
- Registry mirrors under inventory-defined
  `projects/<organization>/<repo>/.agents` paths are registry data and must be
  changed through registry sync or feature adoption workflows, not as ad hoc
  live repository edits.

## Local Agent Files

- Treat `.agents/`, `AGENTS.md`, `.agent`, `.github/agents/`, `.github/hooks/`,
  `.github/copilot-instructions.md`, and local prompt/tooling files as private
  unless the project explicitly says they are public.
- Prefer `.git/info/exclude` for checkout-local ignores that should not alter the
  public repository.
- Before push or handoff, check staged and committed paths for private agent
  files when project rules prohibit publishing them.
- If a local agent file is already tracked, do not remove it from history without
  an explicit project decision.

## Human Authorship And Attribution

Machine, model, vendor, runtime, and agent names must never be used as Git
authors or committers, credit trailers such as `Co-authored-by`,
`Signed-off-by`, or `Reviewed-by`, contributors, authors, credits, copyright
owners, release implementation credits, generated-by stamps, or
agent-implementation disclosures. Use the human or organization identity
approved by the current repository; never assume one global name or email.

Preserve real human credits and ordinary technical references to models,
vendors, runtimes, APIs, and compatibility. A factual disclosure may be added
only when the owner explicitly requests it, and it must not misstate
authorship. Install the composing checkout-local guards with
`.agents/scripts/install-human-authorship-guard`; verify them with the same
command's `--check` mode. Before publishing PR, release, contributor, author,
credit, copyright, or package metadata, run
`.agents/scripts/check-human-authorship metadata-tree` and resolve contextual
findings. Use `.agents/scripts/check-human-authorship all-history` when an
exhaustive audit of every commit reachable from local refs is required.

## Organization Confidentiality

For private or organization-owned repositories, preserve confidentiality for
source, exports, generated packages, internal docs, agent context, raw
artifacts, and cross-repo findings. Respect organization-specific remotes,
release channels, public/private split rules, and publication gates. Classify
source, exports, docs, generated artifacts, and issue/PR text before tracking,
pushing, uploading, or publishing them.

Do not publish `.agents`, `AGENTS.md`, raw agent artifacts, AI/orchestration
traces, private handoff notes, or organization-sensitive findings unless the
project explicitly permits it. Sensitive cross-repo findings should default to
local/private issue drafts or handoffs rather than public issues or edits in
another repository.

## Owner-Local Path Redaction

Do not expose owner-local absolute paths or machine-specific roots in
public-facing docs, code comments, issue/PR text, release notes, packages, or
other publishable tracked content. Examples include `/mnt/d/...`,
`/home/konn/...`, `/home/<owner>/...`, `C:\Users\...`, drive roots such as
`D:\...`, and WSL mount paths. Use repo-relative paths or redacted placeholders
such as `<repo-root>`, `<local-project-root>`, `<owner-home>`, or
`<host-setup-repo>` when a local location matters.

Private agent-only context may mention local paths only when required for local
operation. Do not copy those paths into public docs, issues, PRs, releases, or
packages.

## Change Risk

- Low risk: isolated docs, comments, narrow tests, or leaf code with clear
  callers.
- Medium risk: shared helpers, config defaults, build scripts, public docs, or
  behavior with several callers.
- High risk: public interfaces, protocols, schemas, migrations, security,
  deployment, hardware, data loss, auth, billing, or cross-project behavior.
- Increase verification depth with risk and blast radius.

## Interface Review

Before changing a contract, identify producers, consumers, compatibility
expectations, config names, file formats, APIs, CLI flags, protocols, and tests.
Preserve compatibility unless the task explicitly asks for a breaking change.

## Code And Content Review

Review for correctness, regression risk, missing verification, accidental
privacy leaks, confusing public text, and mismatch with project conventions.
Report concrete findings first, with file paths and line references when
available.

## SPECA Review Escalation

For security-, protocol-, invariant-, trust-boundary-, or
specification-compliance reviews, use SPECA methodology as an optional
escalation path: derive expected properties from specs or requirements, map each
property to implementation code, try to prove it holds, and treat proof gaps as
candidate findings. Run a SPECA CLI or pipeline only when enough specification
context exists and the runtime or cost is justified. Independently verify SPECA
outputs before reporting or acting on them. Keep generated audit outputs, logs,
and target-specific findings sensitive and out of tracked project context unless
explicitly approved.

## Project Hygiene

Keep generated outputs, raw command logs, caches, secrets, local credentials,
temporary experiments, and bulky artifacts out of tracked history. Generated
outputs and caches must be ignored, removed from the worktree, or intentionally
curated as durable project artifacts. Prefer summaries over raw evidence.

## Language Policy

Follow the project `AGENTS.md` Language Policy.

## License Selection

When adding, changing, or recommending a project license, first identify the
project's distribution model and community goal. Do not apply one license family
to every repository by default.

- Prefer `AGPL-3.0-or-later` for server-side applications, hosted developer
  tools, API services, AI/ML infrastructure, databases, monitoring/auth systems,
  or other software where the main risk is SaaS or cloud operators taking
  community work, modifying it, and not returning source changes.
- Prefer `GPL-3.0-or-later` for end-user applications, firmware, CLIs, or tools
  where modified binaries may be distributed but network-service use is not the
  central risk.
- Prefer `LGPL-3.0-or-later`, `MPL-2.0`, `Apache-2.0`, or `MIT` for libraries,
  SDKs, protocol implementations, small utilities, research sample code, or
  standardization-oriented components where adoption, compatibility, and reuse
  are more important than forcing all downstream code open.
- Prefer `Apache-2.0` over `MIT` when an explicit patent grant matters.
- Prefer content-specific licenses for non-code works: for example
  `CC-BY-SA-4.0` for share-alike educational text and documentation, and a
  hardware license such as CERN-OHL when the primary artifact is hardware
  design rather than software.
- Preserve third-party dependency and vendored-code licenses. Do not replace
  upstream notices for bundled SDKs, libraries, generated files, or assets that
  are not owned by the project.

For owner-controlled projects that value community reciprocity over maximum
corporate adoption, default to a copyleft option. For projects whose main goal
is broad ecosystem uptake, prefer a permissive or weak-copyleft option unless
the owner asks for stronger reciprocity. Record the reason in the commit,
release notes, or handoff when changing a license.

## Debugging

Reproduce or observe the failure, isolate the smallest failing surface, state a
hypothesis, test one change at a time, and keep durable notes only when they help
future work.

## Testing And Verification

Use the smallest relevant project check for the change. Broaden to integration,
build, or end-to-end checks when editing shared behavior, public contracts, or
user-facing workflows. If a check cannot run, state why and what remains
unverified.

## Completion

Before final handoff, run the relevant verification when practical, check
`git status --short`, and use the repository handoff guard when available to
confirm branch, verification, and worktree state. In `agent_files`,
registry-local verification and handoff guard details are owned by
`.agents/docs/PROJECT_RULES.md` Verification. Rerun `git status --short` after
commands that can change tracked files. Separate unrelated pre-existing changes
from your work, and leave no dirty state from your task unless handing off
explicit WIP with the reason, owner, and next command. Report changed files,
validation, and remaining risk.
