# AGENTS - Lighthouse Touch Bridge

## Project

Lighthouse Touch Bridge is a C#/.NET 8 SteamVR utility that combines Meta Touch
controller inputs with Lighthouse tracker poses. Its calibration core supports
rotation-only and full 6DoF hand-eye mount calibration, with ALVR, VMT, and
SteamVR `TrackingOverrides` handled as integration boundaries.

## Startup

Read these before project-level changes:

- `.agents/skills/agent-files-registry/SKILL.md`
- `.agents/skills/core-agent-skills/SKILL.md`
- `.agents/skills/agent-orchestration/SKILL.md`
- `.agents/docs/PROJECT_RULES.md`
- `.agents/docs/AGENT_ORCHESTRATION.md`
- `.agents/docs/CORE_AGENT_SKILLS.md`
- `.agents/docs/AUTONOMOUS_ISSUE_FEEDBACK.md`
- `docs/specification.md`
- `README.md`

Load relevant skills from `.agents/skills/<name>/SKILL.md`; Codex discovers
that path natively. Load `.agents/skills/autonomous-issue-feedback/SKILL.md`
when work on this owner-maintained project reveals an actionable bug, rough
edge, missing documentation, or feature idea that should become an issue,
autonomous pull request, or local draft.

## Working Rules

- Treat `docs/specification.md` as the complete current product specification.
- Keep coordinate frames, handedness, transform order, units, timestamp
  semantics, and calibration modes explicit and testable.
- Keep `Ltb.Calibration` free of UI, SteamVR, OpenVR, ALVR, VMT, and `Ltb.App`
  dependencies.
- Keep platform-specific integrations behind narrow interfaces so the
  calibration and configuration libraries remain portable.
- The Windows UI framework is deliberately undecided; record the choice in
  `docs/architecture.md` before introducing one.
- Do not commit build outputs, local recordings, SteamVR configuration backups,
  credentials, device identifiers, or owner-local absolute paths.
- Use the `agent_files` registry scripts for `.agents` synchronization and
  shared-feature adoption.

## Delegation

SubAgent/delegated-worker use is mandatory for non-trivial hands-on work. The
Main/coordinator must not directly mutate files, implement patches, generate
durable artifacts, perform integration edits, or run broad fix-up work; assign
that work to delegated workers with explicit ownership.

This `AGENTS.md` is the owner's standing request and permission to use
SubAgent/delegated workers automatically for every non-trivial hands-on task.
Do not require the user to type `Sub agent` or any other per-turn prompt-level
token. A missing per-turn phrase is not a blocker and is not a reason to
continue solo. Stop before mutation only when no delegated-worker mechanism
exists, or when a higher-priority instruction explicitly forbids delegation;
report the exact blocked worker task.

Hands-on work is allowed only for agents explicitly launched as delegated
workers with narrow ownership. If no approved delegated-worker path exists,
the Main/coordinator may continue read-only inspection and status reporting but
must stop before mutation.

## Validation

- Run `dotnet build` from the repository root.
- Run `dotnet test` from the repository root.
- For agent-context changes, run `scripts/validate-agent`,
  `scripts/check-feature-updates Lighthouse-touch-bridge`, and
  `scripts/sync-agent-state <live-project-dir>` from the `agent_files`
  registry.
- Record Windows-only integration checks separately when Linux or WSL cannot
  exercise the required SteamVR runtime or desktop workload.
