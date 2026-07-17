# Architecture

## Milestone 0 boundary

Milestone 0 proves the mount-calibration mathematics from deterministic,
offline pose arrays. It estimates the fixed tracker-to-controller transform
`X_mount` and reports whether the available motion supports rotation-only or
full 6DoF calibration. Live acquisition and runtime-specific adapters do not
participate in this proof.

The coordinate path below shows the transform direction. Every
`T_parent_child` maps coordinates from the child frame into the parent frame,
so the rightmost transform is applied first.

```text
C -- X_mount = T_T_C --> T -- T_L_T(t) --> L -- Y = T_Q_L --> Q
controller               tracker              Lighthouse       Quest world

T_Q_C(t) = Y * T_L_T(t) * X_mount
```

`Q`, `L`, `T`, and `C` are right-handed Cartesian frames. Rotations use
normalized `System.Numerics.Quaternion` values with components reported in
`XYZW` order. Translation is measured in meters. Sample timestamps are
monotonic host time in seconds, not wall-clock time and not comparable across
host boots.

At runtime the Quest-world transform is absent:

```text
T_L_output(t) = T_L_tracker(t) * X_mount
```

This order rotates the mount translation by the current tracker orientation
before adding the tracker position. It therefore preserves the physical
lever arm in a full 6DoF result; a rotation-only result fixes that translation
to zero.

## Dependency direction

The implemented offline path has the following dependency graph:

```text
Ltb.Core
   ^
   |
Ltb.Calibration <---- Ltb.SyntheticData
   ^                       |
   +--- Ltb.Calibration.Tests
```

- `Ltb.Core` owns rigid transforms, frame and unit conventions, validity
  flags, timestamped pose samples, and synchronized pose pairs. It has no
  project dependency.
- `Ltb.Calibration` depends only on `Ltb.Core`. It contains the deterministic
  hand-eye solver, observability and quality gates, result models, and a
  human-readable report. It has no UI, SteamVR, OpenVR, ALVR, VMT, or
  application dependency.
- `Ltb.SyntheticData` depends on `Ltb.Core` and `Ltb.Calibration`. It generates
  seeded fixtures, invokes the solver, and prints the Milestone 0 console
  report.
- `Ltb.Calibration.Tests` exercises these public contracts and the synthetic
  path. Test-framework packages do not enter the production dependency graph.

Production math uses only .NET and `System.Numerics`; the small symmetric
eigenproblems and translation least-squares solve are implemented inside the
calibration library. Platform projects remain integration boundaries rather
than dependencies of the calibration domain.

## Data contract

`TimestampedPoseSample` combines a finite rigid transform, a non-negative
monotonic timestamp, and independent orientation, position, and tracking-valid
flags. A channel is usable only when its channel flag and tracking-valid flag
are both present. `SynchronizedPosePair` accepts tracker and controller
timestamps within one microsecond and exposes their midpoint as the aligned
time. The solver additionally requires those aligned times to increase
strictly and permits a caller-configured timestamp limit no larger than the
pair-construction tolerance.

Any required stream interpolation or lag estimation must occur before this API
boundary; neither is implemented in Milestone 0. The synthetic tool retains
raw lagged streams for verification, but supplies the solver with pairs aligned
by known simulation truth. This is not an estimate of lag.

## Application boundary and open UI decision

`Ltb.App` remains a console placeholder that references the domain and future
integration projects as an orchestration boundary. No Windows UI framework has
been selected, and Milestone 0 introduces no WinUI, WPF, or Avalonia
dependency. A future framework choice must be recorded here before a UI
dependency is added.

The complete product requirements remain in the [project
specification](specification.md); the implemented calibration details are in
[calibration.md](calibration.md).
