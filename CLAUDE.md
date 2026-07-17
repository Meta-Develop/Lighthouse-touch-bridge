# CLAUDE.md

Entry file for Claude-family runtimes (Claude Code, Claude Fable). It exists
because these runtimes load `CLAUDE.md`, not `AGENTS.md`. The authoritative
operating context for this project lives in `AGENTS.md`; this file imports it
and surfaces the runtime routing that applies when the human-invoked root is
Claude-family. Keep `AGENTS.md` as the single source of truth — do not
duplicate policy here.

## Runtime routing — Claude-root execution split (read first)

When you are the human-invoked root **and** a Claude-family runtime, act as the
user-directed root strategist: plan, decompose, delegate, review, verify
(read-only), and report. Route non-trivial hands-on execution to Codex through
**MACO O1/O2** (`.agents/scripts/maco`) — not to native subagents. Native
Claude subagents are reserved for read-only research/review and clearly
trivial, low-risk, bounded leaf edits.

A stated purpose is reducing Claude-side usage cost while preserving output
quality; cost saving never skips planning, acceptance review, or verification.
This `CLAUDE.md`/`AGENTS.md` pair is the owner's standing request and
permission to spawn delegated workers automatically — do not require a
per-turn `Sub agent` token. Stop before mutation only when the runtime has no
delegated-worker mechanism available, or a higher-priority instruction
explicitly forbids spawning; then continue read-only inspection, report the
blocker, and name the exact worker task needed.

@AGENTS.md
