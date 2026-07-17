# Agent Files Registry

This project participates in the central `agent_files` registry.

At the start of a substantial work session, do this before changing project files:

1. Read `.agents/agent_files.config` to get `project_id` and registry hints.
2. Locate the registry. Prefer `AGENT_FILES_ROOT` if set, then the paths in `registry_hints`.
3. In the registry, run:

   ```bash
   scripts/check-feature-updates <project_id>
   ```

4. For each pending feature, read `features/<feature_id>/FEATURE.md`.
5. Adopt required features unless they conflict with documented project-local rules; record exceptions.
6. Adopt recommended features when they clearly fit the project.
7. If adopting into the live project, run:

   ```bash
   scripts/adopt-feature <project_id> <feature_id> <path-to-this-project>
   ```

Project-local rules remain authoritative. If a shared feature does not fit, record the reason in the handoff or project rules rather than silently ignoring it.

When a session changes `.agents/`, save that local-only context back into the
private registry before final handoff when practical. This includes changes to
`.agents/skills`, project-local workflows, handoff notes, and compact research
notes.

If only this live project's `.agents` changed and the registry copy should record
that state, use:

```bash
scripts/save-agent-state --dry-run <path-to-this-project>
scripts/save-agent-state <path-to-this-project>
```

Use `scripts/save-agent-state --push <path-to-this-project>` only when remote
publication is intended. If the dry run reports deletions, review them before
rerunning with `--allow-deletes`.

If both the live project and the registry mirror may have changed, preview a
bidirectional sync first:

```bash
scripts/sync-agent-state <path-to-this-project>
```

Apply only when the plan is conflict-free and the directions are intentional:

```bash
scripts/sync-agent-state --apply <path-to-this-project>
```

`sync-agent-state --apply` updates files and validates the registry, but it does
not create a Git commit. After applying, review `git status --short` in the
registry and commit the intended registry paths deliberately.

These registry scripts are not background daemons. A project agent must invoke
them explicitly; README or skill text only gives the instruction path.

Do not publish research notes, captured data, or `.agents` context outside the
private project `.agents` and this private `agent_files` registry unless the
owner explicitly approves it.
