---
name: runtime-model-policy
description: "Use when an approved model, subagent, custom-agent, or delegation name is unavailable or exposed under a broader GPT-5.6-series or GPT-5-series runtime label."
---

# Runtime Model Policy

Before stopping on a model-name mismatch, read `.agents/docs/RUNTIME_MODEL_POLICY.md`
when present. Otherwise read `.agents/docs/RUNTIME_MODEL_POLICY_SHARED.md`.

Shared orchestration policy lives in
`.agents/skills/agent-orchestration/SKILL.md` blocks A-K. This skill owns only
model-label fallback and runtime bridge naming.

The standard execution model family is the GPT-5.6 series with standard model
`gpt-5.6-sol`; runtimes without a GPT-5.6-series model fall back to the
strongest available GPT-5-series model, per
`.agents/skills/agent-orchestration/SKILL.md` block F. The model choice is
fixed; scale reasoning effort instead (baseline `xhigh`, per the block F
effort ladder).

Continue when all are true:

- the runtime is GPT-5.6-series, or GPT-5-series as the stated fallback
- the task can be constrained to the approved role boundary
- file ownership and validation duties can be stated explicitly
- project `AGENTS.md` or `.agents` instructions authorize delegated workers as
  the owner's standing request and permission

Determine model, service-tier, and delegation capability from current runtime
metadata, `codex --help`, `codex exec --help`, `codex debug models`,
configuration files, or a real safely scoped delegated task. Follow
`.agents/skills/agent-orchestration/SKILL.md` blocks F-G for manual launch
preflight, anti-probe, and no-blind-retry requirements.

When assigning runtime bridge labels, keep canonical durable roles in `ROLE`
and put runtime-specific labels such as `expert-coder`, `expert-reviewer`,
`expert-explorer`, or `strict-orchestrator` in `AGENT_LABEL` or equivalent
metadata. Exact custom-agent names are bridge labels only and must not become
durable role semantics.

When delegating to Codex CLI, require the session to enable and use Codex
`goals` for the task. O2/O1 roles that may delegate must also enable Codex
`multi_agent`. Shared Codex CLI launch authority and terminal-leaf restrictions
are owned by `.agents/skills/agent-orchestration/SKILL.md` blocks H-J.

The prompt and goal must preserve role, scope, ownership, validation, banned
operations, and final reporting boundaries.

Stop and report a blocker only when the runtime has no delegated-worker
mechanism/tool at all, a higher-priority instruction explicitly forbids spawning
despite the standing owner request, no GPT-5-series delegated path exists, or
the runtime cannot enforce a bounded role.
