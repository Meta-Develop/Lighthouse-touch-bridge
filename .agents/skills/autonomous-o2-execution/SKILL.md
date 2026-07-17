---
name: autonomous-o2-execution
description: "Use when a managed project should run a bounded autonomous O2 top-supervisor loop with depth, round, peer-task, duplicate, safety-gate, and MACO controls."
---

# Autonomous O2 Execution Skill

Use this skill when a project-local task is large enough for a self-running O2
top supervisor, but must remain bounded by explicit budgets and project safety
rules.

The human/user-directed root O2 is a separate out-of-band supervisor. It may
launch several autonomous O2 supervisors and is not counted against autonomous
`task_depth` or `max_depth`. Each autonomous O2 is a bounded subprocess whose
long-running context is durable run state and ledger snapshots, not a single
growing LLM context.

Shared orchestration policy lives in
`.agents/skills/agent-orchestration/SKILL.md` blocks A-K. This skill owns only
the autonomous O2 wrapper contract, run ledgers, budgets, duplicate controls,
and peer-task queue behavior.

Before running the loop, read:

- `.agents/docs/AUTONOMOUS_O2_EXECUTION.md`
- `.agents/workflows/AUTONOMOUS_O2_EXECUTION_WORKFLOW.md`
- `.agents/skills/agent-orchestration/SKILL.md`
- project-local `AGENTS.md` and stricter local instructions

Start with a dry run when the scope or safety gates are uncertain:

```bash
.agents/scripts/o2-autopilot --task-file path/to/task.md --dry-run
```

Run with the default depth limit and separate round and peer-O2 bounds:

```bash
.agents/scripts/o2-autopilot --task-file path/to/task.md --max-depth 10 --max-rounds 3 --max-peer-o2 3
```

Required invariants:

- The autonomous loop does not bypass project-local safety, branch, privacy,
  hardware, live-service, validation, publication, or merge rules.
- The child O2 prompt follows `.agents/skills/agent-orchestration/SKILL.md`
  blocks A-K for shared role, launch, model, MACO, terminal-leaf, and
  subprocess policy.
- `STATE.tsv` and `HEARTBEAT.tsv` record the user-root/autonomous split,
  current phase, task counts, and durable ledger state.
- The initial task is depth `0`; peer tasks consume depth and peer-O2 budget.
- Duplicate scope keys are skipped.
- `NEXT_O2_TASKS.tsv` is only for newly discovered cross-cutting peer-O2 work;
  O2-to-O2 follow-up must use this durable queue state.
- Runtime artifacts stay under `.maco/o2-autopilot/` unless a redacted summary
  is intentionally promoted.

After the run, inspect `SUMMARY.md`, task `final.md` files, and any skipped
peer tasks before widening budgets or accepting the result.
