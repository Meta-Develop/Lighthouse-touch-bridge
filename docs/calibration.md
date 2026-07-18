# Offline Calibration

## Problem and coordinate model

A static tracker/controller pose cannot distinguish the unknown relationship
between the Quest and Lighthouse worlds from the fixed tracker mount. The
offline solver removes that world transform by comparing motion between
synchronized samples.

```text
C -- X_mount = T_T_C --> T -- T_L_T(i) --> L -- Y = T_Q_L --> Q
controller               tracker              Lighthouse       Quest world

T_Q_C(i) = Y * T_L_T(i) * X_mount
runtime: T_L_output(t) = T_L_tracker(t) * X_mount
```

The notation `T_parent_child` means a transform from child coordinates to
parent coordinates. `Q`, `L`, `T`, and `C` are right-handed frames. Rotations
are normalized `System.Numerics` quaternions reported in `XYZW` component
order, positions are in meters, and timestamps are monotonic host seconds.

## Staged solve

The input is an ordered array of already synchronized tracker/controller pose
pairs. Orientation-valid sample indices are divided first into disjoint solve
and held-out validation sets; relative motions are then constructed only
within their respective sample sets. This sample-level split prevents one
source pose from contributing to both fitting and validation.

For two samples `i` and `j`, the solver forms

```text
A = T_L_T(i)^-1 * T_L_T(j)
B = T_Q_C(i)^-1 * T_Q_C(j)
A * X_mount = X_mount * B
```

Motion pairs below the configured angular separation are discarded. Evenly
spaced anchor samples and a bounded pair count retain both local and
recording-wide motion baselines.

### Rotation

The rotation equation is

```text
R_A * R_X = R_X * R_B.
```

For each motion, the implementation adds
`L(q_A) - R(q_B)` to a quaternion homogeneous least-squares system. The unit
eigenvector of the smallest normal-matrix eigenvalue gives `R_X`; its sign is
canonicalized without changing the represented rotation. A residual-based
robust pass removes large rotation outliers and resolves the system.

Rotation observability is measured from the motion-axis tensor. The ratio of
its second-largest to largest eigenvalue must exceed the configured coverage
threshold, which rejects single-axis captures. Held-out geodesic residual RMS,
percentile, and robust inlier ratio then determine whether rotation is
accepted. Static motion, pure translation, insufficient orientation samples,
single-axis motion, timestamp violations, or failed held-out quality produce a
failed calibration. Auto mode does not conceal a failed rotation solve.

### Translation

After rotation passes, position-valid relative motions provide the linear
equation

```text
(R_A - I) * t_X = R_X * t_B - t_A.
```

The equations are stacked and solved by least squares while the accepted
`R_X` remains fixed. A robust residual pass can remove position outliers. The
translation normal matrix must have sufficient minimum eigenvalue and an
acceptable condition number. Two independent subsets of the solve samples
must also produce translations that agree within the stability threshold, and
the final magnitude must pass the physical plausibility gate.

On held-out position-valid motions, the full solution is compared with the
same accepted rotation and `t_X = 0`. Full 6DoF is accepted only when its
robust inlier ratio and position RMS pass and its absolute and fractional RMS
improvements both exceed their configured margins.

## Model selection and reported degeneracy

- `RotationOnly` validates rotation and always returns zero translation.
- `FullSixDof` requires every translation observability and quality gate; a
  translation rejection makes the calibration fail.
- `Auto` returns full 6DoF when all translation gates pass. If rotation is
  valid but translation is missing, unobservable, ill-conditioned, unstable,
  implausible, or fails held-out position quality, it returns the accepted
  rotation with zero translation and records the fallback reason.

The result separates rotation and translation observability and records a
machine-readable degeneracy reason. It also reports sample and motion-pair
counts, motion-axis coverage, translation condition number, rotation and
position residuals, robust inlier ratios, translation split disagreement, and
the selected `X_mount`.

## Configurable quality gates

`CalibrationOptions` owns the initial Milestone 0 defaults below. These values
are configuration, not permanent hardware-tuned constants.

| Gate | Default |
| --- | ---: |
| Minimum samples | 8 |
| Maximum selected motion pairs | 256 |
| Maximum paired timestamp difference | 1 microsecond |
| Accepted relative rotation | 2 to 170 degrees |
| Minimum motion-axis coverage | 0.04 |
| Maximum held-out rotation RMS | 2.5 degrees |
| Residual percentile | 95th |
| Held-out sample fraction | 25% |
| Minimum position-valid fraction | 60% |
| Minimum translation normal-matrix eigenvalue | `1e-4` |
| Maximum translation condition number | 500 |
| Maximum split translation disagreement | 5 mm |
| Maximum mount translation | 0.5 m |
| Maximum held-out position RMS | 40 mm |
| Minimum position RMS improvement | 0.5 mm and 2% |
| Minimum held-out inlier ratio | 70% |

## Deterministic synthetic validation

`Ltb.SyntheticData` generates paired streams from a known `X_mount`, an
arbitrary `Y = T_Q_L`, and the calibration composition equation. The random
seed is explicit and identical options reproduce identical streams and model
selection. The generator supports rotation and position noise, dropped
samples, variable rates, timestamp jitter, quaternion sign flips, pose
outliers, tracking-invalid samples, and partial controller-position validity.

The built-in scenarios are:

- `clean`: exciting multi-axis motion without injected measurement noise;
- `noisy`: seeded rotation and position noise, timestamp jitter, drops,
  variable sample intervals, and quaternion sign flips;
- `static`, `single-axis`, and `pure-translation`: rotation-degenerate captures
  that must fail rather than produce a transform;
- `translation-degenerate`: multi-axis rotation with insufficient valid
  controller positions, which allows Auto to demonstrate rotation-only
  fallback.

Raw synthetic controller timestamps contain the configured known lag (12 ms by
default). `AlignedPairs` instead assign tracker and controller the same
simulation-truth timestamp after applying that known lag. Consequently the
tests validate calibration after alignment and the raw-stream truth boundary;
they do not claim lag estimation.

## Command-line report

Run an end-to-end seeded scenario from the repository root:

```bash
dotnet run --project tools/Ltb.SyntheticData -- --scenario noisy --seed 20260717 --policy auto
```

Supported scenarios are `clean`, `noisy`, `static`, `single-axis`,
`pure-translation`, and `translation-degenerate`. Policies are `auto`,
`rotation`, and `full`. The report prints the verdict, requested and selected
models, selection reason, known-lag alignment boundary, truth and estimated
mounts, errors and residuals, observability and degeneracy verdicts, and sample
and injection counts. A failed calibration returns a nonzero process exit
code; an Auto rotation-only fallback is a successful reported outcome.

## Two-hand guided calibration

Milestone 3 composes the existing portable stages without forking their
numeric logic. For each hand, guided capture reports:

- orientation/tracking-valid and position-valid sample fractions;
- accumulated rotation and coordinate-invariant motion-axis coverage;
- separate rotation-ready and position-ready progress; and
- whether the rotation capture gate is accepted.

The left and right gestures are recorded separately while both candidate
tracker streams remain visible. `TrackerHandAssociator` compares angular-speed
magnitude, estimates lag for every viable hand/tracker candidate, and solves a
one-to-one serial assignment. It rejects disconnected or repeatedly invalid
candidates, weak correlation, ambiguous assignments, and inconsistent
left/right lag. Runtime device order and world-space direction never enter the
decision; a corrected swapped input order is reported explicitly.

After association, `PerHandCalibrationPipeline` runs the assigned raw streams
through `StreamLagEstimator`, `PoseStreamAligner`, and
`HandEyeCalibrationSolver` with `CalibrationPolicy.Auto`. The solver still
validates rotation first and holds it fixed while attempting translation.
Consequently:

- a bad rotation solve is a failed calibration and asks for another capture;
- missing controller position is a successful rotation-only fallback;
- insufficient translation observability or validation improvement is also a
  successful rotation-only fallback; and
- full 6DoF is selected only when all existing translation gates pass.

The quality report is per hand and retains lag, motion-axis coverage, rotation
RMS and percentile, position RMS and percentile where available, the
rotation-only position RMS comparison, translation condition, separate
rotation and translation inlier ratios, translation magnitude, split
disagreement, rotation/translation observability and degeneracy, and the exact
selection reason. These complete fields are emitted directly from the genuine
first-run `CalibrationResult`; the application does not recalculate quality or
select a model. Schema 1 retains a smaller quality subset. Later-run profile
reuse therefore reports only persisted schema fields and never copies RMS or
the one persisted inlier ratio into missing percentile or separate-inlier
fields.

Profiles are keyed by tracker serial and semantic hand. A rotation-only profile
stores zero translation plus its fallback reason. A later run loads a complete
left/right pair by exact serial-and-hand match and applies it without capture;
a missing or mismatched side keeps the wizard on the first-run path. The
runtime also passes explicit-request, observed hand association, remount,
validation-threshold, controller runtime/model, expected-schema, and transform-
convention observations to `RecalibrationEvaluator`; the application does not
reimplement those trigger rules. A recognized older/newer stored schema routes
to capture and replacement, while structurally malformed profile JSON stops
safely. Profile schema validation, deterministic JSON, and atomic save are
owned by `Ltb.Configuration`.

For production `daily` reuse, the runtime/model observation comes from the
current fail-closed ALVR gate, not from the stored profile: the local ALVR
version endpoint must respond and the current OpenVR controllers must pass the
Quest 2 Touch Miramar/`oculus_touch` tuple. Only then does the runtime report
`ALVR` and `Quest 2 Touch` for comparison with the stored values.

## Scripted wizard command

Run the Linux-safe deterministic two-hand flow from the repository root:

```bash
dotnet run --project src/Ltb.App -- wizard-demo --profiles <profile-store.json> [--log <events.jsonl>]
```

The fake session uses `CTRL-TEST-L`, `CTRL-TEST-R`, `LHR-TEST0001`, and
`LHR-TEST0002`. Tracker enumeration is reversed deliberately. The left stream
has valid controller position and selects full 6DoF; the right stream has valid
orientation but no controller position and selects normal rotation-only Auto
fallback. Run the command again against the same store to exercise serial-and-
hand reload and the no-capture apply path. Each hand emits deterministic
progressive coverage snapshots from growing sample prefixes, all evaluated by
the portable analyzer.

`--log` uses the same append-only JSON Lines sink as `daily`. Reusing a path
appends a new event sequence; omitting the option creates no event file. The
wizard records its state transitions and preserves separate codes for missing
controller position, poor translation observability, and bad rotation:
`NoPositionAvailable`, `PoorTranslationObservability`, and
`BadRotationCalibration`.

## Calibration diagnostics and runtime safety

Version 0.1 preserves four distinct observations because they require
different user actions and state transitions:

| Observation | Calibration meaning | State consequence |
| --- | --- | --- |
| Controller position unavailable | Rotation remains solvable, but translation has no input data | Successful Auto rotation-only profile with zero translation |
| Poor translation observability | Position exists, but the motion does not constrain a reliable lever arm | Successful Auto rotation-only profile with zero translation |
| Bad rotation calibration | The fixed mount rotation is unsupported or fails validation | Calibration failure; return to `Ready` with a retry diagnostic |
| Tracker lost | An active runtime pose source is unsafe or absent | `SafeDisable`, then wait for stable-serial reacquisition |

The first two results are normal model-selection outcomes. They retain the
accepted rotation and record different machine-readable fallback reasons. The
third result must not be converted into rotation-only success because every
runtime composition depends on a valid mount rotation. The fourth is not a
solver result at all: it is reported by runtime health monitoring and must not
be described as weak calibration data.

This distinction also applies to structured events. Calibration events record
capture, observability, validation, and selected-mode results. Runtime events
record source loss, SafeDisable, reacquisition, reapplication, and cleanup.
Tests and support tools should compare stable event codes and result fields
rather than infer the category from free-form prose.

## Recalibration, reuse, and rollback

A later run can reuse a profile only after exact stable-serial and semantic-hand
matching and the recalibration checks described above. Reconnect does not alter
the stored transform or bind it to a new transient OpenVR index. After the
required device returns, the daily-use coordinator passes through
`Ready -> ApplyProfile -> Active` again.

Failed recalibration does not make a partial capture the new active profile.
Both newly calibrated hands must validate before the profile store is replaced,
and both runtime applications must succeed before the pair is reported active.
If one application fails, the coordinator rolls back effects created by that
attempt. Rollback or cleanup failure is a runtime diagnostic requiring manual
inspection; it is not a reason to accept a lower-quality calibration.

## Current limitations

The portable pipeline does not perform optional joint nonlinear refinement.
The `wizard-demo` command remains the deterministic fake demonstration path;
it proves orchestration, association, selection, reporting, persistence, and
reload on Linux without a live SteamVR runtime. The production `wizard` command
composes the live pipeline (override release -> Touch capture -> association ->
solve -> persist -> transactional apply -> Active) and is proven end-to-end
through injected fake production backends; its live execution still awaits
Windows hardware verification. The production `daily` command can
load an already complete two-hand store and apply it transactionally through
live OpenVR, VMT, and SteamVR-settings adapters, including watchdog,
SafeDisable, reconnect, and rollback policy. Automated transition tests use
fakes, so the Windows checklist remains required hardware acceptance for that
live later-run composition. Avalonia 11 is the selected desktop framework. The
GUI keeps the deterministic scripted demonstration available and invokes the
production wizard through the shared `Ltb.App` composition seam; native launch,
visual behavior, and live SteamVR hardware operation still require the Windows
verification checklist.
