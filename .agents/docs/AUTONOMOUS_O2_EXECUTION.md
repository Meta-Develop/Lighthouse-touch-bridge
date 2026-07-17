# Autonomous O2 Execution

This feature defines project-local, self-running, and bounded autonomous O2
supervisors launched beneath a separate human/user-directed root O2. The
human-invoked agent or user-root O2 is out-of-band, may launch several bounded
autonomous O2 supervisors, and is not counted against autonomous `task_depth`
or `max_depth`.

Shared orchestration policy for manager boundaries, standing delegation
permission, tiered launch selection, Claude-root handling, model defaults,
manual launch preflight, anti-probe and no-blind-retry rules, terminal-leaf
prohibitions, review-auditor boundaries, MACO usage, and O2/O1 subprocess
authority lives in `.agents/skills/agent-orchestration/SKILL.md` blocks A-K.
This feature adds only the autonomous O2 wrapper, durable ledgers, depth/round
budgets, duplicate controls, and peer-task queue contract.

Autonomous O2 execution is an execution aid, not a permission bypass.
Project-local `AGENTS.md`, `.agents` instructions, branch rules, validation
gates, live-service limits, hardware safety gates, publication gates, and
secret-handling policy remain authoritative.

Use `.agents/scripts/o2-autopilot` when an autonomous O2 supervisor should run
a bounded task, record durable runtime artifacts, and optionally request a
small number of newly discovered peer/child O2 tasks through durable queue
state.

Long-running supervision context must live in durable run state and ledger
snapshots, not in one growing LLM context. The wrapper records `STATE.tsv`,
`HEARTBEAT.tsv`, `queue.tsv`, `skipped.tsv`, task prompt/final/event files,
optional `NEXT_O2_TASKS.tsv`, and `SUMMARY.md` under the run directory.

## Autonomous O2 Loop

The autopilot wrapper:

1. Determines the project root from `.agents/scripts/o2-autopilot`.
2. Creates a run directory under `.maco/o2-autopilot/runs/<run-id>/`.
3. Records preflight evidence, including Git status, Codex availability, MACO
   availability, and best-effort MACO repo-map and sync-status captures.
4. Writes `STATE.tsv` and `HEARTBEAT.tsv` with the out-of-band user-root O2
   policy, autonomous depth policy, current phase, task counts, and ledger
   files.
5. Queues the initial O2 task from `--task-file`, `--task`, or remaining CLI
   arguments.
6. Runs pending O2 tasks sequentially up to the configured round and depth
   limits.
7. Updates `HEARTBEAT.tsv` at meaningful phases: initialized, task running,
   task completed, task failed, task skipped, and summary written.
8. Writes each child prompt, event stream, final message, and optional
   peer-task file under that task's run directory.
9. Ingests `NEXT_O2_TASKS.tsv` only when peer budget, depth budget, and
   duplicate controls allow it.
10. Writes `SUMMARY.md` with completed, failed, and skipped counts plus the next
   safe action.

Each generated child prompt starts with:

```text
ROLE: O2_TOP_SUPERVISOR
```

The child O2 must read project-local startup instructions before acting and
must follow `.agents/skills/agent-orchestration/SKILL.md` blocks A-K for shared
orchestration policy. The generated prompt states that the user-root O2 is
out-of-band, that this subprocess is an autonomous O2 with bounded
`task_depth`, and that O2-to-O2 follow-up must go through `NEXT_O2_TASKS.tsv`
and durable queue state.

## Safety Gates

Autonomous O2 execution must not bypass:

- project-local branch or dirty-worktree rules
- write-scope ownership and unrelated-change preservation
- manager-only or delegated-worker requirements
- O2/O1 subprocess delegation boundaries
- hardware, bench, vehicle, actuator, robotics, or live-service safety gates
- secret, credential, private-data, and local-only artifact boundaries
- publication, release, package, default-branch push, and merge approval gates
- destructive-command restrictions and broad cleanup prohibitions

If a required safety gate is unclear or unavailable, the child O2 must stop and
report the blocker instead of assuming permission.

## Depth, Budget, And Duplicate Controls

The wrapper is bounded by:

- `--max-depth N`, default `10`: the initial task is depth `0`; peer tasks are
  one level deeper than their parent.
- `--max-rounds N`, default `3`: the maximum number of O2 tasks executed in one
  run.
- `--max-peer-o2 N`, default `3`: the maximum number of peer-O2 tasks added
  from child `NEXT_O2_TASKS.tsv` files.
- Seen scope keys: each queued task records a scope key. Duplicate scope keys
  are skipped.

When a child does not know a stable scope key, it may leave the first TSV field
blank and let the wrapper derive a key from the task file path. Prefer explicit
keys for cross-cutting scopes that could be discovered through multiple paths.

## Peer O2 Tasks

Autonomous O2 supervisors may write `NEXT_O2_TASKS.tsv` in their assigned task
directory to request bounded peer/child O2 work. Each row must be:

```text
scope_key<TAB>task_file<TAB>reason
```

Use this file only for newly discovered cross-cutting problems that genuinely
need a peer O2 supervisor. Do not queue ordinary follow-up work, local fixes,
or work that belongs inside the current O2 scope. O2-to-O2 follow-up must use
this durable queue path. Put the peer task prompt in a separate task file.
Relative task paths are resolved from the current task directory.

## Live Project Rules

The autopilot operates inside the live project where its `.agents/scripts`
payload is installed. It must not edit adjacent Git repositories or project
mirrors unless the project-local instructions and explicit user scope allow
that mutation. Cross-repository findings should become a maintainer-facing
issue, local issue draft, or handoff.

Runtime artifacts belong under `.maco/o2-autopilot/`. Raw event streams,
temporary task files, and local logs should stay local unless a project
explicitly promotes a redacted summary.

## MACO Usage

When `.agents/scripts/maco` is available, the O2 child should use it for O1/O2
orchestration and gates when useful or project-required:

- `repo map` before decomposition when repository structure matters
- `sync status` before claiming or assigning overlapping work
- supported claim and scope coordination before overlapping assignments
- supported review and merge gates before integrating delegated worktree results
- launching or structuring O1/O2 delegating orchestration flows

Shared MACO boundaries and terminal-leaf restrictions live in
`.agents/skills/agent-orchestration/SKILL.md` blocks H-I.

MACO failures do not automatically make the task impossible. They are evidence
to report and may become blockers when the project requires MACO gates for that
operation.
