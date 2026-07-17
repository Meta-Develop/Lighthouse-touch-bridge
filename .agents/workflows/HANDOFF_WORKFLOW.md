# Handoff Workflow Base

Use this as a seed for `.agents/workflows/HANDOFF_WORKFLOW.md`.

## Before Starting

1. Read `.agents/docs/PROJECT_RULES.md`.
2. Read the current project plan or status document.
3. Check `git status` in the project repository.
4. Identify files that appear to have user changes before editing.

## During Work

1. Keep edits scoped to the requested task.
2. Treat the current Git repository as the mutation boundary. For findings in
   another Git-backed repository, stop before mutation and preserve a
   maintainer-facing issue, local issue draft, or handoff with repo-relative
   evidence unless the user explicitly changes scope or a delegated worker is
   launched in that target repository context.
3. For non-trivial hands-on work, keep the top-level agent in manager/coordinator mode and delegate mutation, implementation, artifact generation, integration edits, and broad validation fixes before mutation.
4. Treat root `AGENTS.md` and project-local `.agents` instructions as the owner's standing request and permission to use SubAgent/delegated workers automatically; do not wait for a fresh per-turn `Sub agent` phrase.
5. If no delegated-worker mechanism/tool exists at all, or a higher-priority explicit instruction forbids delegation despite the standing owner request, stop before mutation and report the exact blocked worker task.
6. Update project-local `.agents` files when they would reduce future agent ambiguity.
7. Keep transient notes in `.agents/temp` until they are worth promoting.

## Before Finishing

1. Run relevant verification.
2. Update handoff/status notes when the next step changed.
3. Capture `.agents` changes into `agent_files` when they are durable.
