# Calibration

## Scope

This document will explain pose acquisition, tracker-to-hand association, stream time alignment, rotation-only and full 6DoF hand-eye calibration, observability checks, quality gates, and Auto-mode fallback behavior.

It will also document coordinate conventions, recording formats, solver validation, synthetic test cases, and the boundary between the calibration core and runtime-specific adapters.

## Current Source of Truth

The calibration model and requirements currently live in the [project specification](specification.md). This document will be expanded alongside the offline calibration proof and live recorder milestones.
