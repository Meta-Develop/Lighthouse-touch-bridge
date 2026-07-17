# Core Agent Skills

This project uses a layered skill model:

1. Project-local rules and skills are authoritative.
2. Shared registry features provide baseline behavior.
3. Profile guidance fills in project-type defaults.

Do not replace a detailed project-local skill with a thinner shared copy. When common behavior is useful across projects, promote the reusable part to `agent_files/features` and keep project-specific triggers, commands, paths, hardware limits, service names, and validation commands local.

## Core Skill Families

Every managed project must have coverage for:

- startup behavior and project rules
- local-only `.agents` git/privacy handling
- organization-owned repository confidentiality and publication gates
- git workflow and branch discipline
- human authorship and attribution enforcement for commits and publication metadata
- change-risk classification
- interface contract review
- code/content review
- project hygiene and artifact handling
- host software, download, package, and dependency request routing
- license selection and third-party notice preservation
- systematic debugging
- test-driven or verification-driven changes
- completion verification
- agent orchestration when work is parallelizable

The shared `core-skill-baseline` skill supplies fallback behavior for these
families. Richer project-local skills with names such as `git-workflow`,
`agent-local-git`, `change-risk-verification-matrix`, `interface-contract-review`,
`code-review`, `content-review`, `project-hygiene`, `systematic-debugging`,
`test-driven-development`, and `verification-before-completion` remain
authoritative when present. If two skills overlap, load the more specific
project or profile skill first.

Use the shared `host-software-requests` skill when a non-host project needs
software, downloads, packages, or dependencies that might belong in the user's
persistent NixOS host setup. Keep repo-local development shells and one-off
ephemeral tools in the current project when appropriate. Route durable host-wide
installation, downloads, app tooling, services, device rules, desktop
integration, or PATH changes to a delegated worker in the managed
My_NixOS_Setup host repository; that worker must load the host repository's own
instructions and use its branch, validation, and activation workflow.

Install the checkout-local authorship guards with
`.agents/scripts/install-human-authorship-guard`. The installer composes with
existing `commit-msg` and `pre-push` hooks by preserving and dispatching to
them; it must not silently discard a project-local hook. Use
`.agents/scripts/check-human-authorship metadata-tree` before publishing pull
request, release, contributor, author, credit, copyright, or package metadata.
Use `.agents/scripts/check-human-authorship all-history` for a repeatable audit
of every unique commit reachable from any local ref.

## Native Discovery

`.agents/skills` is the canonical skill location for this registry. Codex
discovers repository skills there directly, and other agents can read the same
plain files through the project-local `.agents` tree.

Live projects may keep a local `.agent -> .agents` compatibility symlink for
older tools and prompts. Do not make `.agent/` the canonical source again unless
the project explicitly rolls back the layout.

Keep root `AGENTS.md` as the short bootstrap for rules and docs that are not
skills. Do not rely on any runtime discovering `.agents/docs` without an
`AGENTS.md` pointer or an explicit prompt.

## Promotion Rule

Promote only the stable, cross-project instruction. Keep these local:

- exact commands and paths
- product, host, board, pin, node, account, or service names
- model/runtime exceptions needed by one repository
- safety rules tied to physical hardware or live services
- current status, roadmap, and handoff state

When a shared feature conflicts with a project-local rule, follow the project-local rule and record the reason in handoff or project rules.
