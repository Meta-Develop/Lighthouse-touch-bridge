---
name: agent-files-registry
description: "Use when starting work in a project that has .agents/agent_files.config, checking pending shared features, adopting agent_files registry features, or synchronizing project-local .agents changes."
---

# Agent Files Registry Skill

Use this skill when starting work in a project that has `.agents/agent_files.config`, or when updating or synchronizing project-local `.agents` features.

## Startup Check

1. Read `.agents/agent_files.config`.
2. Locate the `agent_files` registry using `AGENT_FILES_ROOT`, then `registry_hints`.
3. Run `scripts/check-feature-updates <project_id>` from the registry.
4. Read pending feature notes before adopting anything.
5. Use `scripts/adopt-feature <project_id> <feature_id> <live_project_dir>` for accepted features.

## Adoption Rules

- Required features must be adopted unless they conflict with documented project-local rules; record exceptions.
- Recommended features are opt-in when they fit the project.
- Never overwrite project-specific rules without reading the target file first.
- After adoption, save the resulting `.agents` changes back to this private registry.

## Synchronize Local Agent State

When a session changes `.agents/`, save that local-only context before final handoff when practical. `.agents/skills`, workflows, compact research notes, and handoff notes all count.

If only the live project's `.agents` changed and the registry should record it:

1. From the registry, preview the capture:

   ```bash
   scripts/save-agent-state --dry-run <live_project_dir>
   ```

2. If the plan is expected, create a registry commit:

   ```bash
   scripts/save-agent-state <live_project_dir>
   ```

3. Publish the registry commit only when remote update is intended:

   ```bash
   scripts/save-agent-state --push <live_project_dir>
   ```

Do not use `--allow-deletes` unless the dry-run deletion list has been reviewed and the deletions are intentional.

If both sides may have changed, use the conflict-checked bidirectional sync path:

1. Preview first:

   ```bash
   scripts/sync-agent-state <live_project_dir>
   ```

2. Apply only if the plan is conflict-free and intentional:

   ```bash
   scripts/sync-agent-state --apply <live_project_dir>
   ```

3. Review and commit the intended registry changes deliberately:

   ```bash
   git status --short
   scripts/validate-agent
   ```

`sync-agent-state --apply` does not create a Git commit. `save-agent-state` creates a scoped local registry commit by default, and `--push` is only for intentional remote publication.

These scripts are not automatic daemons. The project agent must explicitly invoke them when `.agents` changes.

Do not publish research notes, captured data, or `.agents` context outside the private project `.agents` and this private `agent_files` registry unless the owner explicitly approves it.
