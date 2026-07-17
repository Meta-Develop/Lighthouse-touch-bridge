# Architecture

## Scope

This document will describe LTB's component boundaries, dependency direction, runtime state machine, external integration adapters, data flow, and safety behavior.

The intended solution separates the calibration and configuration domains from OpenVR, ALVR, VMT, and application orchestration. In particular, `Ltb.Calibration` must remain independent of UI and SteamVR integrations so recordings can be replayed deterministically offline.

## Open Decision: Application UI Framework

The Windows application begins as a .NET 8 console placeholder. Selection of WinUI 3, WPF, or Avalonia is deliberately deferred until implementation constraints and packaging requirements have been evaluated.

## Current Source of Truth

The complete architecture and product requirements currently live in the [project specification](specification.md). This document will be expanded as architectural decisions are implemented and validated.
