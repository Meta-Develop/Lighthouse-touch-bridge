# Agent Orchestration

This document provides long-form framing for when to use manager-led
orchestration. The canonical shared orchestration policy blocks A-K live in
`.agents/skills/agent-orchestration/SKILL.md`; do not duplicate those policy
blocks here.

Project-local orchestration rules remain authoritative. If a project has
stricter approved-agent names, model rules, hardware limits, live-service
constraints, branch rules, or validation gates, follow them.

## Parallel Throughput Objective

Manager-led orchestration exists to raise both speed and output quality:
independent scopes progress concurrently while review gates protect precision.
N workers used one at a time deliver neither — that is solo work with extra
overhead. Decompose to safely independent logic units, dispatch every
unblocked scope together, and keep the coordinator busy reviewing and
preparing the next wave while workers run. The binding policy wording lives in
`.agents/skills/agent-orchestration/SKILL.md` block C.

## Non-Trivial Work Taxonomy

Non-trivial work includes tasks that:

- touch multiple files, subsystems, chapters, services, devices, or interfaces
- require research plus implementation plus verification
- can be split into independent exploration, implementation, review, or validation scopes
- are user-facing fixes, feature work, project-level changes, reviews, migrations, or long-running investigations

## Delegated Scope Examples

Typical delegated scopes include:

- separate files or subsystems with low overlap
- read-only exploration of distinct questions
- implementation tasks with disjoint ownership
- review and verification that can happen while implementation continues
- project-local rule, documentation, artifact, or handoff updates that can be isolated from implementation

Do not delegate yet when:

- the next step is blocked on one answer and no useful side work exists
- multiple agents would edit the same files
- the project state is unclear enough that delegation would duplicate confusion
- hardware or deployment actions require one operator to keep context

These exceptions do not permit Main/coordinator mutation. They mean the
coordinator should keep work read-only, request or spawn one clearly bounded
worker, or report that the requested mutation is blocked by the lack of a safe
delegated path.

## Policy Pointer

For manager boundaries, standing permission, delegation availability, completion
persistence, tiered launch policy, Claude-root execution split, model defaults,
manual launch preflight, no-blind-retry, anti-probe, MACO usage, terminal-leaf
prohibitions, review-auditor boundaries, O2/O1 Codex CLI subprocess authority,
and the task assignment template, follow `.agents/skills/agent-orchestration/SKILL.md`
blocks A-K.

## External Orchestrator Entrypoint

For external orchestrator entrypoint policy, follow
`.agents/skills/agent-orchestration/SKILL.md` block H.
