# Runtime Model Policy Shared

Use this policy when project rules prefer a specific GPT-5.6-series or
GPT-5-series model or approved custom-agent names, but the current runtime
exposes only a broader label or generic subagent interface.

Project-specific runtime policy files remain authoritative. If
`.agents/docs/RUNTIME_MODEL_POLICY.md` exists, read it first and use this shared
file only as fallback.

Shared orchestration policy for standing delegation permission, launch tiers,
Claude-root handling, model defaults for non-trivial Codex delegation, manual
launch preflight, anti-probe and no-blind-retry behavior, terminal-leaf
restrictions, MACO usage, review-auditor boundaries, and O2/O1 subprocess
authority lives in `.agents/skills/agent-orchestration/SKILL.md` blocks A-K.
This feature owns only model-label fallback and runtime bridge naming.

## Policy

- Prefer the strongest explicitly approved model named by the project.
- When project rules name no specific model, the standard execution model
  family is the GPT-5.6 series and the standard model is `gpt-5.6-sol` (or the
  closest label the runtime exposes for it), per
  `.agents/skills/agent-orchestration/SKILL.md` block F.
- The model choice is fixed; reasoning effort is the scaling knob, per the
  block F effort ladder (baseline `xhigh`, `max` for genuinely difficult work,
  `ultra` only for the very hardest work, below `xhigh` for clearly easy
  bounded tasks).
- If the runtime exposes no GPT-5.6-series model, fall back to the strongest
  available GPT-5-series model using the same reasoning-effort ladder and
  state the fallback in the task or session report.
- If the runtime exposes only `GPT-5`, `GPT-5-series`, or hides the minor
  version, continue with the available GPT-5-series model using the same
  reasoning-effort ladder.
- Do not stop solely because the runtime label is broader than the preferred
  minor version.
- Do not use pre-GPT-5 models, non-approved model families, or unconstrained
  generic agents for required delegated work.
- Determine model, service-tier, and delegation capability from current runtime
  metadata, `codex --help`, `codex exec --help`, `codex debug models`,
  configuration files, or a real safely scoped delegated task. Use
  `.agents/skills/agent-orchestration/SKILL.md` blocks F-G for manual launch
  preflight, anti-probe, and no-blind-retry requirements.
- Assign the canonical durable role first: `TERMINAL_WORKER`, `RESEARCHER`,
  `REVIEW_AUDITOR`, `O1_CHILD_ORCHESTRATOR`, or `O2_TOP_SUPERVISOR`.
- Durable prompts and reusable instructions must put only the canonical durable
  role in `ROLE`. Do not put runtime labels such as `expert-coder`,
  `expert-reviewer`, `expert-explorer`, or `strict-orchestrator` in `ROLE`.
- Exact custom-agent names are runtime bridge labels only. Use them only when
  the runtime requires or exposes them, preferably as `AGENT_LABEL` or
  equivalent bridge metadata next to the canonical `ROLE`.
- If exact custom-agent names are hidden but a generic GPT-5-series subagent can
  be constrained by prompt, use the generic subagent and bind it to the
  canonical durable role, ownership, and validation duties. Do not treat the
  missing custom name as a blocker.
- Stop and report a blocker only when the runtime has no delegated-worker
  mechanism/tool at all, a higher-priority instruction explicitly forbids
  spawning despite the standing owner request, no GPT-5-series delegated path
  exists, or the runtime cannot enforce a bounded role.

## Runtime Bridge Labels

Start every delegation prompt from the durable role contract, then add bridge
labels only if the runtime needs them:

- For implementation work assigned through native host SubAgent or another
  approved runtime-native terminal-leaf delegation mechanism, use
  `ROLE=TERMINAL_WORKER`. This role assignment is bridge metadata, not a launch
  permission. If the runtime requires or displays `expert-coder`, render it as
  `AGENT_LABEL=expert-coder`.
- For read-only research assigned through native host SubAgent or another
  approved runtime-native terminal-leaf delegation mechanism, use
  `ROLE=RESEARCHER`. This role assignment is bridge metadata, not a launch
  permission. If the runtime requires or displays `expert-explorer`, render it
  as `AGENT_LABEL=expert-explorer`.
- For read-only review or acceptance evidence assigned through native host
  SubAgent, approved runtime-native terminal-leaf delegation, or MACO
  parent-enforced acceptance gates, use `ROLE=REVIEW_AUDITOR`. This role
  assignment is bridge metadata, not a launch permission. If the runtime
  requires or displays `expert-reviewer`, render it as
  `AGENT_LABEL=expert-reviewer`. For acceptance gates, use MACO
  parent-enforced structured review-auditor evidence when the project gate
  requires it.
- For O1/O2 delegation chains, use `ROLE=O1_CHILD_ORCHESTRATOR` or
  `ROLE=O2_TOP_SUPERVISOR`. If the runtime requires or displays
  `strict-orchestrator`, render it as `AGENT_LABEL=strict-orchestrator` only
  when that bridge label matches the assigned durable role.

Every generic subagent prompt should include the canonical durable role,
optional bridge label, model/fallback policy, read/write boundary, owned files
or directories, banned git operations, validation duties, and final report
requirements. Use `.agents/skills/agent-orchestration/SKILL.md` block K for the
shared task assignment template.

## Codex CLI Delegation

When registry or managed-project instructions delegate work to Codex CLI, the
delegated Codex CLI session must enable and use Codex `goals` for the assigned
task. O2/O1 roles that may delegate must also enable Codex `multi_agent`.

Shared Codex CLI launch authority, MACO boundaries, terminal-leaf prohibitions,
model defaults, and retry/probe rules are owned by
`.agents/skills/agent-orchestration/SKILL.md` blocks F-J.

Add `--ask-for-approval never` only when the installed `codex exec --help`
advertises that flag or an equivalent local compatibility check proves it is
supported. Unsupported approval flags must not be passed unconditionally.

Mention `--dangerously-bypass-approvals-and-sandbox` only as a last-resort
equivalent inside an externally sandboxed or otherwise contained environment.

The prompt and Codex goal must preserve the assigned role, scope, file
ownership, validation duties, banned operations, and final reporting
requirements. If a required goal, multi-agent capability, or permission mode is
unavailable or forbidden, report the exact limitation before continuing with
weaker delegation semantics.

Project-local model and safety rules remain authoritative.
