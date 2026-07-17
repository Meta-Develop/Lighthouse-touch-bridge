# Autonomous O2 Execution Workflow

Shared orchestration policy lives in
`.agents/skills/agent-orchestration/SKILL.md` blocks A-K. This workflow covers
only the autonomous O2 wrapper, run ledgers, budgets, and peer-task queue.

## Start

1. Read `AGENTS.md` and the relevant `.agents` docs, workflows, and skills.
2. Confirm that an autonomous O2 loop is appropriate for the task and that the
   human/user-directed root O2 remains out-of-band from autonomous depth.
3. Apply `.agents/skills/agent-orchestration/SKILL.md` blocks A-K before any
   launch or handoff decision.
4. Use the default max depth of 10 unless the task needs a tighter depth cap;
   keep round and peer-O2 task bounds separate.
5. Prepare the initial task as either a task file or a concise `--task` string.
6. Run a dry run first when the prompt, scope, or safety gates are uncertain.

## Run

1. Start the wrapper from the live project:

   ```bash
   .agents/scripts/o2-autopilot --task-file path/to/task.md --dry-run
   .agents/scripts/o2-autopilot --task-file path/to/task.md
   ```

2. Confirm the run directory in `.maco/o2-autopilot/runs/<run-id>/`.
3. Inspect `STATE.tsv` and `HEARTBEAT.tsv` for the out-of-band user-root O2
   policy, autonomous task depth, current phase, and durable ledger snapshots.
4. Inspect `preflight/git-status.txt`, `preflight/codex.txt`, and
   `preflight/maco.txt`.
5. For each executed task, inspect:
   - `prompt.md`
   - `events.jsonl`
   - `final.md`
   - `NEXT_O2_TASKS.tsv`, when present
6. Treat failed MACO repo-map or sync-status captures as evidence, not an
   automatic failure. Stop only when the project-local gate requires that
   evidence before mutation.

## Child O2 Expectations

1. Start from the generated `ROLE: O2_TOP_SUPERVISOR` contract.
2. Treat the human/user-directed root O2 as a separate out-of-band supervisor;
   this subprocess is an autonomous O2 with bounded `task_depth`.
3. Keep long-running context in durable ledger snapshots, not a single growing
   LLM context.
4. Read project-local startup instructions before acting.
5. Follow `.agents/skills/agent-orchestration/SKILL.md` blocks A-K for shared
   role, launch, MACO, model, terminal-leaf, subprocess, and handoff policy.
6. Write `NEXT_O2_TASKS.tsv` only for newly discovered cross-cutting peer-O2
   tasks. O2-to-O2 follow-up must use this durable queue state.
7. Preserve unrelated dirty state and exact write-scope ownership.
8. Stop and report blockers when safety, hardware, secrets, publication,
   branch, or merge gates are not satisfied.

## Finish

1. Read `SUMMARY.md`.
2. Inspect `STATE.tsv`, `HEARTBEAT.tsv`, and every `final.md` before accepting
   the outcome.
3. Review skipped peer tasks before increasing budgets.
4. Run the smallest relevant project verification command.
5. If delegated work produced changes, use project-local merge and handoff
   gates before committing, pushing, publishing, or touching protected branches.
6. Record remaining risks and the next safe action in the final handoff.
