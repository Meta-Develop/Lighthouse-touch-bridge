---
name: agent-orchestration
description: "Use when starting any non-trivial task, explicit multi-agent request, parallel work, delegation, or handoff coordination across independent scopes."
---

# Agent Orchestration Skill

Use this skill for any non-trivial task. This file is the canonical owner for
shared orchestration policy blocks A-K. Other shared feature payloads should
point here instead of restating these blocks.

Project-local orchestration rules remain authoritative. If a project has
stricter approved-agent names, model rules, hardware limits, live-service
constraints, branch rules, or validation gates, follow them.

## Canonical Orchestration Policy Blocks A-K

### A. Manager Boundary

The top-level Main/coordinator is a manager, not a hands-on worker. It plans,
delegates, reviews, verifies, and reports. It must not directly mutate files,
implement patches, generate durable artifacts, perform integration edits, or do
broad hands-on project work.

Main/coordinator direct work is limited to read-only context gathering,
planning and task decomposition, delegated task assignment, reviewing diffs,
findings, and artifacts, running or checking final verification when that does
not mutate project files, handoff, and user reporting.

If the current agent was explicitly launched as a delegated worker, it may work
only inside its assigned role, scope, ownership boundary, write boundary, avoid
list, validation contract, and final report requirements.

### B. Standing Permission And Delegation Availability

Subagent/delegated-worker use is mandatory for non-trivial hands-on work. The
owner's AGENTS.md and project instructions are standing authorization and a
standing request to use SubAgents/delegated workers. Do not require the user to
type `Sub agent` or another prompt-level token each turn before spawning a
delegated worker.

The only delegation blockers are a runtime that literally has no
delegated-worker mechanism/tool available, or a higher-priority instruction that
explicitly forbids spawning despite the standing owner request. A missing
per-turn phrase, keyword, or prompt-level token is not a blocker and is not
permission to continue solo. In true blocker cases, continue only with
read-only coordination, non-mutating verification, user reporting, or another
permitted delegated worker path; stop before mutation and report the exact
blocked worker task.

### C. Work Splitting, Parallel Dispatch, And Completion Persistence

Split non-trivial work into research, implementation, review, and validation
scopes. When delegated workers are available, launch them before any file
mutation, implementation patch, artifact generation, or integration edit, and
use multiple workers when multiple independent scopes can safely progress in
parallel; do not funnel independent scopes through a single worker. Delegate
only concrete, bounded subtasks with explicit ownership, and prefer independent
tasks with disjoint file or subsystem ownership. Tell each agent that other
agents may also be working in the repository. Do not delegate the immediate
blocker if the main agent needs that result before doing anything else. Keep a
short local record of delegated tasks and outcomes.

Decompose to the finest grain that stays safely independent: split by logic
unit (file, subsystem, module, chapter, question, or validation axis) until
further splitting would create overlapping ownership or coordination overhead
larger than the subtask itself. The throughput target is proportional speedup:
N independent scopes run by N concurrent workers should approach N-times the
throughput of one worker. Launching N workers one at a time and waiting on
each delivers no speedup over solo work while adding coordination cost, and is
a policy violation, not a style choice.

Launch every currently unblocked independent scope as one concurrent dispatch
wave — in a single batch or back-to-back without awaiting intermediate results.
Await results only after every currently launchable scope has been dispatched.
While a wave runs, the parent must not sit idle waiting on one worker: it
reviews results as each worker finishes, immediately launches follow-up or
newly unblocked scopes without waiting for the rest of the wave, prepares the
next wave's task assignments, or gathers read-only context. Blocking on a
single worker is acceptable only when its output is a hard dependency for
every remaining task and no useful read-only coordination work exists.

Continue coordinating until the requested outcome is implemented, integrated,
verified, and reported; concretely blocked and reported with evidence;
explicitly paused or narrowed by the user; or deliberately handed off with
remaining tasks, ownership, validation state, and the next safe step. Do not
stop after one delegated task, first patch, or first passing check when
unresolved subtasks, integration work, fixes, validation gaps, or user-requested
scope remain.

### D. Tiered Launch Policy

Use this tiered launch policy:

- Simple bounded terminal leaf work uses native host SubAgent or another
  runtime-native delegated worker path with exact role, scope, write boundary,
  avoid list, validation, and final report requirements.
- Moderately complex non-leaf coordination uses O1. Launch or structure O1
  through MACO when `.agents/scripts/maco` is available; otherwise use Codex
  CLI subprocess workflows with `--enable goals`, `--enable multi_agent`, and
  `--sandbox danger-full-access`.
- Large, cross-cutting, long-running, or autonomous work uses O2. Launch or
  structure O2 through MACO when available, or use a project-approved O2 wrapper
  such as `.agents/scripts/o2-autopilot` or an approved Codex CLI O2
  subprocess.

Bind generic subagents to approved identities when exact custom-agent names are
unavailable. Preserve project-local model, safety, and ownership rules.

### E. Claude-Root Execution Split

When the human-invoked root agent is a Claude-family runtime (for example
Claude Fable in Claude Code), that Claude root is the user-directed root
strategist by default in every managed project.

The Claude root owns user-intent capture, read-only context gathering,
decomposition, strategic planning, delegation, acceptance review,
non-mutating final verification, and user reporting. The existing manager
boundary applies unchanged: the Claude root performs no hands-on mutation
itself.

Non-trivial hands-on execution is delegated to Codex through MACO by default:
large, cross-cutting, long-running, or autonomous scopes go to a MACO O2 top
supervisor; moderately complex bounded workstreams go to a MACO O1 child
orchestrator. Under a Claude root, MACO-launched Codex O1/O2 is the default
execution engine for implementation work, and those Codex orchestrators own
their internal worker/researcher/review-auditor trees under the existing
terminal-leaf rules.

The Claude root must not build implementation trees out of native Claude
subagents when a MACO or approved Codex CLI O1/O2 path is available. Native
host SubAgent under a Claude root is reserved for read-only research, read-only
review support, and clearly trivial, low-risk bounded leaf edits where
launching a MACO O1 would be unjustified overhead.

The execution-layer model policy follows block F: Codex O1/O2 work runs the
fixed GPT-5.6-series standard model `gpt-5.6-sol` at the `xhigh`
reasoning-effort baseline, falling back to the strongest available
GPT-5-series model when the runtime exposes no GPT-5.6-series slug. If MACO and
approved Codex CLI subprocess paths are both unavailable in the current runtime,
fall back to the tiered launch policy with runtime-native delegated workers and
report the degraded execution path.

This split changes runtime binding only. Durable role boundaries, terminal-leaf
prohibitions, sandbox requirements, anti-probe and no-blind-retry rules, and
stricter project-local policies remain authoritative.

A stated purpose of this split is cost efficiency: offloading hands-on execution
to the Codex layer reduces Claude-side token consumption while MACO
orchestration, review gates, and standard-model execution preserve output
quality.
The Claude root keeps its own context lean; heavy exploration, long file reads,
and iterative implementation loops belong in the delegated Codex layer, not the
root context. Cost saving must not skip planning, acceptance review, or
verification duties.

### F. Model And Manual Launch Preflight

The standard execution model family is the GPT-5.6 series. The standard model
is `gpt-5.6-sol` (or the closest label the runtime exposes for it), and the
model choice is fixed for every role: do not select other GPT-5.6 variants,
neither higher tiers such as `Ultra` nor cheaper tiers such as
`gpt-5.6-terra`. Reasoning effort, not model choice, is the scaling knob.
When the spawn/delegation API exposes per-agent model and reasoning
overrides, request `gpt-5.6-sol` with the chosen reasoning effort explicitly
instead of relying on inherited defaults.

The reasoning-effort ladder applies to every role, O1/O2 orchestrators
included. The baseline is `xhigh`. Raise to `max` for genuinely difficult
work, and to `ultra` (where the runtime exposes it) only for the very hardest
work. Clearly easy, low-risk, tightly bounded tasks may run below `xhigh`.
Acceptance-gate review duties must not run below the `xhigh` baseline, and a
below-baseline result still needs its diff and evidence checked by the parent
or a review-auditor gate before acceptance.

If the runtime exposes no GPT-5.6-series model, fall back to the strongest
available GPT-5-series model (for example `gpt-5.5`) using the same
reasoning-effort ladder and state the fallback in the task or session
report. Wrappers such as `o2-autopilot` default to `gpt-5.6-sol`/`xhigh`; on
a runtime without a GPT-5.6-series slug, override the model to the strongest
available GPT-5-series model instead of passing unsupported slugs. If a
required override is unavailable and policy is strict, report the blocker
instead of silently falling back.

Before a manual Codex CLI O1/O2 launch, inspect `codex exec --help` and
`codex debug models` read-only.

### G. No-Blind-Retry And Anti-Probe

Do not retry the same O1/O2 prompt by cycling unsupported model names, service
tiers, config-bypass flags, or equivalent launch-option guesses. If a selected
launch fails because of model, service-tier, or config compatibility, fix the
proven cause once or report a concrete blocker instead of blind retrying.

Do not launch child Codex sessions solely to test delegation model labels,
service tiers, runtime labels, or exact-echo prompts such as `Say exactly:
delegation model probe OK`. Inspect current runtime tool metadata,
`codex --help`, `codex exec --help`, `codex debug models`, or configuration
files read-only instead. If a real delegated task cannot be launched safely
under the current runtime and project rules, report a concrete delegation
blocker.

### H. MACO Use And External Entrypoint

MACO is for O1/O2 orchestration and repo-map, sync, claim, review, and merge
gates when useful or project-required. It is not a terminal worker spawning
surface.

For projects with `.agents/external/multi-agent-coding-orchestrator`, use
`.agents/scripts/maco` as the stable project-local entrypoint for external
orchestrator commands. `.agents/scripts/maco` runs the registry-managed external
package from the attached
`.agents/external/multi-agent-coding-orchestrator/Cargo.toml`, not the local
experimental Orchestrator repository.

The manager boundary remains mandatory. The Main/coordinator still plans,
delegates, reviews, verifies, and reports; hands-on work remains assigned to
delegated workers.

### I. Terminal-Leaf Prohibitions And Review-Auditor Boundary

Do not launch MACO `worker`, `researcher`, `review-auditor`, or equivalent
terminal leaf roles. Do not wrap terminal leaf roles in raw Codex CLI
subprocess prompts such as `codex exec ... ROLE=TERMINAL_WORKER`,
`ROLE=RESEARCHER`, `ROLE=REVIEW_AUDITOR`, or prompts described as delegated
workers, delegated implementation workers, delegated preflight workers,
delegated validation workers, delegated cleanup workers, researchers, or review
auditors.

If native terminal-leaf capacity is unavailable, wait, narrow the task, report
a capacity blocker, or escalate to O1/O2 only when the task complexity warrants
non-leaf coordination.

Workers are terminal leaf roles and do not delegate. Researchers are terminal
leaf roles and remain read-only. Review auditors remain terminal and read-only:
they inspect worker reports, diffs, validation evidence, remaining risk, and
path-boundary compliance. Review auditors do not mutate files, run mutating
commands, claim write scopes, create durable implementation artifacts, or
delegate. Acceptance-gate review auditor evidence is MACO parent-enforced
structured evidence, not a free-standing delegating role.

### J. O2/O1 Codex CLI Subprocess Authority

O2/O1 roles that may delegate should run through MACO when `.agents/scripts/maco`
is available; otherwise they may use Codex CLI subprocess workflows with
`--enable goals`, `--enable multi_agent`, and `--sandbox danger-full-access`.
For O2/O1 subprocess chains, `danger-full-access` is the standard/required
minimum Codex CLI sandbox, not merely an allowed or recommended option.

Do not use native host SubAgent for O2 or O1 roles that may delegate. Runtime
authority does not broaden the prompt-bound role, scope, write boundary, banned
operations, validation, or report contract. Stricter project or
higher-priority safety rules may still block a launch.

### K. Task Assignment Template

When assigning work, include:

- Goal
- Scope
- Role
- Parent or coordinator
- Files, directories, or subsystems owned
- Write access
- Files, directories, commands, or operations to avoid
- Expected final output
- Verification command or review criteria
- Completion condition
- Final report requirements
