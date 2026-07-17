# Agent Orchestration Workflow

This workflow is the operational skeleton. The canonical shared orchestration
policy blocks A-K live in `.agents/skills/agent-orchestration/SKILL.md`.

## Before Delegating

1. Read the project rules and current handoff/status notes.
2. Decide the immediate critical-path task.
3. Classify whether the task needs mutation, implementation, artifact
   generation, integration edit, or a broad validation fix.
4. If it needs hands-on work, apply `.agents/skills/agent-orchestration/SKILL.md`
   blocks A-C for manager boundary, delegated-worker availability, scope
   splitting, and completion persistence.
5. Select the launch path by applying `.agents/skills/agent-orchestration/SKILL.md`
   blocks D-J.
6. Assign file or subsystem ownership explicitly using the task template in
   `.agents/skills/agent-orchestration/SKILL.md` block K.
7. Group every currently unblocked independent scope into one dispatch wave;
   plan waves by dependency order, never one worker at a time.
8. Confirm project-local model, approved-agent, hardware, live-service, branch,
   privacy, and validation limits before launch.

## During Parallel Work

1. Launch the whole current wave concurrently; do not launch one worker and
   idle-wait for it before launching the next.
2. Keep Main/coordinator work inside `.agents/skills/agent-orchestration/SKILL.md`
   block A.
3. Do not duplicate an agent's assigned task.
4. When an agent finishes, inspect its changed files before accepting assumptions.
5. Launch newly unblocked scopes immediately; do not hold them until the whole
   wave completes, and do not sit idle while any launchable scope remains.
6. Resolve conflicts by preserving project-local rules and newer user direction.
7. Ask for a narrow follow-up fix instead of broadening a completed agent's scope silently.
8. Re-delegate rejected, incomplete, or risky results to a bounded owner.
9. Continue the loop until all required implementation, review, and validation work is resolved.

## Completion Standard

Finish only when one of these is true:

- the requested outcome is implemented, integrated, verified, and reported
- a concrete blocker prevents further progress and is reported with evidence
- the user explicitly pauses or narrows the task
- a deliberate handoff records remaining tasks, owners, validation state, and the next safe step

Use `.agents/skills/agent-orchestration/SKILL.md` block C for completion
persistence.

## Handoff

Record:

- agents/tasks launched
- files or subsystems touched
- results accepted
- results rejected or deferred
- verification performed
- next safe task
