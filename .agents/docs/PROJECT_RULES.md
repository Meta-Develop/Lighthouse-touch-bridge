# Project Rules - Lighthouse Touch Bridge

## Source of Truth

- `docs/specification.md` is the complete current product specification.
- `LighthouseTouchBridge.sln` and the projects under `src/`, `tests/`, and
  `tools/` define the implemented system.
- `.agents/` contains project-maintenance context, not product requirements.

## Project Boundary

- Lighthouse Touch Bridge combines Meta Touch controller inputs with
  Lighthouse tracker poses for SteamVR through ALVR, VMT, and
  `TrackingOverrides` orchestration.
- Keep `Ltb.Calibration` portable, deterministic, and independent of UI,
  SteamVR, OpenVR, ALVR, VMT, and `Ltb.App` dependencies.
- Treat coordinate frames, handedness, transform order, units, timestamps, and
  calibration-mode assumptions as contract-bearing.
- The Windows UI framework remains an open architecture decision. Do not add a
  WinUI 3, WPF, or Avalonia dependency without recording the decision in
  `docs/architecture.md`.

## Validation

- Run `dotnet build` from the repository root.
- Run `dotnet test` from the repository root.
- Document any Windows-only workload or runtime validation that cannot run
  under Linux or WSL.

## Safety

- Do not commit SteamVR configuration backups, device identifiers, credentials,
  local recordings, build outputs, or owner-local absolute paths.
- Keep generated and temporary files in ignored locations unless they are
  intentionally curated as durable fixtures or documentation.
