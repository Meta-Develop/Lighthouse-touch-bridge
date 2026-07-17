---
name: core-agent-skills
description: "Use when starting work in a managed project, reviewing .agents skill coverage, promoting repeated rules to agent_files features, or deciding whether a rule belongs in universal, profile, feature, or project-local context."
---

# Core Agent Skills

Use project-local skills first. This skill is a routing and promotion guide; it
must not weaken stricter local rules. Use `core-skill-baseline` when a managed
project is missing a more specific local skill for common engineering workflow.
That fallback includes organization-owned repository confidentiality and
publication-gate guidance for private/org projects.

## Startup Routing

1. Read `.agents/docs/PROJECT_RULES.md` or the project entrypoint.
2. Load the most specific skill that matches the task.
3. If no specific local skill exists, load `.agents/skills/core-skill-baseline/SKILL.md`.
4. If the task needs software, downloads, packages, tools, or dependencies that
   may require persistent host integration, load
   `.agents/skills/host-software-requests/SKILL.md`.
5. If a shared rule and local rule conflict, follow the local rule.
6. Verify `.agents/scripts/install-human-authorship-guard --check`; install the
   composing commit hooks when the check reports that they are missing.

## Native Skill Path

Use `.agents/skills` as the canonical skill path. Codex discovers that path
natively; other agents can read the same files directly. A live project may keep
`.agent -> .agents` as a compatibility symlink for older prompts or tools.

## Promotion Test

Promote a rule to `agent_files/features` only when it is:

- useful in at least two projects or clearly reusable for future projects
- independent of project-specific paths, commands, hardware, accounts, and current status
- strong enough to preserve quality without adding noisy process

Keep it project-local when it depends on exact environment details or raises safety risk if generalized.

## Authorship Boundary

Shared human-authorship enforcement is a required baseline, not an optional
project preference. A project may add stricter publication rules, but it must
not identify a model, vendor, runtime, or agent as author, committer,
co-author, contributor, reviewer, copyright owner, implementation credit, or
generator. Preserve real human credits and ordinary technical mentions.
